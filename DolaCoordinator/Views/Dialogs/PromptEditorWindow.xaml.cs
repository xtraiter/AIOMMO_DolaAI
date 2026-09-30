using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using Microsoft.Win32;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Thêm / sửa một prompt: tên, nội dung nhiều dòng, tỷ lệ, thời lượng, ảnh tham chiếu mặc định, ghi chú.</summary>
public partial class PromptEditorWindow : Window
{
    private const int MaxReferenceImages = 30; // giới hạn của gateway

    public string PromptTitle { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string Ratio { get; set; } = "9:16";
    public int Duration { get; set; } = 30;
    public string Notes { get; set; } = string.Empty;
    public ObservableCollection<string> RefImages { get; } = new();

    public string[] RatioOptions => PromptFileParser.Ratios;
    public int[] DurationOptions => PromptFileParser.Durations;

    public PromptEditorWindow(PromptItem? editing = null)
    {
        if (editing != null)
        {
            PromptTitle = editing.Title;
            PromptText = editing.Text;
            Ratio = editing.Ratio;
            Duration = editing.Duration;
            Notes = editing.Notes ?? string.Empty;
            foreach (var p in editing.ReferenceLocalPaths) RefImages.Add(p);
        }

        InitializeComponent();
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
        if (string.IsNullOrWhiteSpace(PromptTitle)) PromptTitle = PromptFileParser.AutoTitle(PromptText);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
