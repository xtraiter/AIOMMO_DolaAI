using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>
/// Đọc / ghi prompt từ file mà KHÔNG cắt theo từng dòng (một prompt có thể nhiều dòng, dạng bảng...).
/// • .csv / .tsv: mỗi bản ghi là một prompt; ô có thể chứa nhiều dòng nếu nằm trong dấu ngoặc kép. Cột nhận diện theo tiêu đề
///   (Tên/Title, Prompt/Nội dung, Tỷ lệ/Ratio, Thời lượng/Duration, Ghi chú/Notes).
/// • .txt / .md: các prompt cách nhau bằng một dòng chỉ có --- (hoặc ===); không có dòng phân cách thì cả file là MỘT prompt.
/// </summary>
public static class PromptFileParser
{
    public static readonly string[] Ratios = { "9:16", "16:9", "1:1" };
    public static readonly int[] Durations = { 10, 15, 30 };

    private static readonly Regex BlockSeparator = new(@"^[ \t]*(?:-{3,}|={3,})[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<PromptItem> Parse(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".csv" or ".tsv"
            ? ParseCsv(text)
            : ParseBlocks(text, Path.GetFileNameWithoutExtension(path));
    }

    // ------------------------------------------------------------------ txt / md

    public static List<PromptItem> ParseBlocks(string text, string fallbackTitle)
    {
        var result = new List<PromptItem>();
        var blocks = BlockSeparator.Split(text).Select(b => b.Trim('\r', '\n', ' ', '\t')).Where(b => b.Length > 0).ToList();
        foreach (var block in blocks)
        {
            var lines = block.Replace("\r\n", "\n").Split('\n').ToList();
            string? title = null;
            if (lines[0].TrimStart().StartsWith('#'))
            {
                title = lines[0].TrimStart('#', ' ', '\t').Trim();
                lines.RemoveAt(0);
            }
            var body = string.Join(Environment.NewLine, lines).Trim();
            if (body.Length == 0) body = block; // chỉ có một dòng tiêu đề: dùng luôn làm nội dung

            result.Add(new PromptItem
            {
                Title = string.IsNullOrWhiteSpace(title) ? (blocks.Count == 1 ? fallbackTitle : AutoTitle(body)) : title!,
                Text = body,
            });
        }
        return result;
    }

    // ------------------------------------------------------------------ csv

    public static List<PromptItem> ParseCsv(string text)
    {
        var rows = ReadCsv(text);
        var result = new List<PromptItem>();
        if (rows.Count == 0) return result;

        var header = rows[0].Select(Norm).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Contains(h));
        var iText = Col("prompt", "noidung", "content", "text", "kichban", "script");
        var hasHeader = iText >= 0;

        int iTitle, iRatio, iDuration, iNotes;
        if (hasHeader)
        {
            iTitle = Col("title", "ten", "name", "tenprompt", "tieude");
            iRatio = Col("ratio", "tyle", "tile");
            iDuration = Col("duration", "thoiluong", "giay", "seconds");
            iNotes = Col("notes", "note", "ghichu");
        }
        else
        {
            // Không có tiêu đề: 1 cột = prompt; từ 2 cột = (tên, prompt)
            iTitle = rows[0].Length >= 2 ? 0 : -1;
            iText = rows[0].Length >= 2 ? 1 : 0;
            iRatio = iDuration = iNotes = -1;
        }

        foreach (var row in rows.Skip(hasHeader ? 1 : 0))
        {
            string Get(int i) => i >= 0 && i < row.Length ? row[i].Trim() : string.Empty;
            var body = Get(iText);
            if (body.Length == 0) continue;
            var title = Get(iTitle);
            result.Add(new PromptItem
            {
                Title = title.Length > 0 ? title : AutoTitle(body),
                Text = body,
                Ratio = NormalizeRatio(Get(iRatio)),
                Duration = NormalizeDuration(Get(iDuration)),
                Notes = Get(iNotes) is { Length: > 0 } n ? n : null,
            });
        }
        return result;
    }

    /// <summary>CSV RFC 4180: ô trong ngoặc kép có thể chứa dấu phân cách, xuống dòng và "" (một dấu ngoặc kép).</summary>
    private static List<string[]> ReadCsv(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.MaxBy(d => firstLine.Count(c => c == d));
        if (firstLine.All(c => c != delimiter)) delimiter = ',';

        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        void EndCell() { row.Add(cell.ToString()); cell.Clear(); }
        void EndRow()
        {
            EndCell();
            if (row.Any(c => c.Length > 0)) rows.Add(row.ToArray());
            row = new List<string>();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) inQuotes = true;
            else if (c == delimiter) EndCell();
            else if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else if (c == '\n') EndRow();
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    public static string ToCsv(IEnumerable<PromptItem> prompts)
    {
        static string Q(string? s) => "\"" + (s ?? string.Empty).Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.Append("Title,Prompt,Ratio,Duration,Notes\r\n");
        foreach (var p in prompts)
            sb.Append(string.Join(",", Q(p.Title), Q(p.Text), Q(p.Ratio), Q(p.Duration.ToString(CultureInfo.InvariantCulture)), Q(p.Notes))).Append("\r\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ helpers

    public static string AutoTitle(string body)
    {
        var first = body.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "Prompt";
        return first.Length > 50 ? first[..47] + "…" : first;
    }

    public static string NormalizeRatio(string? value)
    {
        var v = (value ?? string.Empty).Trim().Replace('/', ':').Replace('x', ':').Replace(" ", string.Empty);
        return Ratios.Contains(v) ? v : "9:16";
    }

    public static int NormalizeDuration(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) && n > 0 ? Durations.OrderBy(d => Math.Abs(d - n)).First() : 30;
    }

    private static string Norm(string s)
    {
        var d = s.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }
}
