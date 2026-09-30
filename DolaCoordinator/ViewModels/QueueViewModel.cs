using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Notification;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Storage;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

/// <summary>
/// Trang "Vận hành": KHÔNG nhập prompt (việc đó ở "Quản lý prompt") mà quản lý tiến trình: chạy / tạm dừng / dừng,
/// độ ưu tiên, thử lại, hủy, dọn hàng đợi và theo dõi từng video đang làm.
/// </summary>
public partial class QueueViewModel : ObservableObject
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly IDatabaseService _databaseService;
    private readonly INotificationService _notificationService;

    public ObservableCollection<RenderTask> Tasks { get; } = new();
    public ICollectionView View { get; }

    public string[] StatusFilters { get; } =
        { "Tất cả", "Đang chờ", "Đang chạy", "Hoàn tất", "Lỗi", "Đã hủy" };

    [ObservableProperty] private int _statusFilterIndex;
    [ObservableProperty] private string _searchText = string.Empty;

    partial void OnStatusFilterIndexChanged(int value) => View.Refresh();
    partial void OnSearchTextChanged(string value) => View.Refresh();

    [ObservableProperty] private bool _isAllSelected;

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var task in View.Cast<RenderTask>()) task.IsSelected = value;
    }

    [ObservableProperty] private string _logOutput = string.Empty;

    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _processingCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _failedCount;

    /// <summary>Số tác vụ chưa xong (chờ + đang chạy): hiện cạnh tên mục Vận hành.</summary>
    [ObservableProperty] private int _activeCount;

    // ---- tiến độ tổng của đợt đang chạy (thanh ở đáy cửa sổ): 100% = xong toàn bộ
    [ObservableProperty] private int _batchCount;
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private string _overallText = string.Empty;

    private readonly HashSet<string> _batch = new();
    private int _lastActive;

    // ---- trạng thái điều phối
    [ObservableProperty] private bool _isQueueRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _runStateText = "Đã dừng";
    [ObservableProperty] private string _primaryButtonText = "▶ Bắt đầu";

    // ---- cơ chế chạy (lưu ngay vào cài đặt, dispatcher đọc lại mỗi vòng nên có hiệu lực ngay)
    public string[] StrategyOptions { get; } =
    {
        "Chia đều: chạy hết các tài khoản rồi mới sang lượt 2",
        "Hết từng tài khoản: dùng hết tài khoản này mới sang tài khoản kế",
    };

    [ObservableProperty] private int _strategyIndex;
    [ObservableProperty] private int _maxThreads = 2;
    [ObservableProperty] private bool _hideWindow;
    [ObservableProperty] private bool _skipFailed = true;

    private bool _loadingRunOptions;

    partial void OnStrategyIndexChanged(int value) => SaveRunOptions();
    partial void OnMaxThreadsChanged(int value) => SaveRunOptions();
    partial void OnHideWindowChanged(bool value) => SaveRunOptions();
    partial void OnSkipFailedChanged(bool value) => SaveRunOptions();

    private void LoadRunOptions()
    {
        _loadingRunOptions = true;
        var s = _databaseService.GetSettings();
        StrategyIndex = (int)s.AccountStrategy;
        MaxThreads = Math.Clamp(s.ConcurrencyLimit, 1, 30);
        HideWindow = s.HideRenderWindow;
        SkipFailed = s.SkipFailedAccounts;
        _loadingRunOptions = false;
    }

    private void SaveRunOptions()
    {
        if (_loadingRunOptions) return;
        var s = _databaseService.GetSettings();
        s.AccountStrategy = (AccountStrategy)Math.Clamp(StrategyIndex, 0, 1);
        s.ConcurrencyLimit = Math.Clamp(MaxThreads, 1, 30);
        s.HideRenderWindow = HideWindow;
        s.SkipFailedAccounts = SkipFailed;
        _databaseService.SaveSettings(s);
    }

    /// <summary>Thư mục lưu video tải về (mặc định thư mục Videos). Lưu ngay vào cài đặt khi đổi.</summary>
    [ObservableProperty] private string _saveDirectory = string.Empty;

    private bool _loadingSaveDirectory;

    partial void OnSaveDirectoryChanged(string value)
    {
        if (_loadingSaveDirectory || string.IsNullOrWhiteSpace(value)) return;
        var settings = _databaseService.GetSettings();
        settings.DownloadDirectory = value.Trim();
        _databaseService.SaveSettings(settings);
    }

    public QueueViewModel(
        ITaskDispatcher dispatcher,
        IDatabaseService databaseService,
        INotificationService notificationService)
    {
        _dispatcher = dispatcher;
        _databaseService = databaseService;
        _notificationService = notificationService;

        _dispatcher.LogReceived += OnLogReceived;
        _dispatcher.TaskUpdated += OnTaskUpdated;
        _dispatcher.AllTasksCompleted += OnAllTasksCompleted;

        _loadingSaveDirectory = true;
        SaveDirectory = _databaseService.GetSettings().DownloadDirectory;
        _loadingSaveDirectory = false;
        LoadRunOptions();

        var view = new ListCollectionView(Tasks) { Filter = FilterTask };
        view.CustomSort = new TaskOrder();
        View = view;

        LoadTasks();
        SyncRunState();
    }

    // ------------------------------------------------------------------ thứ tự & lọc

    /// <summary>Đang chạy → đang chờ (ưu tiên cao trước, cùng mức thì tạo trước) → lỗi → hủy → xong (mới nhất trước).</summary>
    private sealed class TaskOrder : System.Collections.IComparer
    {
        private static int Group(RenderTask t) => t.Status switch
        {
            RenderTaskStatus.Processing or RenderTaskStatus.Downloading or RenderTaskStatus.Queued => 0,
            RenderTaskStatus.Pending => 1,
            RenderTaskStatus.Failed => 2,
            RenderTaskStatus.Cancelled => 3,
            _ => 4,
        };

        public int Compare(object? x, object? y)
        {
            if (x is not RenderTask a || y is not RenderTask b) return 0;
            var g = Group(a).CompareTo(Group(b));
            if (g != 0) return g;
            if (Group(a) <= 1)
            {
                var p = b.Priority.CompareTo(a.Priority);
                return p != 0 ? p : a.CreatedAt.CompareTo(b.CreatedAt);
            }
            return (b.FinishedAt ?? b.CreatedAt).CompareTo(a.FinishedAt ?? a.CreatedAt);
        }
    }

    private bool FilterTask(object o)
    {
        if (o is not RenderTask t) return false;
        var statusOk = StatusFilterIndex switch
        {
            1 => t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued,
            2 => t.Status is RenderTaskStatus.Processing or RenderTaskStatus.Downloading,
            3 => t.Status == RenderTaskStatus.Completed,
            4 => t.Status == RenderTaskStatus.Failed,
            5 => t.Status == RenderTaskStatus.Cancelled,
            _ => true,
        };
        if (!statusOk) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return t.Prompt.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (t.PromptTitle?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
               || (t.AssignedSessionName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private List<RenderTask> Selected() => Tasks.Where(t => t.IsSelected).ToList();

    // ------------------------------------------------------------------ sự kiện từ dispatcher

    private void OnLogReceived(string log)
    {
        Application.Current?.Dispatcher.Invoke(() => LogOutput += log + Environment.NewLine);
    }

    private void OnTaskUpdated(RenderTask task)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (existing != null && ReferenceEquals(existing, task))
            {
                // Cùng một đối tượng đã được dispatcher sửa tại chỗ: vẽ lại dòng và xếp lại vị trí
                task.NotifyChanged();
                ResortItem(task);
            }
            else if (existing != null)
            {
                task.IsSelected = existing.IsSelected; // giữ dấu tích khi dòng được nạp lại
                Tasks[Tasks.IndexOf(existing)] = task;
            }
            else
            {
                Tasks.Add(task);
            }
            UpdateCounters();
        });
    }

    private void ResortItem(RenderTask task)
    {
        if (View is ListCollectionView lcv && !lcv.IsAddingNew && !lcv.IsEditingItem)
        {
            try { lcv.EditItem(task); lcv.CommitEdit(); }
            catch (InvalidOperationException) { View.Refresh(); }
        }
        else
        {
            View.Refresh();
        }
    }

    private void OnAllTasksCompleted()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            SyncRunState();
            if (_databaseService.GetSettings().EnableToastNotification)
            {
                _notificationService.ShowToast(
                    "Hoàn tất điều phối Dola AI",
                    $"Đã xử lý xong hàng đợi ({CompletedCount} video thành công, {FailedCount} lỗi).");
            }
        });
    }

    public void LoadTasks()
    {
        var selected = Tasks.Where(t => t.IsSelected).Select(t => t.Id).ToHashSet();
        Tasks.Clear();
        foreach (var t in _databaseService.GetAllTasks())
        {
            t.IsSelected = selected.Contains(t.Id);
            Tasks.Add(t);
        }
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        PendingCount = Tasks.Count(t => t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued);
        ProcessingCount = Tasks.Count(t => t.Status is RenderTaskStatus.Processing or RenderTaskStatus.Downloading);
        CompletedCount = Tasks.Count(t => t.Status == RenderTaskStatus.Completed);
        FailedCount = Tasks.Count(t => t.Status == RenderTaskStatus.Failed);
        SyncRunState();
        UpdateBatch();
    }

    /// <summary>
    /// "Đợt" = các tác vụ đã chờ/chạy kể từ lần gần nhất hàng đợi trống. Xong / lỗi / hủy đều tính là 100% của tác vụ đó
    /// nên thanh chạm 100% khi cả đợt đã kết thúc (số lỗi ghi ở dòng chữ bên cạnh).
    /// </summary>
    private void UpdateBatch()
    {
        var active = Tasks.Where(t => t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued or RenderTaskStatus.Processing or RenderTaskStatus.Downloading).ToList();
        ActiveCount = active.Count;
        if (active.Count > 0 && _lastActive == 0) _batch.Clear(); // có việc mới sau lúc trống: bắt đầu đợt mới
        foreach (var t in active) _batch.Add(t.Id);
        _lastActive = active.Count;
        var present = Tasks.Select(t => t.Id).ToHashSet();
        _batch.RemoveWhere(id => !present.Contains(id));

        var items = Tasks.Where(t => _batch.Contains(t.Id)).ToList();
        BatchCount = items.Count;
        if (items.Count == 0) { OverallPercent = 0; OverallText = string.Empty; return; }

        double sum = items.Sum(t => t.Status switch
        {
            RenderTaskStatus.Completed or RenderTaskStatus.Failed or RenderTaskStatus.Cancelled => 100,
            RenderTaskStatus.Processing or RenderTaskStatus.Downloading => Math.Clamp(t.ProgressPercent, 0, 100),
            _ => 0,
        });
        OverallPercent = sum / items.Count;

        var done = items.Count(t => t.Status == RenderTaskStatus.Completed);
        var failed = items.Count(t => t.Status == RenderTaskStatus.Failed);
        var cancelled = items.Count(t => t.Status == RenderTaskStatus.Cancelled);
        var running = items.Count(t => t.Status is RenderTaskStatus.Processing or RenderTaskStatus.Downloading);
        var waiting = items.Count(t => t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued);
        OverallText = $"{done}/{items.Count} video xong"
                      + (failed > 0 ? $" · {failed} lỗi" : string.Empty)
                      + (cancelled > 0 ? $" · {cancelled} hủy" : string.Empty)
                      + (running > 0 ? $" · {running} đang chạy" : string.Empty)
                      + (waiting > 0 ? $" · {waiting} đang chờ" : string.Empty)
                      + (IsPaused && (running + waiting) > 0 ? " · đang tạm dừng" : string.Empty)
                      + (running + waiting == 0 ? " · HOÀN TẤT" : string.Empty);
    }

    private void SyncRunState()
    {
        IsPaused = _dispatcher.IsPaused;
        IsQueueRunning = _dispatcher.IsRunning && !IsPaused && (PendingCount > 0 || ProcessingCount > 0 || _dispatcher.ActiveWorkersCount > 0);
        var loopRunning = _dispatcher.IsRunning;

        if (IsPaused)
        {
            RunStateText = ProcessingCount > 0 ? "Đang tạm dừng — các video đang làm sẽ chạy nốt" : "Đang tạm dừng";
            PrimaryButtonText = "▶ Tiếp tục";
        }
        else if (loopRunning && (PendingCount > 0 || ProcessingCount > 0))
        {
            RunStateText = "Đang chạy";
            PrimaryButtonText = "⏸ Tạm dừng";
        }
        else if (loopRunning)
        {
            RunStateText = "Đang chờ tác vụ mới";
            PrimaryButtonText = "⏸ Tạm dừng";
        }
        else
        {
            RunStateText = PendingCount > 0 ? "Đã dừng — còn tác vụ đang chờ" : "Đã dừng";
            PrimaryButtonText = "▶ Bắt đầu";
        }
    }

    // ------------------------------------------------------------------ nhận việc từ "Quản lý prompt"

    /// <summary>Đưa các tác vụ vào hàng đợi (theo độ ưu tiên đã đặt trong từng tác vụ) và bật điều phối nếu chưa chạy.</summary>
    public async Task EnqueueAsync(IEnumerable<RenderTask> tasks)
    {
        foreach (var task in tasks)
        {
            task.Status = RenderTaskStatus.Pending;
            _dispatcher.EnqueueTask(task);
        }
        LoadTasks();
        if (!_dispatcher.IsRunning) await _dispatcher.StartAsync();
        else if (_dispatcher.IsPaused) Log("Điều phối đang tạm dừng: các video mới sẽ chờ đến khi bạn bấm Tiếp tục.");
        SyncRunState();
    }

    private void Log(string message) => LogOutput += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";

    // ------------------------------------------------------------------ điều khiển chung

    /// <summary>Nút chính: Bắt đầu → Tạm dừng → Tiếp tục.</summary>
    [RelayCommand]
    private async Task TogglePrimaryAsync()
    {
        if (_dispatcher.IsPaused)
        {
            _dispatcher.Resume();
        }
        else if (_dispatcher.IsRunning)
        {
            _dispatcher.Pause();
        }
        else
        {
            await _dispatcher.StartAsync();
        }
        SyncRunState();
    }

    /// <summary>Dừng hẳn: hủy cả các tác vụ đang chạy dở (khác với Tạm dừng).</summary>
    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (!_dispatcher.IsRunning && ProcessingCount == 0) return;
        if (ProcessingCount > 0 &&
            MessageBox.Show($"Dừng hẳn sẽ HỦY {ProcessingCount} video đang làm dở (có thể đã tốn lượt).\nChỉ muốn ngưng nhận việc mới thì dùng 'Tạm dừng'.\n\nDừng hẳn?",
                "Dừng điều phối", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        await _dispatcher.StopAsync();
        LoadTasks();
        SyncRunState();
    }

    // ------------------------------------------------------------------ thao tác trên các dòng đã tích

    private bool RequireSelection(out List<RenderTask> selected, string what)
    {
        selected = Selected();
        if (selected.Count > 0) return true;
        MessageBox.Show($"Tích chọn ít nhất một tác vụ (ô vuông đầu dòng) để {what}.", "Vận hành", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static bool NotStarted(RenderTask t) => t.Status is RenderTaskStatus.Pending or RenderTaskStatus.Queued;

    [RelayCommand]
    private void CancelSelected()
    {
        if (!RequireSelection(out var selected, "dừng")) return;
        var targets = selected.Where(t => t.Status is not (RenderTaskStatus.Completed or RenderTaskStatus.Failed or RenderTaskStatus.Cancelled)).ToList();
        if (targets.Count == 0) { Log("Các tác vụ đã tích đã kết thúc, không có gì để dừng."); return; }
        foreach (var t in targets) _dispatcher.CancelTask(t.Id);
    }

    [RelayCommand]
    private async Task RetrySelectedAsync()
    {
        if (!RequireSelection(out var selected, "thử lại")) return;
        var targets = selected.Where(t => t.Status is RenderTaskStatus.Failed or RenderTaskStatus.Cancelled or RenderTaskStatus.Completed).ToList();
        if (targets.Count == 0) { Log("Chỉ thử lại được tác vụ đã lỗi / đã hủy / đã xong."); return; }
        foreach (var t in targets) await _dispatcher.RetryTaskAsync(t.Id);
        if (!_dispatcher.IsRunning) await _dispatcher.StartAsync();
        SyncRunState();
    }

    /// <summary>Đưa lên đầu hàng đợi: cao hơn mọi tác vụ đang chờ.</summary>
    [RelayCommand]
    private void PrioritizeTop()
    {
        if (!RequireSelection(out var selected, "đưa lên đầu")) return;
        var targets = selected.Where(NotStarted).ToList();
        if (targets.Count == 0) { Log("Chỉ đổi được ưu tiên của tác vụ đang chờ."); return; }
        var top = Tasks.Where(NotStarted).Select(t => t.Priority).DefaultIfEmpty(0).Max() + 1;
        foreach (var t in targets) _dispatcher.SetPriority(t.Id, top);
        Log($"Đã đưa {targets.Count} tác vụ lên đầu hàng đợi.");
    }

    [RelayCommand]
    private void RaisePriority() => ChangePriority(+1);

    [RelayCommand]
    private void LowerPriority() => ChangePriority(-1);

    private void ChangePriority(int delta)
    {
        if (!RequireSelection(out var selected, "đổi ưu tiên")) return;
        var targets = selected.Where(NotStarted).ToList();
        if (targets.Count == 0) { Log("Chỉ đổi được ưu tiên của tác vụ đang chờ."); return; }
        foreach (var t in targets) _dispatcher.SetPriority(t.Id, Math.Clamp(t.Priority + delta, -5, 50));
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (!RequireSelection(out var selected, "xem video")) return;
        var target = selected.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.LocalFilePath) && File.Exists(t.LocalFilePath));
        if (target == null)
        {
            MessageBox.Show("Các tác vụ đã tích chưa có video tải về (hoặc file không còn).", "Vận hành", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = target.LocalFilePath!, UseShellExecute = true });
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        var picked = Selected().FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.LocalFilePath) && File.Exists(t.LocalFilePath));
        if (picked != null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{picked.LocalFilePath}\"") { UseShellExecute = true });
        else
            OpenDownloadFolder();
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (!RequireSelection(out var selected, "xóa")) return;
        if (MessageBox.Show($"Xóa {selected.Count} tác vụ đã tích khỏi danh sách?\n(Tác vụ đang chạy sẽ bị hủy; video đã tải về vẫn còn trong thư mục.)",
                "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var t in selected)
        {
            _dispatcher.CancelTask(t.Id);
            Tasks.Remove(t);
        }
        _databaseService.DeleteTasks(selected.Select(s => s.Id));
        UpdateCounters();
        IsAllSelected = false;
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _databaseService.DeleteCompletedTasks();
        LoadTasks();
    }

    [RelayCommand]
    private void ClearFailed()
    {
        _databaseService.DeleteFailedTasks();
        LoadTasks();
    }

    // ------------------------------------------------------------------ thư mục lưu & nhật ký

    [RelayCommand]
    private void BrowseSaveDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục lưu video",
            InitialDirectory = Directory.Exists(SaveDirectory) ? SaveDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dialog.ShowDialog() == true) SaveDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        var dir = _databaseService.GetSettings().DownloadDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    [RelayCommand]
    private void ClearLogs() => LogOutput = string.Empty;
}
