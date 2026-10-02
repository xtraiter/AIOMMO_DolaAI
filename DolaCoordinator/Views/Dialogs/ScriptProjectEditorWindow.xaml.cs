using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.ViewModels;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Một khung "phần" trong cửa sổ sửa kịch bản (giữ liên kết tới phần cũ để không mất video đã tạo khi chỉ sửa chữ).</summary>
public partial class PartEntry : ObservableObject
{
    public PartEntry(ScriptPart? source, string text)
    {
        Source = source;
        _text = text;
    }

    public ScriptPart? Source { get; }

    [ObservableProperty] private string _text;
    [ObservableProperty] private int _number;
}

/// <summary>Tạo / sửa một kịch bản lớn: thông số, nhân vật + bối cảnh dùng chung, tách kịch bản thành các phần.</summary>
public partial class ScriptProjectEditorWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    private readonly ScriptProject? _editing;

    public string ProjectTitle { get; set; } = string.Empty;
    public string Ratio { get; set; } = "16:9";
    private string _modelLabel = PromptFileParser.ModelLabel("seedance-2.5");
    private int _duration = 10;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Đổi model thì danh sách thời lượng đổi theo (2.0: 5/10/15, 2.5: 5/10/30) và thời lượng không hợp lệ được chuyển về giá trị gần nhất.</summary>
    public string ModelLabel
    {
        get => _modelLabel;
        set
        {
            if (_modelLabel == value) return;
            _modelLabel = value;
            Raise(nameof(ModelLabel));
            Raise(nameof(DurationOptions));
            Duration = PromptFileParser.FitDuration(_duration, PromptFileParser.NormalizeModel(value));
        }
    }

    public int Duration
    {
        get => _duration;
        set
        {
            if (_duration == value) return;
            _duration = value;
            Raise(nameof(Duration));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    public bool UseLastFrame { get; set; } = true;
    public bool AutoMerge { get; set; }
    public string FullScript { get; set; } = string.Empty;
    public int SplitModeIndex { get; set; }
    public string BlocksPerPartText { get; set; } = "1";
    public string ContinueHeader { get; set; } = ScriptProject.DefaultContinueHeader;
    public string ContinueFooter { get; set; } = ScriptProject.DefaultContinueFooter;

    public CastModel Cast { get; }
    public ObservableCollection<PartEntry> Entries { get; } = new();

    public string[] RatioOptions => PromptFileParser.Ratios;
    public int[] DurationOptions => PromptFileParser.DurationsFor(PromptFileParser.NormalizeModel(ModelLabel));
    public string[] ModelOptions => PromptFileParser.ModelLabels;

    /// <summary>Kịch bản sau khi bấm Lưu (khi sửa: chính đối tượng được truyền vào, đã cập nhật).</summary>
    public ScriptProject Result { get; private set; } = new();

    public ScriptProjectEditorWindow(ScriptProject? editing = null)
    {
        _editing = editing;
        Cast = CastModel.From(editing?.Characters, editing?.SceneText, editing?.SceneImages);
        if (editing != null)
        {
            ProjectTitle = editing.Title;
            ModelLabel = PromptFileParser.ModelLabel(editing.Model);
            Ratio = editing.Ratio;
            Duration = PromptFileParser.FitDuration(editing.Duration, editing.Model);
            UseLastFrame = editing.UseLastFrame;
            AutoMerge = editing.AutoMerge;
            ContinueHeader = editing.ContinueHeader;
            ContinueFooter = editing.ContinueFooter;
            foreach (var p in editing.Parts) Entries.Add(new PartEntry(p, p.Text));
            Renumber();
        }

        InitializeComponent();
        MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        CastBox.OtherImageCount = () => UseLastFrame ? 1 : 0;
        DarkTitleBar.Attach(this);
        var title = editing == null ? "Thêm kịch bản lớn" : "Sửa kịch bản lớn";
        Title = title;
        TitleText.Text = title;
        OkButton.Content = editing == null ? "Lưu kịch bản" : "Lưu thay đổi";
        DataContext = this;
    }

    private void Renumber()
    {
        for (var i = 0; i < Entries.Count; i++) Entries[i].Number = i + 1;
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FullScript))
        {
            ShowError("Dán kịch bản đầy đủ vào ô 'Kịch bản đầy đủ' rồi mới tách.");
            return;
        }
        if (!int.TryParse(BlocksPerPartText.Trim(), out var n) || n < 1) n = 1;
        var parts = ScriptSplitter.Split(FullScript, SplitModeIndex == 0 ? SplitMode.Scene : SplitMode.Paragraph, n);
        if (parts.Count == 0)
        {
            ShowError("Không tách được phần nào. Thử đổi cách tách.");
            return;
        }
        if (Entries.Any(x => x.Source != null && x.Source.Status != ScriptPartStatus.NotStarted) &&
            MessageBox.Show("Tách lại sẽ thay toàn bộ các phần hiện có (mất liên kết với các video đã tạo). Tiếp tục?", "Tách kịch bản",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        Entries.Clear();
        foreach (var t in parts) Entries.Add(new PartEntry(null, t));
        Renumber();
        HideError();
    }

    private void AddPart_Click(object sender, RoutedEventArgs e)
    {
        Entries.Add(new PartEntry(null, string.Empty));
        Renumber();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(sender, -1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(sender, +1);

    private void Move(object sender, int delta)
    {
        if (sender is not Button { Tag: PartEntry entry }) return;
        var i = Entries.IndexOf(entry);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= Entries.Count) return;
        Entries.Move(i, j);
        Renumber();
    }

    private void RemovePart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PartEntry entry }) Entries.Remove(entry);
        Renumber();
    }

    private void ResetHints_Click(object sender, RoutedEventArgs e)
    {
        ContinueHeader = ScriptProject.DefaultContinueHeader;
        ContinueFooter = ScriptProject.DefaultContinueFooter;
        DataContext = null;
        DataContext = this;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Entries.Count == 0)
        {
            ShowError("Kịch bản chưa có phần nào: dán kịch bản rồi bấm 'Tách thành các phần', hoặc 'Thêm phần'.");
            return;
        }
        var empty = Entries.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.Text));
        if (empty != null)
        {
            ShowError($"Phần {empty.Number} đang trống. Điền nội dung hoặc xóa phần đó.");
            return;
        }
        var characters = Cast.ToCharacters();
        var total = PromptComposer.CountImages(null, characters, Cast.ToSceneImages(), UseLastFrame ? 1 : 0);
        if (total > PromptComposer.MaxReferenceImages)
        {
            ShowError($"Ảnh tham chiếu mỗi video là {total} (nhân vật + bối cảnh" + (UseLastFrame ? " + khung hình cuối" : "") +
                      $"), Dola chỉ nhận tối đa {PromptComposer.MaxReferenceImages}. Hãy bớt ảnh.");
            return;
        }

        var project = _editing ?? new ScriptProject();
        project.Title = string.IsNullOrWhiteSpace(ProjectTitle)
            ? PromptFileParser.AutoTitle(Entries[0].Text)
            : ProjectTitle.Trim();
        project.Model = PromptFileParser.NormalizeModel(ModelLabel);
        project.Ratio = Ratio;
        project.Duration = Duration;
        project.UseLastFrame = UseLastFrame;
        project.AutoMerge = AutoMerge;
        project.ContinueHeader = string.IsNullOrWhiteSpace(ContinueHeader) ? ScriptProject.DefaultContinueHeader : ContinueHeader.Trim();
        project.ContinueFooter = string.IsNullOrWhiteSpace(ContinueFooter) ? ScriptProject.DefaultContinueFooter : ContinueFooter.Trim();
        project.Characters = characters;
        project.SceneText = Cast.SceneText.Trim();
        project.SceneImages = Cast.ToSceneImages();
        project.Parts = Entries.Select(entry =>
        {
            var part = entry.Source ?? new ScriptPart();
            part.Text = entry.Text.Trim('\r', '\n');
            return part;
        }).ToList();
        project.UpdatedAt = DateTime.UtcNow;
        Result = project;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}
