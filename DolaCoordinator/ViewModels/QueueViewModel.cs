using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Notification;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Storage;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

public partial class QueueViewModel : ObservableObject
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly IDatabaseService _databaseService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private ObservableCollection<RenderTask> _tasks = new();

    [ObservableProperty]
    private RenderTask? _selectedTask;

    [ObservableProperty]
    private string _promptInput = string.Empty;

    [ObservableProperty]
    private string _selectedRatio = "9:16";

    [ObservableProperty]
    private int _selectedDuration = 30;

    [ObservableProperty]
    private bool _isQueueRunning;

    [ObservableProperty]
    private bool _isAllSelected;

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var task in Tasks)
        {
            task.IsSelected = value;
        }
    }

    [ObservableProperty]
    private string _logOutput = string.Empty;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _processingCount;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _failedCount;

    /// <summary>Ảnh tham chiếu (tùy chọn) áp dụng cho mọi prompt thêm vào hàng đợi lần này.</summary>
    public ObservableCollection<string> ReferenceImagePaths { get; } = new();

    private const int MaxReferenceImages = 30; // giới hạn của gateway (DOLA_REFERENCE_IMAGE_MAX_COUNT)

    /// <summary>Thư mục lưu video tải về (mặc định thư mục Videos). Lưu ngay vào cài đặt khi đổi.</summary>
    [ObservableProperty]
    private string _saveDirectory = string.Empty;

    private bool _loadingSaveDirectory;

    partial void OnSaveDirectoryChanged(string value)
    {
        if (_loadingSaveDirectory || string.IsNullOrWhiteSpace(value)) return;
        var settings = _databaseService.GetSettings();
        settings.DownloadDirectory = value.Trim();
        _databaseService.SaveSettings(settings);
    }

    public string[] RatioOptions { get; } = ["9:16", "16:9", "1:1"];
    public int[] DurationOptions { get; } = [10, 15, 30];

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

        LoadTasks();
    }

    private void OnLogReceived(string log)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogOutput += log + Environment.NewLine;
        });
    }

    private void OnTaskUpdated(RenderTask task)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var existing = Tasks.FirstOrDefault(t => t.Id == task.Id);
            if (existing != null)
            {
                var index = Tasks.IndexOf(existing);
                Tasks[index] = task;
            }
            else
            {
                Tasks.Insert(0, task);
            }
            UpdateCounters();
        });
    }

    private void OnAllTasksCompleted()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsQueueRunning = false;
            var settings = _databaseService.GetSettings();
            if (settings.EnableToastNotification)
            {
                _notificationService.ShowToast(
                    "Hoàn tất điều phối Dola AI",
                    $"Đã hoàn thành toàn bộ hàng đợi tác vụ ({CompletedCount} video thành công)!"
                );
            }
        });
    }

    public void LoadTasks()
    {
        var list = _databaseService.GetAllTasks();
        Tasks = new ObservableCollection<RenderTask>(list);
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        PendingCount = Tasks.Count(t => t.Status == RenderTaskStatus.Pending || t.Status == RenderTaskStatus.Queued);
        ProcessingCount = Tasks.Count(t => t.Status == RenderTaskStatus.Processing || t.Status == RenderTaskStatus.Downloading);
        CompletedCount = Tasks.Count(t => t.Status == RenderTaskStatus.Completed);
        FailedCount = Tasks.Count(t => t.Status == RenderTaskStatus.Failed);
    }

    [RelayCommand]
    private void AddPromptsToQueue()
    {
        if (string.IsNullOrWhiteSpace(PromptInput))
        {
            MessageBox.Show("Vui lòng nhập ít nhất một prompt kịch bản.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var lines = PromptInput.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        int added = 0;

        foreach (var raw in lines)
        {
            var prompt = raw.Trim();
            if (string.IsNullOrWhiteSpace(prompt) || prompt.StartsWith("#")) continue;

            var task = new RenderTask
            {
                Prompt = prompt,
                Ratio = SelectedRatio,
                Duration = SelectedDuration,
                ReferenceLocalPaths = ReferenceImagePaths.ToList(),
                Status = RenderTaskStatus.Pending
            };

            _dispatcher.EnqueueTask(task);
            added++;
        }

        PromptInput = string.Empty;
        LoadTasks();

        if (!IsQueueRunning)
        {
            _ = ToggleQueue();
        }
    }

    [RelayCommand]
    private void AddReferenceImages()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",
            Title = "Chọn ảnh tham chiếu",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var skipped = 0;
        foreach (var file in dialog.FileNames)
        {
            if (ReferenceImagePaths.Contains(file, StringComparer.OrdinalIgnoreCase)) continue;
            if (ReferenceImagePaths.Count >= MaxReferenceImages) { skipped++; continue; }
            ReferenceImagePaths.Add(file);
        }
        if (skipped > 0)
            MessageBox.Show($"Tối đa {MaxReferenceImages} ảnh tham chiếu. Đã bỏ qua {skipped} ảnh.", "Ảnh tham chiếu", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void RemoveReferenceImage(string? path)
    {
        if (path != null) ReferenceImagePaths.Remove(path);
    }

    [RelayCommand]
    private void ClearReferenceImages() => ReferenceImagePaths.Clear();

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
    private void ImportPromptsFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Title = "Nhập Kịch Bản Từ File"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dialog.FileName, Encoding.UTF8);
            int added = 0;

            foreach (var raw in lines)
            {
                var prompt = raw.Trim();
                if (string.IsNullOrWhiteSpace(prompt) || prompt.StartsWith("#")) continue;

                var task = new RenderTask
                {
                    Prompt = prompt,
                    Ratio = SelectedRatio,
                    Duration = SelectedDuration,
                    ReferenceLocalPaths = ReferenceImagePaths.ToList(),
                    Status = RenderTaskStatus.Pending
                };

                _dispatcher.EnqueueTask(task);
                added++;
            }

            LoadTasks();
            MessageBox.Show($"Đã thêm {added} kịch bản vào hàng đợi!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);

            if (!IsQueueRunning)
            {
                _ = ToggleQueue();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi nhập kịch bản: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ToggleQueue()
    {
        if (IsQueueRunning)
        {
            await StopQueue();
        }
        else
        {
            await StartQueue();
        }
    }

    [RelayCommand]
    private async Task StartQueue()
    {
        if (IsQueueRunning) return;

        // Enqueue all pending tasks in database that were not yet in channel
        var pendingTasks = Tasks.Where(t => t.Status == RenderTaskStatus.Pending).ToList();
        if (pendingTasks.Count > 0)
        {
            _dispatcher.EnqueueTasks(pendingTasks);
        }

        await _dispatcher.StartAsync();
        IsQueueRunning = true;
    }

    [RelayCommand]
    private async Task StopQueue()
    {
        if (!IsQueueRunning) return;
        
        await _dispatcher.StopAsync();
        IsQueueRunning = false;
    }

    [RelayCommand]
    private async Task RetryTask(object? obj = null)
    {
        var task = obj as RenderTask;
        var target = task ?? SelectedTask;
        if (target == null) return;
        await _dispatcher.RetryTaskAsync(target.Id);
    }

    [RelayCommand]
    private void CancelSelectedTask(object? obj = null)
    {
        var task = obj as RenderTask;
        var target = task ?? SelectedTask;
        if (target == null) return;
        _dispatcher.CancelTask(target.Id);
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

    [RelayCommand]
    private void DeleteSingleTask(object? obj = null)
    {
        var task = obj as RenderTask;
        var target = task ?? SelectedTask;
        if (target == null) return;

        _dispatcher.CancelTask(target.Id);
        _databaseService.DeleteTask(target.Id);
        Tasks.Remove(target);
        UpdateCounters();
    }

    [RelayCommand]
    private void DeleteSelectedTasks()
    {
        var selected = Tasks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Vui lòng tích chọn ít nhất một tác vụ để xóa.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show($"Bạn có chắc chắn muốn xóa {selected.Count} tác vụ đã chọn?", "Xác nhận xóa hàng loạt", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        foreach (var task in selected)
        {
            _dispatcher.CancelTask(task.Id);
            Tasks.Remove(task);
        }

        _databaseService.DeleteTasks(selected.Select(s => s.Id));
        UpdateCounters();
        IsAllSelected = false;
    }

    [RelayCommand]
    private async Task ClearAllTasks()
    {
        if (Tasks.Count == 0) return;

        var result = MessageBox.Show("Bạn có chắc chắn muốn XÓA TOÀN BỘ hàng đợi tác vụ không?", "Xác nhận xóa hàng đợi", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        await _dispatcher.StopAsync();
        IsQueueRunning = false;

        _databaseService.ClearAllTasks();
        Tasks.Clear();
        UpdateCounters();
        IsAllSelected = false;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            LogOutput += $"[{DateTime.Now:HH:mm:ss}] Đã xóa toàn bộ hàng đợi tác vụ." + Environment.NewLine;
        });
    }

    [RelayCommand]
    private void OpenDownloadFolder()
    {
        var settings = _databaseService.GetSettings();
        var dir = settings.DownloadDirectory;
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void PlaySelectedVideo(object? obj = null)
    {
        var task = obj as RenderTask;
        var target = task ?? SelectedTask;
        if (target == null || string.IsNullOrWhiteSpace(target.LocalFilePath) || !File.Exists(target.LocalFilePath))
        {
            MessageBox.Show("Video chưa được tải về hoặc file không tồn tại.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = target.LocalFilePath,
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ClearLogs()
    {
        LogOutput = string.Empty;
    }

    [RelayCommand]
    private void MergeVideos()
    {
        MessageBox.Show("Tính năng Nối Video đang được phát triển. Vui lòng chờ các bản cập nhật tiếp theo!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private async Task RetrySelectedTasks()
    {
        var selected = Tasks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("Vui lòng tích chọn ít nhất một tác vụ để tạo lại.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var task in selected)
        {
            await _dispatcher.RetryTaskAsync(task.Id);
        }
        IsAllSelected = false;
        MessageBox.Show($"Đã đưa {selected.Count} tác vụ vào lại hàng đợi thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private async Task RetryFailedTasks()
    {
        var failed = Tasks.Where(t => t.Status == RenderTaskStatus.Failed).ToList();
        if (failed.Count == 0)
        {
            MessageBox.Show("Không có tác vụ lỗi nào để tạo lại.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var task in failed)
        {
            await _dispatcher.RetryTaskAsync(task.Id);
        }
        MessageBox.Show($"Đã đưa {failed.Count} tác vụ lỗi vào lại hàng đợi thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void OpenZaloGroup()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://zalo.me/g/your_group_link", // Thay bằng link thật nếu có
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi mở link Zalo: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
