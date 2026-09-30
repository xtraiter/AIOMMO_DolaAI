using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.ViewModels;
using Microsoft.Win32;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Thêm / sửa một prompt: tên, nội dung nhiều dòng, tỷ lệ, thời lượng, ảnh tham chiếu mặc định, ghi chú.</summary>
public partial class PromptEditorWindow : Window
{
    private const int MaxReferenceImages = PromptComposer.MaxReferenceImages; // Dola chỉ nhận tối đa 10 ảnh tham chiếu

    public string PromptTitle { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string Ratio { get; set; } = "9:16";
    public int Duration { get; set; } = 30;
    public string ModelLabel { get; set; } = PromptFileParser.ModelLabel("seedance-2.0");

    /// <summary>Giá trị API của model đã chọn (seedance-2.0 / seedance-2.5).</summary>
    public string Model => PromptFileParser.NormalizeModel(ModelLabel);
    public string Notes { get; set; } = string.Empty;
    public ObservableCollection<string> RefImages { get; } = new();

    /// <summary>Nhân vật (nhiều nhân vật, mỗi người có ảnh riêng) và bối cảnh của prompt.</summary>
    public CastModel Cast { get; }

    public System.Collections.Generic.List<PromptCharacter> Characters => Cast.ToCharacters();
    public string SceneText => Cast.SceneText.Trim();
    public System.Collections.Generic.List<string> SceneImages => Cast.ToSceneImages();

    public string[] RatioOptions => PromptFileParser.Ratios;
    public int[] DurationOptions => PromptFileParser.Durations;
    public string[] ModelOptions => PromptFileParser.ModelLabels;

    public PromptEditorWindow(PromptItem? editing = null)
    {
        Cast = CastModel.From(editing?.Characters, editing?.SceneText, editing?.SceneImages);
        if (editing != null)
        {
            PromptTitle = editing.Title;
            PromptText = editing.Text;
            Ratio = editing.Ratio;
            Duration = editing.Duration;
            ModelLabel = PromptFileParser.ModelLabel(editing.Model);
            Notes = editing.Notes ?? string.Empty;
            foreach (var p in editing.ReferenceLocalPaths) RefImages.Add(p);
        }

        InitializeComponent();
        MaxHeight = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        CastBox.OtherImageCount = () => RefImages.Count;
        RefImages.CollectionChanged += (_, _) => CastBox.RefreshCount();
        DarkTitleBar.Attach(this);
        var title = editing == null ? "Thêm prompt" : "Sửa prompt";
        Title = title;
        TitleText.Text = title;
        OkButton.Content = editing == null ? "Lưu prompt" : "Lưu thay đổi";
        DataContext = this;
        Loaded += (_, _) => { TitleBox.Focus(); UpdateCount(); };
    }

    private void UpdateCount()
    {
        var text = PromptText ?? string.Empty;
        var lines = string.IsNullOrEmpty(text) ? 0 : text.Split('\n').Length;
        CountText.Text = $"{text.Length:N0} ký tự · {lines} dòng";
    }

    private void TextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateCount();

    private void AddImages_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",
            Title = "Chọn ảnh tham chiếu",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames)
        {
            if (RefImages.Count >= MaxReferenceImages) break;
            if (!RefImages.Contains(file, StringComparer.OrdinalIgnoreCase)) RefImages.Add(file);
        }
    }

    private void Rules_Click(object sender, RoutedEventArgs e) => new PromptRulesWindow { Owner = this }.ShowDialog();

    private void ClearImages_Click(object sender, RoutedEventArgs e) => RefImages.Clear();

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) RefImages.Remove(path);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PromptText))
        {
            ErrorText.Text = "Nội dung prompt đang trống.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var total = PromptComposer.CountImages(RefImages, Characters, SceneImages);
        if (total > MaxReferenceImages)
        {
            ErrorText.Text = $"Tổng ảnh tham chiếu (mặc định + nhân vật + bối cảnh) là {total}, Dola chỉ nhận tối đa {MaxReferenceImages} ảnh mỗi video. Hãy bớt ảnh.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (string.IsNullOrWhiteSpace(PromptTitle)) PromptTitle = PromptFileParser.AutoTitle(PromptText);
        DialogResult = true;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var composed = PromptComposer.Compose(PromptText, RefImages, Characters, SceneText, SceneImages);
        var images = composed.Images.Count == 0
            ? "(không có ảnh tham chiếu)"
            : string.Join(Environment.NewLine, composed.Images.Select((p, i) => $"Ảnh {i + 1}: {System.IO.Path.GetFileName(p)}"));
        new TextPreviewWindow("Prompt sẽ gửi cho Dola",
            composed.Text + Environment.NewLine + Environment.NewLine + "—— Ảnh tham chiếu (theo thứ tự gửi) ——" + Environment.NewLine + images)
        { Owner = this }.ShowDialog();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
