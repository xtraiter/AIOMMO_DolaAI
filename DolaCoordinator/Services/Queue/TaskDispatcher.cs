using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Profiles;
using DolaCoordinator.Services.Downloader;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Sessions;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Queue;

/// <summary>
/// Điều phối: mỗi prompt vào MỘT tài khoản (profile) còn hạn ngạch; nếu tài khoản đó không tạo được
/// (hết lượt/credit, mất đăng nhập, risk control, chat hỏi thăm không được trả lời) thì tự đổi sang tài khoản khác.
/// Gateway làm phần tự động hóa trên Dola (hỏi thăm → chat mới → nhập cấu hình → chờ video → tải).
/// </summary>
public class TaskDispatcher : ITaskDispatcher, IDisposable
{
    private static readonly TimeSpan WaitLogInterval = TimeSpan.FromSeconds(30);
    private const int MaxPollErrors = 12; // ~1 phút mất kết nối liên tục thì mới bỏ cuộc

    private readonly IDolaGatewayClient _gatewayClient;
    private readonly IAssetDownloader _assetDownloader;
    private readonly IDatabaseService _databaseService;
    private readonly IQuotaTracker _quotaTracker;
    private readonly ISecurityService _securityService;
    private readonly IAccountProfileService _profiles;
    private readonly IGatewayHost _gatewayHost;

    private readonly Channel<RenderTask> _taskChannel = Channel.CreateUnbounded<RenderTask>();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly HashSet<string> _activeSessionIds = new();
    private readonly object _sessionLock = new();
    private readonly object _claimLock = new();
    private readonly HashSet<string> _queuedIds = new(); // id đang nằm trong hàng đợi (channel): không xếp trùng

    private CancellationTokenSource? _dispatcherCts;
    private Task? _dispatcherLoopTask;
    private int _roundRobinIndex = 0;
    private int _activeWorkersCount = 0;

    public event Action<string>? LogReceived;
    public event Action<RenderTask>? TaskUpdated;
    public event Action? AllTasksCompleted;

    public bool IsRunning => _dispatcherLoopTask != null && !_dispatcherLoopTask.IsCompleted;
    public int ActiveWorkersCount => _activeWorkersCount;

    public TaskDispatcher(
        IDolaGatewayClient gatewayClient,
        IAssetDownloader assetDownloader,
        IDatabaseService databaseService,
        IQuotaTracker quotaTracker,
        ISecurityService securityService,
        IAccountProfileService profiles,
        IGatewayHost gatewayHost)
    {
        _gatewayClient = gatewayClient;
        _assetDownloader = assetDownloader;
        _databaseService = databaseService;
        _quotaTracker = quotaTracker;
        _securityService = securityService;
        _profiles = profiles;
        _gatewayHost = gatewayHost;
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogReceived?.Invoke($"[{timestamp}] {message}");
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _dispatcherCts?.Cancel();
        _dispatcherCts?.Dispose();
        _dispatcherCts = new CancellationTokenSource();
        _dispatcherLoopTask = Task.Run(() => DispatcherLoopAsync(_dispatcherCts.Token), CancellationToken.None);
        Log("Bộ điều phối hàng đợi tác vụ đã khởi động.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _dispatcherCts?.Cancel();
        foreach (var (_, cts) in _runningTasks)
        {
            try { cts.Cancel(); cts.Dispose(); } catch (ObjectDisposedException) { }
        }
        _runningTasks.Clear();

        if (_dispatcherLoopTask != null)
        {
            try
            {
                await _dispatcherLoopTask;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[Lỗi Dừng Dispatcher] {ex.Message}");
            }
            _dispatcherLoopTask = null;
        }

        _dispatcherCts = null;
        Log("Bộ điều phối đã dừng hoạt động.");
    }

    /// <summary>Đưa tác vụ vào hàng đợi; trả về false nếu nó đã nằm sẵn trong hàng đợi (không xếp trùng).</summary>
    private bool TryQueue(RenderTask task)
    {
        lock (_queuedIds)
        {
            if (!_queuedIds.Add(task.Id)) return false;
        }
        return _taskChannel.Writer.TryWrite(task);
    }

    public void EnqueueTask(RenderTask task)
    {
        task.Status = RenderTaskStatus.Pending;
        task.ProgressPercent = 0;
        _databaseService.UpsertTask(task);
        if (!TryQueue(task)) return; // đã có trong hàng đợi
        TaskUpdated?.Invoke(task);
        Log($"Đã thêm tác vụ vào hàng đợi: {task.DisplayPrompt}");
    }

    public void EnqueueTasks(IEnumerable<RenderTask> tasks)
    {
        foreach (var task in tasks)
        {
            EnqueueTask(task);
        }
    }

    public async Task RetryTaskAsync(string taskId)
    {
        var task = _databaseService.GetTaskById(taskId);
        if (task == null) return;

        // Cancel running instance if any
        if (_runningTasks.TryRemove(taskId, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch (ObjectDisposedException) { }
        }

        task.Status = RenderTaskStatus.Pending;
        task.ErrorMessage = null;
        task.StageText = null;
        task.ProgressPercent = 0;
        task.RetryCount = 0; // Reset retry count for fresh attempts
        _databaseService.UpsertTask(task);
        TryQueue(task);
        TaskUpdated?.Invoke(task);
        Log($"Yêu cầu thử lại tác vụ: {task.DisplayPrompt}");

        if (!IsRunning)
        {
            await StartAsync();
        }
    }

    public void CancelTask(string taskId)
    {
        if (_runningTasks.TryRemove(taskId, out var cts))
        {
            try { cts.Cancel(); cts.Dispose(); } catch (ObjectDisposedException) { }
        }

        var task = _databaseService.GetTaskById(taskId);
        if (task != null)
        {
            task.Status = RenderTaskStatus.Cancelled;
            _databaseService.UpsertTask(task);
            TaskUpdated?.Invoke(task);
            Log($"Đã hủy tác vụ: {task.DisplayPrompt}");
        }
    }

    // ------------------------------------------------------------------ chọn tài khoản

    private sealed record PickResult(DolaSession? Session, bool MustWait, string Summary);

    /// <summary>
    /// Chọn tài khoản cho một lượt: còn hạn ngạch của app, chưa thử lỗi cho tác vụ này, không bận, không đang mở profile
    /// và gateway không chặn (hết lượt ngày / hết credit / cooldown / mất đăng nhập / tắt lập lịch).
    /// </summary>
    private async Task<PickResult> PickSessionAsync(HashSet<string> tried, CancellationToken ct)
    {
        // Trạng thái thật do gateway giữ; null = gateway chưa chạy (khi đó chỉ dựa vào dữ liệu của app)
        var gatewayAccounts = await _gatewayClient.GetAccountsAsync(ct);
        var gateway = gatewayAccounts?.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        lock (_sessionLock)
        {
            var sessions = _databaseService.GetAllSessions();
            _quotaTracker.EnsureDailyQuotaReset(sessions);

            // Tài khoản có thể phục vụ tác vụ này (kể cả khi tạm bận): loại các chặn "cứng" trong ngày
            var potential = sessions
                .Where(s => s.IsSchedulable && !tried.Contains(s.Id) && !IsHardBlocked(s, gateway))
                .ToList();
            if (potential.Count == 0)
                return new PickResult(null, false, Summarize(sessions, gateway, tried));

            var ready = potential
                .Where(s => !_activeSessionIds.Contains(s.Id)
                            && !_profiles.IsAccountOpen(s.Name) // profile đang mở → Chromium khóa thư mục, gateway không dùng được
                            && !IsBusyOrCooling(s, gateway))
                .ToList();
            if (ready.Count == 0)
                return new PickResult(null, true, string.Empty);

            _roundRobinIndex %= ready.Count;
            var selected = ready[_roundRobinIndex];
            _roundRobinIndex = (_roundRobinIndex + 1) % ready.Count;
            _activeSessionIds.Add(selected.Id);
            return new PickResult(selected, false, string.Empty);
        }
    }

    private static GatewayAccountDto? MetaOf(DolaSession s, Dictionary<string, GatewayAccountDto>? gateway)
        => gateway != null && gateway.TryGetValue(s.Name, out var meta) ? meta : null;

    /// <summary>Gateway chặn trong cả ngày hoặc cho tới khi có người can thiệp: chờ cũng vô ích.</summary>
    private static bool IsHardBlocked(DolaSession s, Dictionary<string, GatewayAccountDto>? gateway)
    {
        if (gateway == null) return false;
        var meta = MetaOf(s, gateway);
        if (meta == null) return true; // thư mục accounts/<tên> không còn trong gateway
        return !meta.Scheduling || meta.RateLimited || meta.QuotaBlocked || meta.LoginOk == false;
    }

    /// <summary>Chặn tạm thời: đang render việc khác hoặc đang cooldown.</summary>
    private static bool IsBusyOrCooling(DolaSession s, Dictionary<string, GatewayAccountDto>? gateway)
    {
        var meta = MetaOf(s, gateway);
        return meta != null && (meta.Busy || meta.Cooling);
    }

    private static string Summarize(List<DolaSession> sessions, Dictionary<string, GatewayAccountDto>? gateway, HashSet<string> tried)
    {
        if (sessions.Count == 0) return "Chưa có tài khoản nào. Hãy thêm tài khoản ở tab Dola Super.";

        var reasons = new Dictionary<string, int>();
        void Count(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

        foreach (var s in sessions)
        {
            var meta = MetaOf(s, gateway);
            if (!s.IsEnabled) Count("đã tắt");
            else if (s.Status == SessionStatus.Invalid) Count("cần đăng nhập lại");
            else if (s.Status == SessionStatus.Unknown || s.Status == SessionStatus.Validating) Count("chưa kiểm tra phiên");
            else if (s.Status == SessionStatus.Exhausted || s.UsedToday >= s.DailyLimit) Count("hết hạn ngạch hôm nay");
            else if (gateway != null && meta == null) Count("không còn trong gateway");
            else if (meta is { LoginOk: false }) Count("gateway báo mất đăng nhập");
            else if (meta is { RateLimited: true }) Count("gateway báo hết lượt trong ngày");
            else if (meta is { QuotaBlocked: true }) Count("hết credit");
            else if (meta is { Scheduling: false }) Count("tắt lập lịch");
            else if (tried.Contains(s.Id)) Count("đã thử nhưng lỗi");
        }

        return "Không còn tài khoản khả dụng: " + string.Join(", ", reasons.Select(kv => $"{kv.Value} {kv.Key}")) + ".";
    }

    private void ReleaseActive(string sessionId)
    {
        lock (_sessionLock)
        {
            _activeSessionIds.Remove(sessionId);
        }
    }

    /// <summary>Báo UI nạp lại hạn ngạch/trạng thái các tài khoản.</summary>
    private void NotifyProfilesChanged()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var profilesVm = (System.Windows.Application.Current.MainWindow?.DataContext as ViewModels.MainViewModel)?.ProfilesVm;
            profilesVm?.ReloadSessions();
        });
    }

    // ------------------------------------------------------------------ vòng điều phối

    private async Task DispatcherLoopAsync(CancellationToken ct)
    {
        var settings = _databaseService.GetSettings();
        using var concurrencySemaphore = new SemaphoreSlim(settings.ConcurrencyLimit);

        while (!ct.IsCancellationRequested)
        {
            RenderTask task;
            try
            {
                task = await _taskChannel.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            lock (_queuedIds)
            {
                _queuedIds.Remove(task.Id);
            }

            // Nhận việc đúng MỘT lần: một tác vụ có thể nằm trong hàng đợi nhiều lần (thêm rồi bấm Bắt đầu, dừng rồi chạy lại,
            // thử lại...). Chỉ chạy khi nó còn "Pending"; đã hủy / đang chạy ở worker khác / đã xong thì bỏ qua,
            // nếu không cùng một prompt sẽ tạo hai video và tốn gấp đôi credit.
            RenderTask? currentTask;
            lock (_claimLock)
            {
                currentTask = _databaseService.GetTaskById(task.Id);
                if (currentTask == null || currentTask.Status != RenderTaskStatus.Pending)
                {
                    continue;
                }
                currentTask.Status = RenderTaskStatus.Queued;
                _databaseService.UpsertTask(currentTask);
            }
            TaskUpdated?.Invoke(currentTask);

            await concurrencySemaphore.WaitAsync(ct);
            Interlocked.Increment(ref _activeWorkersCount);

            _ = Task.Run(async () =>
            {
                var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _runningTasks[currentTask.Id] = taskCts;

                try
                {
                    await ProcessSingleTaskAsync(currentTask, taskCts.Token);
                }
                finally
                {
                    _runningTasks.TryRemove(currentTask.Id, out _);
                    concurrencySemaphore.Release();
                    Interlocked.Decrement(ref _activeWorkersCount);

                    if (_activeWorkersCount == 0 && _taskChannel.Reader.Count == 0)
                    {
                        AllTasksCompleted?.Invoke();
                    }
                }
            }, CancellationToken.None);
        }
    }

    private void FailTask(RenderTask task, string message)
    {
        task.Status = RenderTaskStatus.Failed;
        task.ErrorMessage = message;
        task.StageText = null;
        task.FinishedAt = DateTime.UtcNow;
        _databaseService.UpsertTask(task);
        TaskUpdated?.Invoke(task);
        Log($"[Thất bại] {task.DisplayPrompt}: {message}");
    }

    private async Task ProcessSingleTaskAsync(RenderTask task, CancellationToken ct)
    {
        var settings = _databaseService.GetSettings();
        var tried = new HashSet<string>();   // tài khoản đã lỗi cho tác vụ này: không chọn lại
        var lastWaitLog = DateTime.MinValue;

        // Ảnh tham chiếu là file trên máy: kiểm tra còn tồn tại trước khi tốn công chọn tài khoản
        var missing = task.ReferenceLocalPaths.FirstOrDefault(p => !File.Exists(p));
        if (missing != null)
        {
            FailTask(task, $"Ảnh tham chiếu không còn tồn tại: {missing}");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            PickResult pick;
            try
            {
                // Gateway chưa chạy thì bật ngầm ngay bây giờ (chỉ khi có việc cần làm)
                var host = await _gatewayHost.EnsureRunningAsync(ct);
                if (!host.Ok)
                {
                    FailTask(task, $"Không bật được gateway: {host.Error}");
                    return;
                }
                pick = await PickSessionAsync(tried, ct);
            }
            catch (OperationCanceledException)
            {
                MarkCancelled(task);
                return;
            }

            if (pick.Session == null)
            {
                if (pick.MustWait)
                {
                    if (DateTime.Now - lastWaitLog > WaitLogInterval)
                    {
                        Log($"[Chờ Tài Khoản] Các tài khoản còn hạn ngạch đang bận / đang mở profile / cooldown. Chờ để điều phối: {task.DisplayPrompt}");
                        lastWaitLog = DateTime.Now;
                    }
                    try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                    catch (OperationCanceledException) { MarkCancelled(task); return; }
                    continue;
                }

                FailTask(task, pick.Summary);
                return;
            }

            var session = pick.Session;
            var accepted = false; // gateway đã nhận tác vụ → coi là đã tiêu một lượt của tài khoản
            try
            {
                task.AssignedSessionId = session.Id;
                task.AssignedSessionName = session.Name;
                task.Status = RenderTaskStatus.Queued;
                task.StageText = "Đang chuyển cho gateway";
                task.StartedAt ??= DateTime.UtcNow;
                _databaseService.UpsertTask(task);
                TaskUpdated?.Invoke(task);

                // Tính quota ngay khi giao việc để hai tác vụ song song không cùng chiếm lượt cuối; hoàn lại nếu chưa tốn
                _quotaTracker.IncrementUsedQuota(session.Id);
                NotifyProfilesChanged();

                Log($"[{session.Name}] Bắt đầu điều phối: \"{task.DisplayPrompt}\" ({task.Duration}s, {task.Ratio}" +
                    (task.ReferenceLocalPaths.Count > 0 ? $", {task.ReferenceLocalPaths.Count} ảnh tham chiếu" : "") + ")");

                var sessionToken = session.PlainToken ?? _securityService.Decrypt(session.EncryptedToken);
                var req = new VideoGenApiRequest
                {
                    Model = task.Model,
                    Prompt = task.Prompt,
                    Ratio = task.Ratio,
                    Duration = task.Duration,
                    ReferenceImages = task.ReferenceImages,
                    ReferenceLocalPaths = task.ReferenceLocalPaths,
                    Account = session.Name,
                    Cookie = sessionToken
                };

                var createResp = await CreateOnGatewayAsync(req, settings.ClientApiKey, ct);
                if (createResp == null || string.IsNullOrWhiteSpace(createResp.Id))
                    throw new InvalidOperationException("Gateway không trả về Task ID hợp lệ.");
                accepted = true;

                task.GatewayTaskId = createResp.Id;
                task.Status = RenderTaskStatus.Processing;
                task.ProgressPercent = 15;
                _databaseService.UpsertTask(task);
                TaskUpdated?.Invoke(task);
                Log($"[{task.GatewayTaskId}] Gateway đã nhận tác vụ, theo dõi tiến trình...");

                var completed = await PollUntilCompleteAsync(task, session, settings.PollingIntervalSeconds, ct);
                if (completed == null || string.IsNullOrWhiteSpace(completed.VideoUrl))
                    throw new GatewayTaskFailedException(completed?.Error ?? "Render thất bại trên gateway", completed?.FailureCode);

                task.VideoUrl = completed.VideoUrl;
                task.ProgressPercent = 85;
                task.StageText = "Đang tải video về máy";
                task.Status = RenderTaskStatus.Downloading;
                _databaseService.UpsertTask(task);
                TaskUpdated?.Invoke(task);
                Log($"[{task.GatewayTaskId}] Video đã render xong! Bắt đầu tải về máy...");

                var progress = new Progress<int>(pct =>
                {
                    task.ProgressPercent = pct;
                    TaskUpdated?.Invoke(task);
                });

                var localPath = await DownloadWithRetryAsync(task, task.VideoUrl, progress, ct);
                task.StageText = null;
                Log($"[Thành Công] [{session.Name}] Đã lưu: {localPath}");
                TaskUpdated?.Invoke(task);
                return;
            }
            catch (OperationCanceledException)
            {
                if (!accepted) _quotaTracker.ReleaseQuota(session.Id);
                NotifyProfilesChanged();
                MarkCancelled(task);
                return;
            }
            catch (GatewayTaskFailedException gx)
            {
                if (!HandleGatewayFailure(task, session, gx, tried))
                {
                    FailTask(task, gx.Message);
                    return;
                }
                // đã đánh dấu tài khoản lỗi → vòng lặp chọn tài khoản khác
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TimeoutException)
            {
                // Lỗi mạng / gateway tạm thời: nếu chưa được nhận thì chưa tốn lượt
                if (!accepted) _quotaTracker.ReleaseQuota(session.Id);
                NotifyProfilesChanged();

                task.RetryCount++;
                task.ErrorMessage = ex.Message;
                Log($"[Lỗi Tác Vụ] {ex.Message} (Lần thử {task.RetryCount}/{task.MaxRetries})");

                if (task.RetryCount > task.MaxRetries || !settings.AutoRetryOnFailure)
                {
                    FailTask(task, ex.Message);
                    return;
                }

                var delaySec = task.RetryCount * 5; // exponential-ish backoff
                Log($"Chờ {delaySec}s trước khi tự động thử lại tác vụ...");
                try { await Task.Delay(TimeSpan.FromSeconds(delaySec), ct); }
                catch (OperationCanceledException) { MarkCancelled(task); return; }
            }
            finally
            {
                ReleaseActive(session.Id);
            }
        }

        MarkCancelled(task);
    }

    /// <summary>
    /// Tải video đã render xong. Lỗi tải chỉ thử tải lại từ cùng URL; tuyệt đối không gửi lại prompt
    /// (video đã được tạo và đã tốn credit).
    /// </summary>
    private async Task<string> DownloadWithRetryAsync(RenderTask task, string url, IProgress<int> progress, CancellationToken ct)
    {
        const int attempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _assetDownloader.DownloadVideoAsync(task, url, progress, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                    throw new GatewayTaskFailedException(
                        $"Video đã tạo xong nhưng tải về lỗi: {ex.Message}. Địa chỉ video: {url}", "download");

                Log($"[Tải Video] Lỗi ({ex.Message}); thử tải lại lần {attempt + 1}/{attempts}...");
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct);
            }
        }
    }

    private void MarkCancelled(RenderTask task)
    {
        task.Status = RenderTaskStatus.Cancelled;
        task.ErrorMessage = "Người dùng đã hủy tác vụ.";
        task.StageText = null;
        _databaseService.UpsertTask(task);
        TaskUpdated?.Invoke(task);
    }

    /// <summary>Gọi gateway tạo tác vụ; các lỗi HTTP có ý nghĩa rõ ràng được đổi thành GatewayTaskFailedException.</summary>
    private async Task<TaskApiResponse?> CreateOnGatewayAsync(VideoGenApiRequest req, string? apiKey, CancellationToken ct)
    {
        try
        {
            return await _gatewayClient.CreateVideoTaskAsync(req, apiKey, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("(429)"))
        {
            // Toàn bộ pool gateway đã hết lượt/credit: đổi tài khoản cũng không giúp
            throw new GatewayTaskFailedException($"Gateway từ chối: {ex.Message}", "429");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("(401)") || ex.Message.Contains("(403)") || ex.Message.Contains("(422)"))
        {
            // Sai API key / dữ liệu không hợp lệ (vd. ảnh tham chiếu): thử lại hay đổi tài khoản đều vô ích
            throw new GatewayTaskFailedException($"Gateway từ chối: {ex.Message}", "rejected");
        }
    }

    /// <summary>
    /// Xử lý lỗi gateway theo failure_code. Trả về true nếu nên đổi sang tài khoản khác, false nếu nên dừng tác vụ.
    /// </summary>
    private bool HandleGatewayFailure(RenderTask task, DolaSession session, GatewayTaskFailedException gx, HashSet<string> tried)
    {
        var fresh = _databaseService.GetSessionById(session.Id) ?? session;
        bool switchAccount;

        switch (gx.FailureCode)
        {
            case "account_limited":
                // Dola báo hết lượt tạo video trong ngày: khóa tài khoản tới lúc reset, không hoàn lượt
                fresh.UsedToday = Math.Max(fresh.UsedToday, fresh.DailyLimit);
                fresh.Status = SessionStatus.Exhausted;
                fresh.LastErrorMessage = "Dola báo đã hết lượt tạo video trong ngày.";
                switchAccount = true;
                break;

            case "credit":
                _quotaTracker.ReleaseQuota(session.Id);
                fresh = _databaseService.GetSessionById(session.Id) ?? fresh;
                fresh.Status = SessionStatus.Exhausted;
                fresh.LastErrorMessage = "Không đủ credit để tạo video.";
                switchAccount = true;
                break;

            case "login_required":
                _quotaTracker.ReleaseQuota(session.Id);
                fresh = _databaseService.GetSessionById(session.Id) ?? fresh;
                fresh.Status = SessionStatus.Invalid;
                fresh.LastErrorMessage = "Mất đăng nhập Dola — mở profile và đăng nhập lại.";
                switchAccount = true;
                break;

            case "risk_control":
            case "unhealthy":
                // Gateway đã đặt cooldown cho tài khoản; chưa tốn lượt
                _quotaTracker.ReleaseQuota(session.Id);
                fresh = _databaseService.GetSessionById(session.Id) ?? fresh;
                fresh.LastErrorMessage = gx.FailureCode == "risk_control"
                    ? "Dola bật kiểm soát rủi ro (captcha) — tạm nghỉ."
                    : "Chat hỏi thăm không được Dola trả lời — tạm nghỉ.";
                switchAccount = true;
                break;

            case "timeout":
                // Video có thể vẫn ra sau đó và gateway không gửi lại prompt: không đổi tài khoản, không hoàn lượt
                return false;

            case "lost":     // mất liên lạc với gateway khi đang chờ: video có thể vẫn ra, không gửi lại prompt
            case "download": // video đã có, chỉ tải lỗi
                return false;

            case "429":
            case "rejected":
            case "no_account":
                _quotaTracker.ReleaseQuota(session.Id);
                return false;

            default:
                // Lỗi khác trong lúc chạy UI: thử tài khoản khác một lần, đã tốn công nên không hoàn lượt
                fresh.LastErrorMessage = gx.Message.Length > 200 ? gx.Message[..200] : gx.Message;
                switchAccount = true;
                break;
        }

        _databaseService.UpsertSession(fresh);
        tried.Add(session.Id);
        NotifyProfilesChanged();
        task.ErrorMessage = gx.Message;
        task.ProgressPercent = 0;
        task.StageText = null;
        _databaseService.UpsertTask(task);
        TaskUpdated?.Invoke(task);
        Log($"[{session.Name}] Không tạo được ({gx.FailureCode ?? "lỗi"}): {gx.Message} → đổi sang tài khoản khác.");
        return switchAccount;
    }

    // ------------------------------------------------------------------ theo dõi tiến trình

    private static string? DescribeStage(string? stage) => stage switch
    {
        "warmup" => "Kiểm tra tài khoản (chat hỏi thăm)",
        "new_chat" => "Mở chat mới",
        "submitting" => "Nhập cấu hình & gửi prompt",
        "generating" => "Dola đang tạo video",
        "done" => "Hoàn tất",
        _ => null,
    };

    private static int StageProgress(string? stage) => stage switch
    {
        "warmup" => 20,
        "new_chat" => 30,
        "submitting" => 40,
        "generating" => 50,
        _ => 0,
    };

    private async Task<TaskApiResponse?> PollUntilCompleteAsync(RenderTask task, DolaSession session, int pollIntervalSec, CancellationToken ct)
    {
        var settings = _databaseService.GetSettings();
        var progress = 20;
        var notFoundCount = 0;
        var startTime = DateTime.UtcNow;
        string? lastStage = null;
        var transientErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow - startTime > TimeSpan.FromMinutes(30))
            {
                throw new GatewayTaskFailedException("Quá thời gian chờ render (30 phút).", "timeout");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, pollIntervalSec)), ct);

            TaskApiResponse? statusResp;
            try
            {
                statusResp = await _gatewayClient.GetTaskStatusAsync(task.GatewayTaskId!, settings.ClientApiKey, ct);
                transientErrors = 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Lỗi mạng thoáng qua: tác vụ vẫn chạy trên gateway, cứ hỏi lại; KHÔNG được gửi lại prompt
                transientErrors++;
                if (transientErrors >= MaxPollErrors)
                    throw new GatewayTaskFailedException(
                        $"Mất liên lạc với gateway khi chờ video ({ex.Message}). Tác vụ {task.GatewayTaskId} có thể vẫn đang chạy trên gateway.", "lost");
                Log($"[{task.GatewayTaskId}] Lỗi mạng khi hỏi trạng thái ({ex.Message}); thử lại {transientErrors}/{MaxPollErrors}...");
                continue;
            }

            if (statusResp == null)
            {
                notFoundCount++;
                if (notFoundCount >= 4)
                {
                    throw new InvalidOperationException($"Tác vụ {task.GatewayTaskId} không tìm thấy trên Gateway (có thể Gateway đã khởi động lại).");
                }
                continue;
            }
            notFoundCount = 0;

            if (statusResp.Status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                return statusResp;
            }
            if (statusResp.Status.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new GatewayTaskFailedException(
                    statusResp.Error ?? "Gateway báo lỗi không rõ nguyên nhân", statusResp.FailureCode ?? "error");
            }

            // Hiển thị giai đoạn hiện tại mà gateway báo (hỏi thăm → chat mới → gửi → tạo video)
            if (statusResp.Stage != lastStage)
            {
                lastStage = statusResp.Stage;
                var text = DescribeStage(statusResp.Stage);
                if (text != null)
                {
                    task.StageText = text;
                    Log($"[{session.Name}] {text}...");
                }
                progress = Math.Max(progress, StageProgress(statusResp.Stage));
            }

            if (statusResp.Status.Equals("processing", StringComparison.OrdinalIgnoreCase))
            {
                if (statusResp.Stage == "generating") progress = Math.Min(80, progress + 3);
                task.ProgressPercent = progress;
                task.Status = RenderTaskStatus.Processing;
                _databaseService.UpsertTask(task);
                TaskUpdated?.Invoke(task);
            }
        }

        return null;
    }

    public void Dispose()
    {
        _dispatcherCts?.Cancel();
        _dispatcherCts?.Dispose();
    }
}
