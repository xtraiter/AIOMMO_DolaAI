using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;

namespace DolaCoordinator.ViewModels;

/// <summary>Một ảnh tham chiếu trong ô nhập (biết danh sách chứa nó để nút "bỏ ảnh" xóa đúng chỗ).</summary>
public sealed class CastImage
{
    public CastImage(string path, IList<CastImage> parent)
    {
        Path = path;
        Parent = parent;
    }

    public string Path { get; }
    public IList<CastImage> Parent { get; }
}

public partial class CharacterEntry : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    public ObservableCollection<CastImage> Images { get; } = new();

    public void AddImage(string path)
    {
        if (!Images.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            Images.Add(new CastImage(path, Images));
    }
}

/// <summary>Dữ liệu của khung "Nhân vật + Bối cảnh" (dùng chung cho cửa sổ sửa prompt và cửa sổ kịch bản lớn).</summary>
public partial class CastModel : ObservableObject
{
    public ObservableCollection<CharacterEntry> Characters { get; } = new();

    [ObservableProperty] private string _sceneText = string.Empty;

    public ObservableCollection<CastImage> SceneImages { get; } = new();

    public void AddSceneImage(string path)
    {
        if (!SceneImages.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)))
            SceneImages.Add(new CastImage(path, SceneImages));
    }

    public static CastModel From(IEnumerable<PromptCharacter>? characters, string? sceneText, IEnumerable<string>? sceneImages)
    {
        var m = new CastModel { SceneText = sceneText ?? string.Empty };
        foreach (var c in characters ?? Enumerable.Empty<PromptCharacter>())
        {
            var e = new CharacterEntry { Name = c.Name, Description = c.Description };
            foreach (var p in c.Images) e.AddImage(p);
            m.Characters.Add(e);
        }
        foreach (var p in sceneImages ?? Enumerable.Empty<string>()) m.AddSceneImage(p);
        return m;
    }

    /// <summary>Nhân vật để lưu: bỏ khung trống (không tên, không mô tả, không ảnh).</summary>
    public List<PromptCharacter> ToCharacters() => Characters
        .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Description) || c.Images.Count > 0)
        .Select(c => new PromptCharacter
        {
            Name = c.Name.Trim(),
            Description = c.Description.Trim(),
            Images = c.Images.Select(i => i.Path).ToList(),
        }).ToList();

    public List<string> ToSceneImages() => SceneImages.Select(i => i.Path).ToList();

    public int ImageCount(IEnumerable<string>? otherImages = null, int leading = 0)
        => PromptComposer.CountImages(otherImages, ToCharacters(), ToSceneImages(), leading);
}
