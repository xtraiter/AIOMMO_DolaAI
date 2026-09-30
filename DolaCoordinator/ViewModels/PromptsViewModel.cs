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
using DolaCoordinator.Services.Storage;
using DolaCoordinator.Views.Dialogs;
using Microsoft.Win32;

namespace DolaCoordinator.ViewModels;

/// <summary>
/// Trang "Quản lý prompt": thư viện prompt (nhiều dòng, dạng bảng…) — soạn, nhập/xuất, nhân bản, xóa và
/// "Thêm vào hàng đợi" để làm video. Việc chạy / dừng / ưu tiên do trang Vận hành lo.
/// </summary>
public partial class PromptsViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly QueueViewModel _queue;

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

    public PromptsViewModel(IDatabaseService db, QueueViewModel queue)
    {
        _db = db;
        _queue = queue;

        var view = new ListCollectionView(Prompts) { Filter = FilterPrompt };
        view.SortDescriptions.Add(new SortDescription(nameof(PromptItem.UpdatedAt), ListSortDirection.Descending));
        View = view;

        foreach (var p in _db.GetAllPrompts())
        {
            p.PropertyChanged += OnPromptPropertyChanged;
            Prompts.Add(p);
        }
        UpdateCounters();
    }

    private bool FilterPrompt(object o)
    {
        if (o is not PromptItem p) return false;
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
    }

    private void Add(PromptItem p)
    {
        p.PropertyChanged += OnPromptPropertyChanged;
        Prompts.Add(p);
    }

    // ------------------------------------------------------------------ thêm / sửa / nhân bản / xóa

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
            Notes = string.IsNullOrWhiteSpace(dlg.Notes) ? null : dlg.Notes.Trim(),
            ReferenceLocalPaths = dlg.RefImages.ToList(),
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
        p.Notes = string.IsNullOrWhiteSpace(dlg.Notes) ? null : dlg.Notes.Trim();
        p.ReferenceLocalPaths = dlg.RefImages.ToList();
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
                Notes = src.Notes,
                ReferenceLocalPaths = src.ReferenceLocalPaths.ToList(),
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
            Filter = "Prompt (*.csv;*.tsv;*.txt;*.md)|*.csv;*.tsv;*.txt;*.md|Tất cả (*.*)|*.*",
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        UpdateCounters();
        View.Refresh();
        StatusText = $"Đã nhập {added} prompt." + (problems.Count > 0 ? " Lỗi: " + string.Join("; ", problems) : string.Empty);
        if (added == 0 && problems.Count == 0)
            MessageBox.Show("Không tìm thấy prompt nào trong file.\n\n• .csv: cần cột 'Prompt' (hoặc 'Nội dung'); ô nhiều dòng phải nằm trong dấu ngoặc kép.\n• .txt / .md: các prompt cách nhau bằng một dòng chỉ có ---; không có dòng đó thì cả file là một prompt.",
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
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"prompts_{DateTime.Now:yyyyMMdd_HHmm}.csv",
        };
        if (dialog.ShowDialog() != true) return;

        File.WriteAllText(dialog.FileName, PromptFileParser.ToCsv(targets), new UTF8Encoding(true)); // BOM để Excel đọc đúng tiếng Việt
        StatusText = $"Đã xuất {targets.Count} prompt ra {dialog.FileName}.";
    }

    [RelayCommand]
    private void DownloadTemplate()
    {
        var dialog = new SaveFileDialog { Title = "Lưu file mẫu", Filter = "CSV (*.csv)|*.csv", FileName = "prompt_mau.csv" };
        if (dialog.ShowDialog() != true) return;
        var sample = new[]
        {
            new PromptItem { Title = "Cảnh biển hoàng hôn", Text = "Cảnh biển lúc hoàng hôn, sóng nhẹ.\nMáy quay lướt chậm từ trái sang phải.", Ratio = "16:9", Duration = 15, Notes = "ví dụ" },
            new PromptItem { Title = "Phân cảnh 3 bước", Text = "Cảnh 1 | Cô gái mở cửa sổ | 5s\nCảnh 2 | Ánh nắng tràn vào phòng | 5s\nCảnh 3 | Cô mỉm cười nhìn ra xa | 5s", Ratio = "9:16", Duration = 15 },
        };
        File.WriteAllText(dialog.FileName, PromptFileParser.ToCsv(sample), new UTF8Encoding(true));
        StatusText = $"Đã lưu file mẫu: {dialog.FileName}";
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
                tasks.Add(new RenderTask
                {
                    Prompt = p.Text,
                    PromptTitle = p.Title,
                    Ratio = dlg.RatioOverride ?? p.Ratio,
                    Duration = dlg.DurationOverride ?? p.Duration,
                    ReferenceLocalPaths = p.ReferenceLocalPaths.ToList(),
                    Priority = dlg.Priority,
                    CreatedAt = DateTime.UtcNow.AddMilliseconds(tasks.Count), // giữ đúng thứ tự khi cùng độ ưu tiên
                });
            }
            p.LastQueuedAt = DateTime.UtcNow;
            p.QueuedCount += dlg.Copies;
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
