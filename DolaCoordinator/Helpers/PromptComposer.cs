using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>Prompt cuối cùng gửi cho Dola và danh sách ảnh tham chiếu đi kèm (đúng thứ tự được đánh số trong prompt).</summary>
public sealed record ComposedPrompt(string Text, List<string> Images);

/// <summary>
/// Ghép nội dung prompt + nhân vật + bối cảnh thành prompt gửi đi. Mỗi nhân vật / bối cảnh có ảnh riêng; ảnh được đánh số
/// theo thứ tự gửi ("ảnh tham chiếu 2, 3") để Dola biết ảnh nào là ai.
/// Thứ tự ảnh: ảnh dẫn đầu (khung hình cuối của phần trước) → ảnh tham chiếu mặc định → ảnh từng nhân vật → ảnh bối cảnh.
/// </summary>
public static class PromptComposer
{
    /// <summary>Dola chỉ nhận tối đa 10 ảnh tham chiếu mỗi video (theo README dola-pool).</summary>
    public const int MaxReferenceImages = 10;

    public static ComposedPrompt Compose(
        string text,
        IEnumerable<string>? defaultImages = null,
        IReadOnlyList<PromptCharacter>? characters = null,
        string? sceneText = null,
        IEnumerable<string>? sceneImages = null,
        IEnumerable<string>? leadingImages = null,
        string? header = null,
        string? footer = null)
    {
        var images = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return 0;
            if (!seen.Add(path)) return images.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) + 1;
            images.Add(path);
            return images.Count;
        }

        foreach (var p in leadingImages ?? Enumerable.Empty<string>()) Add(p);
        foreach (var p in defaultImages ?? Enumerable.Empty<string>()) Add(p);

        var cast = new StringBuilder();
        var named = (characters ?? Array.Empty<PromptCharacter>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Description) || c.Images.Count > 0)
            .ToList();
        if (named.Count > 0)
        {
            cast.AppendLine(named.Count == 1 ? "Nhân vật:" : "Các nhân vật:");
            for (var i = 0; i < named.Count; i++)
            {
                var c = named[i];
                var name = string.IsNullOrWhiteSpace(c.Name) ? $"Nhân vật {i + 1}" : c.Name.Trim();
                cast.Append("- ").Append(name).Append(RefLabel(c.Images.Select(Add).Where(n => n > 0).Distinct().OrderBy(n => n).ToList()));
                if (!string.IsNullOrWhiteSpace(c.Description)) cast.Append(": ").Append(OneLine(c.Description));
                cast.AppendLine();
            }
        }

        var sceneNumbers = (sceneImages ?? Enumerable.Empty<string>()).Select(Add).Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
        if (!string.IsNullOrWhiteSpace(sceneText) || sceneNumbers.Count > 0)
        {
            cast.Append("Bối cảnh").Append(RefLabel(sceneNumbers));
            if (!string.IsNullOrWhiteSpace(sceneText)) cast.Append(": ").Append(OneLine(sceneText));
            cast.AppendLine();
        }

        var blocks = new List<string>();
        if (!string.IsNullOrWhiteSpace(header)) blocks.Add(header.Trim());
        if (cast.Length > 0) blocks.Add(cast.ToString().TrimEnd());
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(text.Trim('\r', '\n'));
        if (!string.IsNullOrWhiteSpace(footer)) blocks.Add(footer.Trim());
        return new ComposedPrompt(string.Join("\n\n", blocks), images);
    }

    /// <summary>" (ảnh tham chiếu 2, 3)" hoặc " (không có ảnh)" khi nhân vật chưa có ảnh; rỗng với bối cảnh không ảnh.</summary>
    private static string RefLabel(List<int> numbers)
        => numbers.Count == 0 ? string.Empty : $" (ảnh tham chiếu {string.Join(", ", numbers)})";

    private static string OneLine(string s)
        => string.Join(" ", s.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Số ảnh khác nhau sẽ được gửi (để báo vượt giới hạn trước khi lưu/chạy).</summary>
    public static int CountImages(
        IEnumerable<string>? defaultImages,
        IReadOnlyList<PromptCharacter>? characters,
        IEnumerable<string>? sceneImages,
        int leading = 0)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in defaultImages ?? Enumerable.Empty<string>()) set.Add(p);
        foreach (var c in characters ?? Array.Empty<PromptCharacter>()) foreach (var p in c.Images) set.Add(p);
        foreach (var p in sceneImages ?? Enumerable.Empty<string>()) set.Add(p);
        return set.Count + leading;
    }
}
