using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DolaCoordinator.Helpers;

public enum SplitMode
{
    /// <summary>Theo dòng "---" (nếu có) hoặc theo các đoạn cách nhau bằng dòng trống.</summary>
    Paragraph = 0,

    /// <summary>Mỗi dòng bắt đầu bằng "Cảnh N", "Scene N", "Phần N"... mở một khối mới.</summary>
    Scene = 1,
}

/// <summary>Tách một kịch bản dài thành nhiều phần (mỗi phần một video).</summary>
public static class ScriptSplitter
{
    private static readonly Regex SceneHeading = new(
        @"^\s*(?:[#*•\-]+\s*)?(?:cảnh|canh|scene|phần|phan|part|shot|đoạn|doan)\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Dashes = new(@"^\s*-{3,}\s*$", RegexOptions.Compiled);

    /// <param name="blocksPerPart">Gom bao nhiêu khối (đoạn / cảnh) vào một phần (tối thiểu 1).</param>
    public static List<string> Split(string text, SplitMode mode, int blocksPerPart)
    {
        blocksPerPart = Math.Max(1, blocksPerPart);
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = mode == SplitMode.Scene ? BySceneHeading(lines) : ByParagraph(lines);
        blocks = blocks.Select(b => b.Trim('\n', ' ', '\t')).Where(b => b.Length > 0).ToList();

        var parts = new List<string>();
        for (var i = 0; i < blocks.Count; i += blocksPerPart)
            parts.Add(string.Join("\n", blocks.Skip(i).Take(blocksPerPart)));
        return parts;
    }

    private static List<string> ByParagraph(string[] lines)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        var hasDashes = lines.Any(l => Dashes.IsMatch(l));

        void Flush()
        {
            if (current.Count > 0) blocks.Add(string.Join("\n", current));
            current = new List<string>();
        }

        foreach (var line in lines)
        {
            var boundary = hasDashes ? Dashes.IsMatch(line) : string.IsNullOrWhiteSpace(line);
            if (boundary) Flush();
            else current.Add(line);
        }
        Flush();
        return blocks;
    }

    private static List<string> BySceneHeading(string[] lines)
    {
        var blocks = new List<string>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (SceneHeading.IsMatch(line) && current.Any(l => !string.IsNullOrWhiteSpace(l)))
            {
                blocks.Add(string.Join("\n", current));
                current = new List<string>();
            }
            current.Add(line);
        }
        if (current.Count > 0) blocks.Add(string.Join("\n", current));

        // lời dẫn đứng trước cảnh đầu tiên thì dính vào cảnh đó, không tạo thành một phần riêng
        if (blocks.Count >= 2 && !SceneHeading.IsMatch(blocks[0]))
        {
            blocks[1] = blocks[0] + "\n" + blocks[1];
            blocks.RemoveAt(0);
        }
        return blocks;
    }
}
