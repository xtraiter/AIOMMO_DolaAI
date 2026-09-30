using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Profiles;
using DolaCoordinator.Services.Proxy;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Sessions;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Views.Dialogs;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

/// <summary>
/// Trang "Quản lý tài khoản": mỗi dòng là một tài khoản Dola = một profile Chromium của dola-render-gateway
/// (accounts/&lt;tên&gt;) + phiên (cookie) + hạn ngạch + trạng thái thật do gateway giữ.
/// </summary>
public partial class ProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CookieRefreshInterval = TimeSpan.FromMinutes(1);
    private const int MaxLogChars = 24_000;

    private readonly IAccountProfileService _chrome;
    private readonly IDatabaseService _db;
    private readonly ISessionValidator _validator;
    private readonly IQuotaTracker _quota;
    private readonly ISecurityService _security;
    private readonly IDolaGatewayClient _gateway;
    private readonly IGatewayHost _gatewayHost;
    private readonly IProxyService _proxies;

    private readonly CancellationTokenSource _cts = new();
    private readonly PeriodicTimer _timer = new(MonitorInterval);
    private readonly Dictionary<string, DateTime> _lastCapture = new();
    private readonly HashSet<string> _pendingVerify = new(); // profile vừa đăng nhập, chờ đóng cửa sổ để gateway verify
    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public ObservableCollection<AccountProfile> Profiles { get; } = new();
    public ICollectionView View { get; }

    public string[] StatusFilters { get; } =
        { "Mọi trạng thái", "Sẵn sàng", "Hết hạn ngạch / credit", "Chưa đăng nhập", "Hết phiên", "Đang mở" };

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _statusFilterIndex;

    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _readyCount;
    [ObservableProperty] private int _exhaustedCount;
    [ObservableProperty] private int _needLoginCount;
    [ObservableProperty] private int _runningCount;

    [ObservableProperty] private bool _isAllSelected;
    [ObservableProperty] private bool _isValidating;
    [ObservableProperty] private string _logOutput = string.Empty;
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private string _problemText = string.Empty;

    public ProfilesViewModel(
        IAccountProfileService chrome,
        IDatabaseService db,
        ISessionValidator validator,
        IQuotaTracker quota,
        ISecurityService security,
        IDolaGatewayClient gateway,
        IGatewayHost gatewayHost,
        IProxyService proxies)
    {
        _chrome = chrome;
        _db = db;
        _validator = validator;
        _quota = quota;
        _security = security;
        _gateway = gateway;
        _gatewayHost = gatewayHost;
        _proxies = proxies;

        View = CollectionViewSource.GetDefaultView(Profiles);
        View.Filter = FilterProfile;

        LoadProfiles();
        WeakReferenceMessenger.Default.Register<ProfilesViewModel, SessionsChangedMessage>(this, static (vm, _) => vm.ReloadSessions());
        WeakReferenceMessenger.Default.Register<ProfilesViewModel, ProxiesChangedMessage>(this, static (vm, _) => vm.OnProxiesChanged());
        _ = MonitorLoopAsync(_cts.Token);
    }

    // ------------------------------------------------------------------ load / filter / counters

    /// <summary>
    /// Đồng bộ danh sách với gateway: tài khoản = thư mục accounts/&lt;tên&gt;. Phiên cũ chưa có profile và
    /// tài khoản có sẵn của gateway (vd. tạo bằng add_account.py) đều được đưa vào chung một danh sách.
    /// </summary>
    public void LoadProfiles()
    {
        RefreshProblem();
        var profiles = _db.GetAllProfiles();

        if (!HasProblem)
        {
            foreach (var p in profiles)
            {
                _chrome.ResolvePaths(p);
                Directory.CreateDirectory(p.FolderPath); // thư mục bị xóa ngoài app thì tạo lại (tài khoản trống, chờ đăng nhập lại)
            }

            var linked = profiles.Select(p => p.LinkedSessionId).Where(id => id != null).ToHashSet();
            foreach (var orphan in _db.GetAllSessions().Where(s => !linked.Contains(s.Id)))
                profiles.Add(_chrome.AdoptSession(orphan));

            profiles.AddRange(_chrome.DiscoverGatewayAccounts(profiles.Select(p => p.Name)));
        }

        Profiles.Clear();
        foreach (var p in profiles.OrderBy(p => p.CreatedAt))
            Profiles.Add(p);
        ReloadSessions();
        if (!HasProblem) _proxies.SyncFiles(Profiles); // proxy.txt của từng tài khoản phải khớp proxy đang gán
        RefreshProxyTexts();
    }

    // ------------------------------------------------------------------ proxy

    /// <summary>Cột Proxy: tên proxy (+ quốc gia IP thoát nếu đã kiểm tra).</summary>
    private void RefreshProxyTexts()
    {
        var all = _proxies.GetAll().ToDictionary(p => p.Id);
        foreach (var a in Profiles)
        {
            if (string.IsNullOrEmpty(a.ProxyId)) { a.ProxyText = "—"; continue; }
            if (!all.TryGetValue(a.ProxyId, out var px)) { a.ProxyText = "(proxy đã xóa)"; continue; }
            a.ProxyText = px.DisplayName + (px.LastOk == true && !string.IsNullOrEmpty(px.LastCountry) ? $" · {px.LastCountry}" : string.Empty)
                          + (px.LastOk == false ? " · lỗi" : string.Empty);
        }
    }

    /// <summary>Proxy được sửa / xóa / kiểm tra ở trang khác: đọc lại việc gán từ DB rồi làm mới cột.</summary>
    private void OnProxiesChanged()
    {
        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            Application.Current.Dispatcher.InvokeAsync(OnProxiesChanged);
            return;
        }
        var stored = _db.GetAllProfiles().ToDictionary(p => p.Id, p => p.ProxyId);
        foreach (var a in Profiles)
            if (stored.TryGetValue(a.Id, out var id)) a.ProxyId = id;
        RefreshProxyTexts();
    }

    /// <summary>Gán proxy cho các tài khoản đã tích: một proxy cho tất cả, chia vòng tròn, hoặc bỏ proxy.</summary>
    [RelayCommand]
    private void AssignProxySelected()
    {
        var targets = Selected();
        if (targets.Count == 0) { Log("Tích chọn tài khoản cần gán proxy."); return; }
        var list = _proxies.GetAll();
        var dlg = new ProxyAssignWindow(list, targets.Count) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        switch (dlg.Mode)
        {
            case ProxyAssignMode.None:
                foreach (var a in targets) _proxies.Assign(a, null);
                Log($"Đã bỏ proxy của {targets.Count} tài khoản.");
                break;
            case ProxyAssignMode.RoundRobin:
                for (var i = 0; i < targets.Count; i++) _proxies.Assign(targets[i], list[i % list.Count].Id);
                Log($"Đã chia vòng tròn {list.Count} proxy cho {targets.Count} tài khoản.");
                break;
            default:
                foreach (var a in targets) _proxies.Assign(a, dlg.Proxy!.Id);
                Log($"Đã gán proxy '{dlg.Proxy!.DisplayName}' cho {targets.Count} tài khoản.");
                break;
        }
        RefreshProxyTexts();
        if (targets.Any(a => a.IsRunning))
            Log("Có tài khoản đang mở Chromium: đóng rồi mở lại để dùng proxy mới.");
        WeakReferenceMessenger.Default.Send(new ProxiesChangedMessage()); // trang Quản lý proxy cập nhật số tài khoản đang dùng
    }

    /// <summary>Nạp lại phiên/hạn ngạch từ DB lên từng dòng. Gọi trên luồng UI (TaskDispatcher gọi sau khi trừ quota).</summary>
    public void ReloadSessions()
    {
        var sessions = _db.GetAllSessions();
        _quota.EnsureDailyQuotaReset(sessions);
        var map = sessions.ToDictionary(s => s.Id);

        foreach (var p in Profiles)
            p.Session = p.LinkedSessionId != null && map.TryGetValue(p.LinkedSessionId, out var s) ? s : null;

        UpdateCounters();
        if (StatusFilterIndex != 0) View.Refresh();
    }

    private void RefreshProblem()
    {
        var problem = _chrome.EnvironmentProblem;
        HasProblem = problem != null;
        ProblemText = problem ?? string.Empty;
    }

    private bool FilterProfile(object o)
    {
        if (o is not AccountProfile p) return false;

        var statusOk = StatusFilterIndex switch
        {
            1 => p.State == ProfileState.Ready,
            2 => p.State is ProfileState.Exhausted or ProfileState.NoCredit,
            3 => p.State == ProfileState.NotLoggedIn,
            4 => p.State == ProfileState.Invalid,
            5 => p.IsRunning,
            _ => true,
        };
        if (!statusOk) return false;

        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (p.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    partial void OnSearchTextChanged(string value) => View.Refresh();
    partial void OnStatusFilterIndexChanged(int value) => View.Refresh();

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var p in View.Cast<AccountProfile>())
            p.IsSelected = value;
    }

    private void UpdateCounters()
    {
        TotalCount = Profiles.Count;
        ReadyCount = Profiles.Count(p => p.State == ProfileState.Ready);
        ExhaustedCount = Profiles.Count(p => p.State is ProfileState.Exhausted or ProfileState.NoCredit);
        NeedLoginCount = Profiles.Count(p => p.State is ProfileState.NotLoggedIn or ProfileState.Invalid);
        RunningCount = Profiles.Count(p => p.IsRunning);
    }

    private List<AccountProfile> Selected() => Profiles.Where(p => p.IsSelected).ToList();

    /// <summary>Các dòng đã chọn; nếu chưa chọn dòng nào thì lấy toàn bộ dòng đang hiển thị.</summary>
    private List<AccountProfile> SelectedOrVisible()
    {
        var picked = Selected();
        return picked.Count > 0 ? picked : View.Cast<AccountProfile>().ToList();
    }

    private void Log(string message)
    {
        LogOutput += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (LogOutput.Length > MaxLogChars)
            LogOutput = LogOutput[^(MaxLogChars / 2)..];
    }

    // ------------------------------------------------------------------ monitor

    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        try
        {
            await MonitorTickAsync(ct); // nhận lại các profile đang mở từ lần chạy trước
            while (await _timer.WaitForNextTickAsync(ct))
                await MonitorTickAsync(ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task MonitorTickAsync(CancellationToken ct)
    {
        // Bỏ qua nếu lượt trước còn đang chạy, tránh lưu/kiểm tra phiên trùng
        if (!await _tickGate.WaitAsync(0, ct)) return;
        try
        {
            RefreshProblem();
            if (HasProblem) return;

            await SyncGatewayAccountsAsync(ct);

            foreach (var p in Profiles.Where(p => !p.IsBusy).ToList())
                await ApplyProbeAsync(p, _chrome.Probe(p));

            UpdateCounters();
            if (StatusFilterIndex != 0) View.Refresh();
        }
        finally
        {
            _tickGate.Release();
        }
    }

    /// <summary>Lấy trạng thái thật từ gateway (/api/admin/accounts) và nhận thêm tài khoản mới xuất hiện ở gateway.</summary>
    private async Task SyncGatewayAccountsAsync(CancellationToken ct)
    {
        var accounts = await _gateway.GetAccountsAsync(ct); // null = gateway chưa chạy → các dòng chỉ còn dữ liệu cục bộ
        var map = accounts?.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var p in Profiles)
            p.Gateway = map != null && map.TryGetValue(p.Name, out var a) ? a : null;

        var added = _chrome.DiscoverGatewayAccounts(Profiles.Select(p => p.Name));
        if (added.Count == 0) return;

        foreach (var p in added.OrderBy(p => p.Name))
        {
            Profiles.Add(p);
            Log($"Phát hiện tài khoản mới trong gateway: '{p.Name}'.");
        }
        ReloadSessions();
    }

    private async Task ApplyProbeAsync(AccountProfile p, ProfileProbe probe)
    {
        var wasRunning = p.IsRunning;
        p.IsRunning = probe.Running;

        // Script đang chờ bạn giải captcha / 2FA / checkpoint: hiện trên dòng và ghi nhật ký một lần
        var previousNote = p.HumanNote;
        p.HumanNote = probe.Running ? probe.NeedHuman : null;
        if (!string.IsNullOrEmpty(p.HumanNote) && p.HumanNote != previousNote)
            Log($"⚠ '{p.Name}' cần bạn xử lý: {p.HumanNote}");

        if (wasRunning && !probe.Running)
        {
            Log($"Profile '{p.Name}' đã đóng.");
            _lastCapture.Remove(p.Id);

            // Profile tự đóng ngay sau khi đăng nhập có thể xảy ra giữa hai lượt kiểm tra: nhặt phiên từ cookie.txt
            var (loggedInThisRun, savedNewSession) = _chrome.CaptureFromCookieFile(p);
            if (loggedInThisRun)
            {
                p.LoginStatus = ProfileLoginStatus.LoggedIn;
                if (savedNewSession)
                {
                    ReloadSessions();
                    Log($"✔ '{p.Name}' đã đăng nhập Dola — đã lưu phiên.");
                }
                _pendingVerify.Add(p.Id);
            }
            _db.UpsertProfile(p);
            await VerifyPendingAsync(p);
            return;
        }
        if (!probe.Running) return;
        if (!wasRunning) Log($"Profile '{p.Name}' đang mở.");

        p.LastCheckedAt = DateTime.UtcNow;

        if (!probe.LoggedIn || string.IsNullOrWhiteSpace(probe.CookieHeader))
        {
            if (p.LoginStatus != ProfileLoginStatus.LoggedOut)
            {
                p.LoginStatus = ProfileLoginStatus.LoggedOut;
                _db.UpsertProfile(p);
            }
            return;
        }

        var statusChanged = p.LoginStatus != ProfileLoginStatus.LoggedIn;
        var due = !_lastCapture.TryGetValue(p.Id, out var last) || DateTime.UtcNow - last > CookieRefreshInterval;
        if (!statusChanged && !due) return;

        var sessionChanged = _chrome.CaptureSession(p, probe.CookieHeader);
        _lastCapture[p.Id] = DateTime.UtcNow;
        p.LoginStatus = ProfileLoginStatus.LoggedIn;
        _db.UpsertProfile(p);

        if (statusChanged) Log($"✔ '{p.Name}' đã đăng nhập Dola — đã lưu phiên (cookie.txt của gateway đã được cập nhật).");
        if (sessionChanged)
        {
            ReloadSessions();
            // Gateway cần mở chính profile này để verify nên chỉ làm được sau khi đóng cửa sổ
            if (_pendingVerify.Add(p.Id))
                Log($"'{p.Name}': sẽ tự kiểm tra phiên với gateway khi bạn đóng profile.");
        }
    }

    /// <summary>Sau khi profile đóng: nhờ gateway verify (login_ok) để xác nhận phiên dùng được cho render.</summary>
    private async Task VerifyPendingAsync(AccountProfile p)
    {
        if (!_pendingVerify.Remove(p.Id)) return;
        await ValidateAsync(p);
    }

    /// <summary>Kiểm tra phiên của profile bằng gateway (Chromium headless trên accounts/&lt;tên&gt;).</summary>
    private async Task ValidateAsync(AccountProfile p)
    {
        if (p.IsRunning)
        {
            Log($"'{p.Name}' đang mở — đóng profile trước khi kiểm tra phiên (gateway cần mở profile).");
            return;
        }

        ReloadSessions();
        var session = p.Session;
        if (session == null)
        {
            p.LoginStatus = ProfileLoginStatus.LoggedOut; // chưa có phiên = chưa đăng nhập, dù trước đó từng thấy đăng nhập
            _db.UpsertProfile(p);
            p.RefreshState();
            Log($"'{p.Name}' chưa có phiên. Chọn tài khoản rồi bấm 'Đăng nhập tự động' (hoặc Mở và đăng nhập tay).");
            return;
        }

        Log($"Đang nhờ gateway kiểm tra đăng nhập của '{p.Name}' (Chromium headless, vài chục giây)...");
        session.Status = SessionStatus.Validating;
        p.RefreshState();
        try
        {
            await _validator.ValidateSessionAsync(session, _cts.Token);
            Log(session.Status switch
            {
                SessionStatus.Active => $"✔ '{p.Name}': còn đăng nhập, sẵn sàng nhận tác vụ.",
                SessionStatus.Exhausted => $"'{p.Name}': còn đăng nhập nhưng đã hết hạn ngạch hôm nay.",
                SessionStatus.Invalid => $"✖ '{p.Name}': HẾT PHIÊN — {session.LastErrorMessage} Tích tài khoản rồi bấm 'Đăng nhập tự động'.",
                _ => $"⚠ '{p.Name}': chưa kết luận được — {session.LastErrorMessage}",
            });

            // Ghi kết luận vào tài khoản để dòng đổi trạng thái ngay và không bị trạng thái cũ "Đã đăng nhập" đè lại
            if (session.Status is SessionStatus.Active or SessionStatus.Exhausted)
                p.LoginStatus = ProfileLoginStatus.LoggedIn;
            else if (session.Status == SessionStatus.Invalid)
                p.LoginStatus = ProfileLoginStatus.LoggedOut;
            p.LastCheckedAt = DateTime.UtcNow;
            _db.UpsertProfile(p);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            session.Status = SessionStatus.Unknown;
            Log($"⚠ Không kiểm tra được '{p.Name}': {ex.Message}");
        }
        ReloadSessions();
        await SyncGatewayAccountsAsync(_cts.Token); // lấy luôn login_ok / credit mới ghi
    }

    // ------------------------------------------------------------------ add / edit / toggle

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        if (!EnsureEnvironment()) return;

        var dlg = new ProfileEditorWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var options = dlg.Login;

        var name = dlg.ProfileName.Trim();
        if (!GatewayLocator.IsValidAccountName(name))
        {
            Log($"Không tạo '{name}': tên vượt 32 ký tự hoặc có ký tự không hợp lệ.");
            return;
        }
        if (NameExists(name, null) || Directory.Exists(Path.Combine(_chrome.AccountsDir!, name)))
        {
            Log($"Không tạo '{name}': tài khoản đã tồn tại trong gateway.");
            return;
        }

        var profile = _chrome.CreateProfile(name, dlg.Notes);
        if (options.IsAutomatic && options.Remember)
            _chrome.SaveLogin(profile, options); // phải lưu TRƯỚC khi mở: sau khi đưa cho script, mật khẩu bị xóa khỏi bộ nhớ
        Profiles.Add(profile);
        var created = new List<AccountProfile> { profile };

        ReloadSessions();
        if (created.Count == 0) return;
        Log($"Đã tạo {created.Count} tài khoản (thư mục accounts/… của gateway).");

        // Đăng nhập tự động (Google / Facebook / cookie Facebook): mở profile và để script làm
        if (created.Count == 1 && options.IsAutomatic)
        {
            await LaunchWithOptionsAsync(created[0], options);
            return;
        }

        // Có cookie: cho gateway kiểm tra ngay (profile chưa mở nên không bị khóa)
        if (created.Count == 1 && created[0].LinkedSessionId != null)
            await ValidateAsync(created[0]);

        if (dlg.OpenAfterCreate && created.Count == 1)
            await ToggleProfileAsync(created[0]);
    }

    /// <summary>Sửa tài khoản đã tích: cách đăng nhập, tài khoản/mật khẩu/2FA/cookie đã ghi nhớ và ghi chú.</summary>
    [RelayCommand]
    private void EditSelected()
    {
        var targets = Selected();
        if (targets.Count != 1)
        {
            MessageBox.Show(targets.Count == 0 ? "Tích chọn 1 tài khoản (ô vuông đầu dòng) rồi bấm Sửa." : "Chỉ sửa được từng tài khoản một. Hãy tích đúng 1 dòng.",
                "Sửa tài khoản", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var p = targets[0];
        var note = p.HasSavedLogin
            ? $"✔ Đã lưu đăng nhập của tài khoản này: {p.LoginSummary}. Mật khẩu / khóa 2FA / cookie đã lưu và hiện dạng ẩn ●●●; sửa rồi bấm Lưu để thay."
            : p.Session != null || p.LoginStatus == ProfileLoginStatus.LoggedIn
                ? "Tài khoản này ĐÃ đăng nhập Dola và phiên vẫn còn dùng được — bạn không cần nhập gì cả. Phần đăng nhập bên dưới chỉ để app TỰ đăng nhập lại khi phiên hết hạn (tùy chọn; tài khoản thêm từ bản cũ chưa có thông tin này vì trước đây app không lưu)."
                : "Chưa lưu thông tin đăng nhập cho tài khoản này. Nhập một lần bên dưới (tùy chọn), lần sau chỉ cần bấm 'Đăng nhập tự động'.";
        var dlg = new ProfileEditorWindow(p, _chrome.GetSavedLogin(p), note) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        p.Notes = string.IsNullOrWhiteSpace(dlg.Notes) ? null : dlg.Notes.Trim();
        if (dlg.Login.IsAutomatic && !dlg.Login.Remember)
            dlg.Login.Method = LoginMethod.Manual; // bỏ tích "Ghi nhớ" = xóa thông tin đã lưu
        _chrome.SaveLogin(p, dlg.Login); // lưu luôn cả tài khoản vào DB
        View.Refresh();
        Log(p.HasSavedLogin ? $"Đã lưu '{p.Name}' — đăng nhập {p.SubText}." : $"Đã lưu '{p.Name}' (đăng nhập thủ công).");
    }

    /// <summary>Mở profile với đăng nhập tự động. Mật khẩu / khóa 2FA / cookie chỉ sống trong bộ nhớ và bị xóa ngay sau khi đưa cho script.</summary>
    private async Task LaunchWithOptionsAsync(AccountProfile p, LoginOptions options)
    {
        if (p.IsBusy || p.IsRunning || !EnsureEnvironment()) return;

        var label = options.Method switch
        {
            LoginMethod.Google => "Google",
            LoginMethod.Facebook => "Facebook",
            LoginMethod.FacebookCookie => "cookie Facebook",
            _ => "thủ công",
        };

        p.IsBusy = true;
        try
        {
            Log($"Đang mở '{p.Name}' và đăng nhập tự động bằng {label}...");
            await _chrome.LaunchAsync(p, options, _cts.Token);
            p.IsRunning = true;
            Log(options.IsAutomatic
                ? $"Đã mở '{p.Name}'. Script đang đăng nhập {label}; nếu có captcha/2FA thì dòng sẽ báo 'Cần bạn xử lý'."
                : $"Đã mở '{p.Name}'.");
            if (options.After == AfterLogin.Close)
                Log($"'{p.Name}' sẽ tự đóng khi đăng nhập xong.");
        }
        catch (BrowserMissingException bex)
        {
            Log($"✖ '{p.Name}': {bex.Message}");
            var answer = MessageBox.Show(bex.Message + Environment.NewLine + Environment.NewLine + "Cài đặt lại trình duyệt ngay bây giờ?",
                $"Tài khoản '{p.Name}'", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                var result = await _gatewayHost.InstallBrowserAsync(new Progress<string>(line => Log(line)));
                Log(result.Ok ? "✔ Đã cài xong trình duyệt. Bấm Mở để đăng nhập." : $"✖ {result.Error}");
                if (!result.Ok) MessageBox.Show(result.Error, "Cài đặt trình duyệt", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or ArgumentException)
        {
            Log($"✖ '{p.Name}': {ex.Message}");
            MessageBox.Show(ex.Message, $"Tài khoản '{p.Name}'", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            options.Password = string.Empty;
            options.Totp = string.Empty;
            options.Cookie = string.Empty;
            p.IsBusy = false;
            _db.UpsertProfile(p);
            UpdateCounters();
        }
    }

    /// <summary>
    /// Đăng nhập tự động cho các tài khoản đã tích: tài khoản đã ghi nhớ thông tin thì chạy luôn, chưa có thì hỏi một lần
    /// (và ghi nhớ nếu bạn để tích "Ghi nhớ").
    /// </summary>
    [RelayCommand]
    private async Task AutoLoginSelectedAsync()
    {
        var targets = Selected();
        if (targets.Count == 0) { Log("Tích chọn tài khoản cần đăng nhập tự động."); return; }
        if (!EnsureEnvironment()) return;

        var busy = targets.Where(p => p.IsRunning || p.IsBusy).ToList();
        if (busy.Count > 0)
            Log($"Bỏ qua {busy.Count} tài khoản đang mở/đang bận: {string.Join(", ", busy.Select(p => p.Name))} (đóng profile trước).");

        // Tài khoản đã đăng nhập Dola (phiên còn dùng được) thì không cần đăng nhập lại
        var alreadyIn = targets.Except(busy).Where(p => p.State is not (ProfileState.NotLoggedIn or ProfileState.Invalid or ProfileState.NeedsAction)).ToList();
        if (alreadyIn.Count > 0)
            Log($"Đã đăng nhập sẵn, không cần làm gì: {string.Join(", ", alreadyIn.Select(p => p.Name))}. (Phiên hết hạn thì tài khoản sẽ chuyển sang 'Chưa đăng nhập' / 'Hết phiên' — lúc đó bấm lại nút này.)");

        foreach (var p in targets.Except(busy).Except(alreadyIn))
        {
            if (p.HasSavedLogin)
                await LaunchWithOptionsAsync(p, _chrome.GetSavedLogin(p));
            else if (!await PromptAndLoginAsync(p))
                break; // bấm Hủy: dừng cả loạt
            await Task.Delay(2000); // mở lần lượt để máy không bị nghẽn
        }
    }

    /// <summary>Hỏi thông tin đăng nhập cho một tài khoản chưa lưu. Trả false nếu người dùng bấm Hủy.</summary>
    private async Task<bool> PromptAndLoginAsync(AccountProfile p)
    {
        var initial = _chrome.GetSavedLogin(p);
        if (!initial.IsAutomatic && p.Session != null)
        {
            // Phiên đang giữ cookie Facebook chưa chuyển thành cookie Dola: đề xuất luôn luồng cookie Facebook
            var token = p.Session.PlainToken ?? _security.Decrypt(p.Session.EncryptedToken);
            if (LoginOptions.LooksLikeFacebookCookie(token))
            {
                initial.Method = LoginMethod.FacebookCookie;
                initial.Cookie = token;
            }
        }

        var note = "Chưa lưu thông tin đăng nhập cho tài khoản này (tài khoản thêm từ bản cũ, hoặc thêm bằng đăng nhập thủ công). Nhập một lần; để tích \"Ghi nhớ\" thì lần sau app tự dùng, không hỏi lại.";
        var dlg = new AutoLoginWindow(p.Name, initial, note) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return false;

        if (dlg.Options.IsAutomatic && dlg.Options.Remember)
            _chrome.SaveLogin(p, dlg.Options); // lưu trước khi mở: mở xong mật khẩu bị xóa khỏi bộ nhớ
        await LaunchWithOptionsAsync(p, dlg.Options);
        return true;
    }

    private bool NameExists(string name, AccountProfile? except)
        => Profiles.Any(x => x != except && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task ToggleProfileAsync(AccountProfile? p)
    {
        if (p == null || p.IsBusy) return;
        if (!p.IsRunning && !EnsureEnvironment()) return;

        p.IsBusy = true;
        try
        {
            if (p.IsRunning)
            {
                Log($"Đang đóng '{p.Name}' và lưu phiên...");
                await _chrome.CloseAsync(p, _cts.Token);
                p.IsRunning = false;
                Log($"Đã đóng '{p.Name}'.");
                _lastCapture.Remove(p.Id);
                p.IsBusy = false;
                await VerifyPendingAsync(p);
            }
            else
            {
                Log($"Đang mở '{p.Name}' bằng Chromium của gateway (patchright)...");
                await _chrome.LaunchAsync(p, null, _cts.Token);
                p.IsRunning = true;
                Log(p.LinkedSessionId != null
                    ? $"Đã mở '{p.Name}' (gateway nạp sẵn cookie đã lưu). Đăng nhập nếu cần, app tự lưu phiên."
                    : $"Đã mở '{p.Name}'. Đăng nhập Dola trong cửa sổ, app sẽ tự lưu phiên.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or Win32Exception or ArgumentException)
        {
            Log($"✖ '{p.Name}': {ex.Message}");
            MessageBox.Show(ex.Message, $"Tài khoản '{p.Name}'", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            p.IsBusy = false;
            _db.UpsertProfile(p);
            UpdateCounters();
        }
    }

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        var targets = Selected().Where(p => !p.IsRunning).ToList();
        if (targets.Count == 0) { Log("Chọn ít nhất một tài khoản đang đóng để mở."); return; }

        foreach (var p in targets)
        {
            await ToggleProfileAsync(p);
            await Task.Delay(1500); // mở lần lượt để máy không bị nghẽn
        }
    }

    [RelayCommand]
    private async Task CloseSelectedAsync()
    {
        var targets = Selected().Where(p => p.IsRunning).ToList();
        if (targets.Count == 0) { Log("Không có profile nào đang mở trong các mục đã chọn."); return; }
        foreach (var p in targets)
            await ToggleProfileAsync(p);
    }

    // ------------------------------------------------------------------ session validation / quota

    /// <summary>Kiểm tra phiên của các dòng đã chọn (hoặc tất cả dòng đang hiển thị nếu chưa chọn).</summary>
    [RelayCommand]
    private async Task ValidateSelectedAsync()
    {
        if (IsValidating) return;
        var targets = SelectedOrVisible().Where(p => !p.IsRunning).ToList();
        if (targets.Count == 0) { Log("Không có tài khoản nào để kiểm tra (profile đang mở thì đóng trước)."); return; }

        IsValidating = true;
        try
        {
            Log($"Kiểm tra {targets.Count} tài khoản qua gateway...");
            foreach (var p in targets)
                await ValidateAsync(p);
            Log($"Xong: {ReadyCount} sẵn sàng, {ExhaustedCount} hết hạn ngạch/credit, {NeedLoginCount} cần đăng nhập.");
        }
        finally
        {
            IsValidating = false;
        }
    }

    /// <summary>Lưu lựa chọn "Dùng để chạy" của một dòng (ô tích trong bảng).</summary>
    public void PersistUseForRender(AccountProfile p)
    {
        if (p.Session == null) return;
        _db.UpsertSession(p.Session);
        Log($"'{p.Name}': {(p.UseForRender ? "được dùng" : "KHÔNG dùng")} khi tạo video.");
    }

    [RelayCommand]
    private void IncludeSelected() => SetUseForRender(true);

    [RelayCommand]
    private void ExcludeSelected() => SetUseForRender(false);

    private void SetUseForRender(bool value)
    {
        var targets = Selected().Where(p => p.Session != null).ToList();
        if (targets.Count == 0) { Log("Tích chọn tài khoản (đã đăng nhập) để đưa vào / loại khỏi tạo video."); return; }
        foreach (var p in targets)
        {
            p.UseForRender = value;
            _db.UpsertSession(p.Session!);
        }
        Log($"{(value ? "Đã đưa" : "Đã loại")} {targets.Count} tài khoản {(value ? "vào" : "khỏi")} việc tạo video.");
    }

    [RelayCommand]
    private void ResetQuotaSelected()
    {
        var targets = SelectedOrVisible().Where(p => p.LinkedSessionId != null).ToList();
        if (targets.Count == 0) return;

        var scope = Selected().Count > 0 ? $"{targets.Count} tài khoản đã chọn" : "toàn bộ tài khoản";
        if (MessageBox.Show($"Reset bộ đếm hạn ngạch hôm nay của {scope}?\n(Chỉ đặt lại bộ đếm của app, không đổi hạn mức của gateway.)",
                "Xác nhận", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var p in targets)
            _quota.ResetQuota(p.LinkedSessionId!);
        ReloadSessions();
        Log($"Đã reset hạn ngạch của {targets.Count} tài khoản.");
    }

    // ------------------------------------------------------------------ import / export

    /// <summary>
    /// Nhập hàng loạt từ Excel / CSV / TXT: Google (Gmail), Facebook thường, Facebook cookie, Cookie Dola, tài khoản thủ công.
    /// Thông tin đăng nhập được ghi nhớ (mã hóa) để bấm "Đăng nhập tự động" là chạy.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        if (!EnsureEnvironment()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "Excel / CSV / văn bản (*.xlsx;*.csv;*.tsv;*.txt)|*.xlsx;*.csv;*.tsv;*.txt|Tất cả (*.*)|*.*",
            Title = "Nhập danh sách tài khoản (Excel / CSV)",
        };
        if (dialog.ShowDialog() != true) return;

        List<AccountRow> rows;
        try
        {
            rows = AccountFileParser.Parse(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or DecoderFallbackException)
        {
            MessageBox.Show($"Không đọc được file: {ex.Message}", "Nhập tài khoản", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (rows.Count == 0)
        {
            MessageBox.Show("Không tìm thấy dòng tài khoản nào trong file. Bấm 'Tải file mẫu Excel' để xem các cột và từng loại tài khoản.",
                "Nhập tài khoản", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bad = rows.Where(r => r.Error != null).ToList();
        var good = rows.Where(r => r.Error == null).ToList();
        var existing = good.Where(r => NameExists(r.Name, null) || Directory.Exists(Path.Combine(_chrome.AccountsDir!, r.Name))).ToList();
        var toCreate = good.Except(existing).ToList();

        var summary = new StringBuilder($"File có {rows.Count} dòng tài khoản:{Environment.NewLine}");
        foreach (var g in toCreate.GroupBy(r => r.KindLabel))
            summary.AppendLine($"  • {g.Count()} × {g.Key}");
        if (existing.Count > 0)
            summary.AppendLine($"  • {existing.Count} bỏ qua vì tên đã tồn tại: {string.Join(", ", existing.Take(5).Select(r => r.Name))}{(existing.Count > 5 ? "…" : string.Empty)}");
        if (bad.Count > 0)
        {
            summary.AppendLine($"  • {bad.Count} dòng lỗi (bỏ qua):");
            foreach (var b in bad.Take(8)) summary.AppendLine($"      dòng {b.Line}: {b.Error}");
            if (bad.Count > 8) summary.AppendLine($"      … và {bad.Count - 8} dòng nữa");
        }

        if (toCreate.Count == 0)
        {
            MessageBox.Show(summary.ToString(), "Nhập tài khoản", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        summary.AppendLine().Append($"Nhập {toCreate.Count} tài khoản?");
        if (MessageBox.Show(summary.ToString(), "Nhập tài khoản", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        var created = new List<(AccountProfile Profile, AccountRow Row)>();
        foreach (var r in toCreate)
        {
            var p = _chrome.CreateProfile(r.Name, r.Notes);
            if (r.IsAutomatic)
            {
                _chrome.SaveLogin(p, new LoginOptions
                {
                    Method = r.LoginMethod, Email = r.Email, Password = r.Password, Totp = r.Totp, Cookie = r.Cookie,
                    After = r.After, Remember = true,
                });
            }
            else if (r.Kind == AccountKind.DolaCookie)
            {
                _chrome.AttachCookie(p, r.Cookie);
            }
            Profiles.Add(p);
            created.Add((p, r));
        }

        ReloadSessions();
        foreach (var (p, r) in created.Where(c => !c.Row.Use))
        {
            if (p.Session == null) { Log($"'{p.Name}': chưa có phiên nên chưa đặt được 'Không dùng để chạy' (đặt sau khi đăng nhập)."); continue; }
            p.UseForRender = false;
            _db.UpsertSession(p.Session);
        }
        UpdateCounters();
        Log($"Đã nhập {created.Count} tài khoản từ {Path.GetFileName(dialog.FileName)}" +
            (existing.Count + bad.Count > 0 ? $" (bỏ qua {existing.Count} trùng tên, {bad.Count} lỗi)." : "."));

        // Google / Facebook / cookie Facebook: mời đăng nhập luôn
        var autos = created.Where(c => c.Row.IsAutomatic).Select(c => c.Profile).ToList();
        if (autos.Count > 0 && MessageBox.Show(
                $"Đăng nhập tự động ngay cho {autos.Count} tài khoản (Google / Facebook / cookie Facebook)?\n\n" +
                "Mỗi tài khoản mở một cửa sổ Chromium; captcha / 2FA (khi không có khóa TOTP) bạn tự xử lý.\n" +
                "Chọn Không thì để sau: tích tài khoản rồi bấm 'Đăng nhập tự động'.",
                "Đăng nhập tự động", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var p in autos)
            {
                await LaunchWithOptionsAsync(p, _chrome.GetSavedLogin(p));
                await Task.Delay(2000); // mở lần lượt để máy không bị nghẽn
            }
        }

        // Cookie Dola: mời kiểm tra phiên qua gateway
        var dolas = created.Where(c => c.Row.Kind == AccountKind.DolaCookie).Select(c => c.Profile).ToList();
        if (dolas.Count > 0 && MessageBox.Show(
                $"Nhờ gateway kiểm tra đăng nhập của {dolas.Count} tài khoản cookie Dola ngay bây giờ?\n(Mỗi tài khoản mở Chromium ẩn vài chục giây.)",
                "Kiểm tra phiên", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            IsValidating = true;
            try
            {
                foreach (var p in dolas) await ValidateAsync(p);
            }
            finally
            {
                IsValidating = false;
            }
        }
    }

    [RelayCommand]
    private void DownloadTemplate()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = "mau_nhap_tai_khoan.xlsx",
            Title = "Lưu file mẫu nhập tài khoản",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            if (TableFile.IsExcel(dialog.FileName)) ImportTemplates.WriteAccountTemplate(dialog.FileName);
            else File.WriteAllText(dialog.FileName, ImportTemplates.AccountTemplateCsv(), new UTF8Encoding(true));
            Log($"Đã lưu file mẫu: {dialog.FileName} (Excel có sheet 'Hướng dẫn' ghi rõ từng loại tài khoản).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Không lưu được file (đang mở trong Excel?): {ex.Message}", "File mẫu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void ExportToFile()
    {
        var targets = SelectedOrVisible().Where(p => p.Session != null).ToList();
        if (targets.Count == 0) { Log("Không có tài khoản nào để xuất."); return; }

        if (MessageBox.Show(
                "File xuất chứa cookie đăng nhập ở dạng văn bản thường — ai có file này dùng được tài khoản.\nChỉ lưu ở nơi an toàn. Tiếp tục?",
                "Cảnh báo bảo mật", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        var dialog = new SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"Dola_Accounts_{DateTime.Now:yyyyMMdd_HHmm}.txt",
            Title = "Xuất danh sách tài khoản"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder("# Tên|Cookie\n");
            foreach (var p in targets)
            {
                var s = p.Session!;
                sb.AppendLine($"{p.Name}|{s.PlainToken ?? _security.Decrypt(s.EncryptedToken)}");
            }
            File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
            Log($"Đã xuất {targets.Count} tài khoản ra {dialog.FileName}.");
        }
        catch (IOException ex)
        {
            MessageBox.Show($"Không xuất được file: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ folders / delete / misc

    /// <summary>Mở thư mục accounts/&lt;tên&gt; của tài khoản đã tích (chưa tích thì mở thư mục accounts chung).</summary>
    [RelayCommand]
    private void OpenFolderSelected()
    {
        var picked = Selected();
        if (picked.Count == 0) { OpenProfilesRoot(); return; }
        foreach (var p in picked.Take(5)) _chrome.OpenFolder(p);
    }

    [RelayCommand]
    private void OpenProfilesRoot()
    {
        if (EnsureEnvironment()) _chrome.OpenAccountsDir();
    }

    [RelayCommand]
    private void SyncFromGateway()
    {
        LoadProfiles();
        Log($"Đã đồng bộ với thư mục accounts/ của gateway: {TotalCount} tài khoản.");
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var targets = Selected();
        if (targets.Count == 0) { Log("Chưa chọn tài khoản nào để xóa."); return; }
        await DeleteManyAsync(targets);
    }

    private async Task DeleteManyAsync(List<AccountProfile> targets)
    {
        if (targets.Any(t => t.IsRunning))
        {
            MessageBox.Show("Có profile đang mở. Hãy đóng profile đó trước khi xóa.", "Không thể xóa", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var label = targets.Count == 1 ? $"tài khoản '{targets[0].Name}'" : $"{targets.Count} tài khoản đã chọn";
        var answer = MessageBox.Show(
            $"Xóa hẳn {label}?\n\n" +
            "Sẽ xóa thư mục accounts/<tên> của gateway (profile Chromium, cookie), metadata trong gateway, phiên Dola và hạn ngạch.\n" +
            "Không khôi phục được.",
            "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        foreach (var p in targets)
        {
            try
            {
                await _chrome.DeleteAsync(p, _cts.Token);
                Profiles.Remove(p);
                Log($"Đã xóa '{p.Name}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Log($"✖ Không xóa được '{p.Name}': {ex.Message}");
            }
        }
        IsAllSelected = false;
        UpdateCounters();
    }

    [RelayCommand]
    private void ClearLog() => LogOutput = string.Empty;

    private bool EnsureEnvironment()
    {
        RefreshProblem();
        if (!HasProblem) return true;
        MessageBox.Show(ProblemText, "Thiếu dola-render-gateway", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }
}
