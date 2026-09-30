using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Queue;
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Views.Dialogs;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

/// <summary>Một lựa chọn trong ô lọc trạng thái (cần là thuộc tính để ComboBox đọc được).</summary>
public sealed record StatusOption(string Key, string Label);

/// <summary>
/// Trang "Quản lý prompt": thư viện prompt (nhiều dòng, dạng bảng…) — soạn, nhập/xuất, nhân bản, xóa và
/// "Thêm vào hàng đợi" để làm video. Việc chạy / dừng / ưu tiên do trang Tạo video lo.
/// </summary>
public partial class PromptsViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly QueueViewModel _queue;
    private readonly ITaskDispatcher _dispatcher;

    /// <summary>Hai trang con của mục Quản lý prompt: 0 = Prompt, 1 = Kịch bản lớn.</summary>
    [ObservableProperty] private int _subTab;

    /// <summary>Lọc theo trạng thái: all | none | active | done | failed.</summary>
    [ObservableProperty] private string _statusFilter = "all";

    [ObservableProperty] private int _activePromptCount;
    [ObservableProperty] private int _donePromptCount;
    [ObservableProperty] private int _failedPromptCount;

    public IReadOnlyList<StatusOption> StatusFilters { get; } = new[]
    {
        new StatusOption("all", "Mọi trạng thái"), new StatusOption("none", "Chưa làm"), new StatusOption("active", "Đang làm"),
        new StatusOption("done", "Đã xong"), new StatusOption("failed", "Có lỗi"),
    };

    partial void OnStatusFilterChanged(string value) => View.Refresh();

    public ObservableCollection<PromptItem> Prompts { get; } = new();
    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isAllSelected;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _usedCount;
    [ObservableProperty] private string _statusText = string.Empty;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnIsAllSelectedChanged(bool value)
    {
        foreach (var p in View.Cast<PromptItem>()) p.IsSelected = value;
        UpdateCounters();
    }

    public PromptsViewModel(IDatabaseService db, QueueViewModel queue, ITaskDispatcher dispatcher)
    {
        _db = db;
        _queue = queue;
        _dispatcher = dispatcher;

        var view = new ListCollectionView(Prompts) { Filter = FilterPrompt };
        view.SortDescriptions.Add(new SortDescription(nameof(PromptItem.UpdatedAt), ListSortDirection.Descending));
        View = view;

        foreach (var p in _db.GetAllPrompts())
        {
            if (!PromptFileParser.Durations.Contains(p.Duration) || !PromptFileParser.Ratios.Contains(p.Ratio))
            {
                p.Duration = PromptFileParser.NormalizeDuration(p.Duration.ToString());
                p.Ratio = PromptFileParser.NormalizeRatio(p.Ratio);
                _db.UpsertPrompt(p);
            }
            p.PropertyChanged += OnPromptPropertyChanged;
            Prompts.Add(p);
        }
        ReconcileActive();
        UpdateCounters();
        _dispatcher.TaskUpdated += OnTaskUpdated;
    }

    // ------------------------------------------------------------------ trạng thái của prompt

    private static string? BucketOf(RenderTaskStatus s) => s switch
    {
        RenderTaskStatus.Pending or RenderTaskStatus.Queued or RenderTaskStatus.Processing or RenderTaskStatus.Downloading => "active",
        RenderTaskStatus.Completed => "done",
        RenderTaskStatus.Failed => "failed",
        _ => null, // đã hủy: không tính là xong hay lỗi
    };

    private static void Adjust(PromptItem p, string? bucket, int delta)
    {
        switch (bucket)
        {
            case "active": p.ActiveCount = Math.Max(0, p.ActiveCount + delta); break;
            case "done": p.DoneCount = Math.Max(0, p.DoneCount + delta); break;
            case "failed": p.FailedCount = Math.Max(0, p.FailedCount + delta); break;
        }
    }

    /// <summary>Mỗi lần trạng thái tác vụ đổi, chuyển nó sang đúng nhóm đếm của prompt (chỉ tính một lần cho mỗi lần đổi nhóm).</summary>
    private void OnTaskUpdated(RenderTask task)
    {
        if (string.IsNullOrEmpty(task.PromptId)) return;
        Application.Current?.Dispatcher.InvokeAsync(() => ApplyTaskState(task));
    }

    private void ApplyTaskState(RenderTask task)
    {
        var prompt = Prompts.FirstOrDefault(p => p.Id == task.PromptId);
        if (prompt == null) return;
        var newBucket = BucketOf(task.Status);
        if (newBucket == task.PromptBucket) return;

        Adjust(prompt, task.PromptBucket, -1);
        Adjust(prompt, newBucket, +1);
        task.PromptBucket = newBucket;
        _db.UpsertTask(task);
        _db.UpsertPrompt(prompt);
        prompt.NotifyChanged();
        UpdateCounters();
        if (StatusFilter != "all") View.Refresh();
    }

    /// <summary>Số "đang làm" của mỗi prompt phải khớp các tác vụ còn trong hàng đợi (tác vụ có thể đã bị xóa khỏi hàng đợi).</summary>
    public void ReconcileActive()
    {
        var active = _db.GetAllTasks()
            .Where(t => !string.IsNullOrEmpty(t.PromptId) && BucketOf(t.Status) == "active")
            .GroupBy(t => t.PromptId!)
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var p in Prompts)
        {
            var actual = active.TryGetValue(p.Id, out var n) ? n : 0;
            if (p.ActiveCount == actual) continue;
            p.ActiveCount = actual;
            _db.UpsertPrompt(p);
            p.NotifyChanged();
        }
    }

    [RelayCommand]
    private void RefreshStatuses()
    {
        ReconcileActive();
        UpdateCounters();
        View.Refresh();
    }

    /// <summary>Xóa các prompt có trạng thái đã chọn (đã xong / lỗi / chưa làm) sau khi hỏi lại.</summary>
    private void DeleteByStatus(string what, Func<PromptItem, bool> match)
    {
        var victims = Prompts.Where(match).ToList();
        if (victims.Count == 0) { StatusText = $"Không có prompt nào {what}."; return; }
        var question = $"Xóa {victims.Count} prompt {what}?" + Environment.NewLine + "(Video đã tạo vẫn nằm trong thư mục lưu video.)";
        if (MessageBox.Show(question, "Xóa prompt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _db.DeletePrompts(victims.Select(v => v.Id));
        foreach (var v in victims) Prompts.Remove(v);
        UpdateCounters();
        StatusText = $"Đã xóa {victims.Count} prompt {what}.";
    }

    [RelayCommand] private void DeleteDone() => DeleteByStatus("đã xong", p => p.StatusKey == "done");
    [RelayCommand] private void DeleteFailed() => DeleteByStatus("bị lỗi", p => p.StatusKey is "failed" or "partial");
    [RelayCommand] private void DeleteUnused() => DeleteByStatus("chưa làm video nào", p => p.StatusKey == "none");

    private bool FilterPrompt(object o)
    {
        if (o is not PromptItem p) return false;
        if (StatusFilter != "all")
        {
            var ok = StatusFilter switch
            {
                "none" => p.StatusKey == "none",
                "active" => p.StatusKey == "active",
                "done" => p.StatusKey is "done" or "partial",
                "failed" => p.StatusKey is "failed" or "partial",
                _ => true,
            };
            if (!ok) return false;
        }
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var q = SearchText.Trim();
        return p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || p.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
               || (p.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void OnPromptPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PromptItem.IsSelected)) UpdateCounters();
    }

    private List<PromptItem> Selected() => Prompts.Where(p => p.IsSelected).ToList();

    private void UpdateCounters()
    {
        TotalCount = Prompts.Count;
        SelectedCount = Prompts.Count(p => p.IsSelected);
        UsedCount = Prompts.Count(p => p.QueuedCount > 0);
        ActivePromptCount = Prompts.Count(p => p.StatusKey == "active");
        DonePromptCount = Prompts.Count(p => p.StatusKey is "done" or "partial");
        FailedPromptCount = Prompts.Count(p => p.StatusKey is "failed" or "partial");
    }

    private void Add(PromptItem p)
    {
        p.PropertyChanged += OnPromptPropertyChanged;
        Prompts.Add(p);
    }

    // ------------------------------------------------------------------ thêm / sửa / nhân bản / xóa

    [RelayCommand]
    private void ShowRules() => new PromptRulesWindow { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private void AddPrompt()
    {
        var dlg = new PromptEditorWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var p = new PromptItem
        {
            Title = dlg.PromptTitle.Trim(),
            Text = dlg.PromptText.Trim('\r', '\n'),
            Ratio = dlg.Ratio,
            Duration = dlg.Duration,
            Model = dlg.Model,
            Notes = string.IsNullOrWhiteSpace(dlg.Notes) ? null : dlg.Notes.Trim(),
            ReferenceLocalPaths = dlg.RefImages.ToList(),
            Characters = dlg.Characters,
            SceneText = dlg.SceneText,
            SceneImages = dlg.SceneImages,
        };
        _db.UpsertPrompt(p);
        Add(p);
        UpdateCounters();
        StatusText = $"Đã lưu prompt '{p.Title}'.";
    }

    [RelayCommand]
    private void EditSelected()
    {
        var picked = Selected();
        if (picked.Count != 1)
        {
            MessageBox.Show(picked.Count == 0 ? "Tích chọn 1 prompt (ô vuông đầu dòng) rồi bấm Sửa." : "Chỉ sửa được từng prompt một. Hãy tích đúng 1 dòng.",
                "Sửa prompt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Edit(picked[0]);
    }

    /// <summary>Sửa một prompt cụ thể (bấm đúp vào dòng).</summary>
    public void Edit(PromptItem p)
    {
        var dlg = new PromptEditorWindow(p) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        p.Title = dlg.PromptTitle.Trim();
        p.Text = dlg.PromptText.Trim('\r', '\n');
        p.Ratio = dlg.Ratio;
        p.Duration = dlg.Duration;
        p.Model = dlg.Model;
        p.Notes = string.IsNullOrWhiteSpace(dlg.Notes) ? null : dlg.Notes.Trim();
        p.ReferenceLocalPaths = dlg.RefImages.ToList();
        p.Characters = dlg.Characters;
        p.SceneText = dlg.SceneText;
        p.SceneImages = dlg.SceneImages;
        p.UpdatedAt = DateTime.UtcNow;
        _db.UpsertPrompt(p);
        p.NotifyChanged();
        View.Refresh();
        StatusText = $"Đã lưu thay đổi của '{p.Title}'.";
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        var picked = Selected();
        if (picked.Count == 0) { StatusText = "Tích chọn prompt cần nhân bản."; return; }
        foreach (var src in picked)
        {
            Add(new PromptItem
            {
                Title = src.Title + " (bản sao)",
                Text = src.Text,
                Ratio = src.Ratio,
                Duration = src.Duration,
                Model = src.Model,
                Notes = src.Notes,
                ReferenceLocalPaths = src.ReferenceLocalPaths.ToList(),
                Characters = src.Characters.Select(c => new PromptCharacter { Name = c.Name, Description = c.Description, Images = c.Images.ToList() }).ToList(),
                SceneText = src.SceneText,
                SceneImages = src.SceneImages.ToList(),
            });
            _db.UpsertPrompt(Prompts[^1]);
        }
        UpdateCounters();
        StatusText = $"Đã nhân bản {picked.Count} prompt.";
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        var picked = Selected();
        if (picked.Count == 0) { StatusText = "Tích chọn prompt cần xóa."; return; }
        if (MessageBox.Show($"Xóa {picked.Count} prompt đã tích khỏi thư viện?\n(Các video đã nằm trong hàng đợi không bị ảnh hưởng.)",
                "Xác nhận xóa", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _db.DeletePrompts(picked.Select(p => p.Id));
        foreach (var p in picked)
        {
            p.PropertyChanged -= OnPromptPropertyChanged;
            Prompts.Remove(p);
        }
        IsAllSelected = false;
        UpdateCounters();
        StatusText = $"Đã xóa {picked.Count} prompt.";
    }

    // ------------------------------------------------------------------ nhập / xuất

    [RelayCommand]
    private void ImportFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Nhập prompt từ file",
            Filter = "Excel / CSV / văn bản (*.xlsx;*.csv;*.tsv;*.txt;*.md)|*.xlsx;*.csv;*.tsv;*.txt;*.md|Tất cả (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        var added = 0;
        var problems = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                foreach (var p in PromptFileParser.Parse(file))
                {
                    _db.UpsertPrompt(p);
                    Add(p);
                    added++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.DecoderFallbackException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        UpdateCounters();
        View.Refresh();
        StatusText = $"Đã nhập {added} prompt." + (problems.Count > 0 ? " Lỗi: " + string.Join("; ", problems) : string.Empty);
        if (added == 0 && problems.Count == 0)
            MessageBox.Show("Không tìm thấy prompt nào trong file.\n\n• Excel / .csv: cần cột 'Nội dung' (hoặc 'Prompt'); ô nhiều dòng trong Excel xuống dòng bằng Alt+Enter, trong CSV phải nằm trong dấu ngoặc kép. Tải 'File mẫu' để xem đầy đủ.\n• .txt / .md: các prompt cách nhau bằng một dòng chỉ có ---; không có dòng đó thì cả file là một prompt.",
                "Nhập prompt", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    [RelayCommand]
    private void ExportToFile()
    {
        var targets = Selected();
        if (targets.Count == 0) targets = View.Cast<PromptItem>().ToList();
        if (targets.Count == 0) { StatusText = "Chưa có prompt nào để xuất."; return; }

        var dialog = new SaveFileDialog
        {
            Title = "Xuất prompt",
            Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = $"prompts_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SavePromptFile(dialog.FileName, targets);
            StatusText = $"Đã xuất {targets.Count} prompt ra {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Không ghi được file (đang mở trong Excel?): {ex.Message}", "Xuất prompt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>.xlsx → Excel (kèm sheet hướng dẫn); còn lại → CSV UTF-8 có BOM để Excel đọc đúng tiếng Việt.</summary>
    private static void SavePromptFile(string path, IEnumerable<PromptItem> items)
    {
        if (TableFile.IsExcel(path)) ImportTemplates.WritePromptWorkbook(path, items);
        else File.WriteAllText(path, PromptFileParser.ToCsv(items), new UTF8Encoding(true));
    }

    [RelayCommand]
    private void DownloadTemplate()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Lưu file mẫu nhập prompt",
            Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
            FileName = "mau_nhap_prompt.xlsx",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            SavePromptFile(dialog.FileName, ImportTemplates.SamplePrompts());
            StatusText = $"Đã lưu file mẫu: {dialog.FileName} (có sheet 'Hướng dẫn' ghi rõ từng cột).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Không ghi được file: {ex.Message}", "File mẫu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ thêm vào hàng đợi

    [RelayCommand]
    private async Task AddToQueueAsync()
    {
        var picked = Selected();
        if (picked.Count == 0)
        {
            MessageBox.Show("Tích chọn ít nhất một prompt (ô vuông đầu dòng) rồi bấm 'Thêm vào hàng đợi'.", "Thêm vào hàng đợi",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new AddToQueueWindow(picked.Count) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var tasks = new List<RenderTask>();
        foreach (var p in picked)
        {
            for (var i = 0; i < dlg.Copies; i++)
            {
                // Ghép nhân vật + bối cảnh vào prompt; ảnh được đánh số theo thứ tự gửi (ảnh mặc định → nhân vật → bối cảnh)
                var composed = PromptComposer.Compose(p.Text, p.ReferenceLocalPaths, p.Characters, p.SceneText, p.SceneImages);
                tasks.Add(new RenderTask
                {
                    PromptId = p.Id,
                    PromptBucket = "active",
                    Prompt = composed.Text,
                    PromptTitle = p.Title,
                    Model = dlg.ModelOverride ?? p.Model,
                    Ratio = dlg.RatioOverride ?? p.Ratio,
                    Duration = dlg.DurationOverride ?? p.Duration,
                    ReferenceLocalPaths = composed.Images,
                    Priority = dlg.Priority,
                    CreatedAt = DateTime.UtcNow.AddMilliseconds(tasks.Count), // giữ đúng thứ tự khi cùng độ ưu tiên
                });
            }
            p.LastQueuedAt = DateTime.UtcNow;
            p.QueuedCount += dlg.Copies;
            p.ActiveCount += dlg.Copies;
            _db.UpsertPrompt(p);
            p.NotifyChanged();
        }

        await _queue.EnqueueAsync(tasks);
        UpdateCounters();
        StatusText = $"Đã thêm {tasks.Count} video vào hàng đợi ({picked.Count} prompt).";
        foreach (var p in picked) p.IsSelected = false;
        IsAllSelected = false;

        if (dlg.GoToQueue) WeakReferenceMessenger.Default.Send(new NavigateMessage(0));
    }
}
