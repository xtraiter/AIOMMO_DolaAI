using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DolaCoordinator.Helpers;
using DolaCoordinator.ViewModels;
using Microsoft.Win32;

namespace DolaCoordinator.Views.Dialogs;

/// <summary>Khung nhập nhân vật (nhiều nhân vật, mỗi người có ảnh riêng) và bối cảnh (kèm ảnh).</summary>
public partial class CastEditor : UserControl
{
    /// <summary>Ảnh dùng ngoài khung này (ảnh tham chiếu mặc định, khung hình cuối phần trước...) để đếm đúng giới hạn 10 ảnh.</summary>
    public Func<int>? OtherImageCount { get; set; }

    public CastEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Wire();
        Loaded += (_, _) => Wire();
    }

    private CastModel? _wired;

    private void Wire()
    {
        if (_wired == DataContext) { UpdateCount(); return; }
        _wired = DataContext as CastModel;
        if (_wired == null) return;
        _wired.Characters.CollectionChanged += (_, _) => UpdateCount();
        _wired.SceneImages.CollectionChanged += (_, _) => UpdateCount();
        foreach (var c in _wired.Characters) c.Images.CollectionChanged += (_, _) => UpdateCount();
        UpdateCount();
    }

    /// <summary>Cập nhật bộ đếm ảnh (gọi khi ảnh ở ngoài khung này thay đổi).</summary>
    public void RefreshCount() => UpdateCount();

    private void UpdateCount()
    {
        if (DataContext is not CastModel m) return;
        var total = m.ImageCount() + (OtherImageCount?.Invoke() ?? 0);
        ImageCountText.Text = $"Ảnh tham chiếu của khung này: {m.ImageCount()}" +
                              (OtherImageCount != null ? $" · tổng gửi đi: {total}/{PromptComposer.MaxReferenceImages}" : $" (tối đa {PromptComposer.MaxReferenceImages} ảnh mỗi video)");
    }

    private static string[]? PickImages()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Ảnh (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",
            Title = "Chọn ảnh tham chiếu",
            Multiselect = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileNames : null;
    }

    private void AddCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CastModel m) return;
        var entry = new CharacterEntry();
        entry.Images.CollectionChanged += (_, _) => UpdateCount();
        m.Characters.Add(entry);
    }

    private void RemoveCharacter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CastModel m && sender is Button { Tag: CharacterEntry c }) m.Characters.Remove(c);
    }

    private void AddCharacterImages_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CharacterEntry c }) return;
        if (PickImages() is { } files) foreach (var f in files) c.AddImage(f);
    }

    private void AddSceneImages_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CastModel m) return;
        if (PickImages() is { } files) foreach (var f in files) m.AddSceneImage(f);
    }

    private void RemoveImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CastImage img }) img.Parent.Remove(img);
    }
}
