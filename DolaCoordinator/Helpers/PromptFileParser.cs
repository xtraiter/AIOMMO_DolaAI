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
    /// <summary>Đúng các lựa chọn trong ô "比率" (tỷ lệ) của Dola.</summary>
    public static readonly string[] Ratios = { "1:1", "3:4", "4:3", "9:16", "16:9", "21:9" };

    /// <summary>Mọi thời lượng có thể có (hợp của hai model). Dùng <see cref="DurationsFor"/> để lấy đúng danh sách theo model.</summary>
    public static readonly int[] Durations = { 5, 10, 15, 30 };

    /// <summary>Thời lượng theo model (README dola-pool): Seedance 2.0 → 5/10/15 giây; Seedance 2.5 → 5/10/30 giây.</summary>
    public static int[] DurationsFor(string? model) => NormalizeModel(model) == "seedance-2.5" ? new[] { 5, 10, 30 } : new[] { 5, 10, 15 };

    /// <summary>Giữ nguyên nếu hợp lệ với model; không thì lấy thời lượng gần nhất mà model đó có.</summary>
    public static int FitDuration(int seconds, string? model)
    {
        var allowed = DurationsFor(model);
        return allowed.Contains(seconds) ? seconds : allowed.OrderBy(d => Math.Abs(d - seconds)).ThenBy(d => d).First();
    }

    /// <summary>Model mà gateway hỗ trợ (giá trị gửi qua API /v1/videos/generations).</summary>
    public static readonly string[] Models = { "seedance-2.0", "seedance-2.5" };

    /// <summary>Tên hiển thị: seedance-2.5 → "Seedance 2.5".</summary>
    public static string ModelLabel(string? model) => "Seedance " + NormalizeModel(model).Replace("seedance-", string.Empty);

    public static readonly string[] ModelLabels = Models.Select(ModelLabel).ToArray();

    /// <summary>Nhận "2.5", "Seedance 2.5", "seedance-2.5", "seedance_v2.5"… → giá trị API; không nhận ra thì seedance-2.0.</summary>
    public static string NormalizeModel(string? value)
    {
        var v = (value ?? string.Empty).ToLowerInvariant();
        return v.Contains("2.5") || v.Contains("25") ? "seedance-2.5" : "seedance-2.0";
    }

    private static readonly Regex BlockSeparator = new(@"^[ \t]*(?:-{3,}|={3,})[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<PromptItem> Parse(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsx" or ".csv" or ".tsv") return FromRows(TableFile.ReadRows(path));

        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        return ParseBlocks(text, Path.GetFileNameWithoutExtension(path));
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

    public static List<PromptItem> ParseCsv(string text) => FromRows(ReadCsv(text));

    /// <summary>Bảng (dòng đầu là tiêu đề) từ Excel hoặc CSV → danh sách prompt. Ô nhiều dòng được giữ nguyên.</summary>
    public static List<PromptItem> FromRows(List<string[]> rows)
    {
        var result = new List<PromptItem>();
        if (rows.Count == 0) return result;

        var header = rows[0].Select(Norm).ToList();
        int Col(params string[] names) => header.FindIndex(h => names.Any(n => h == n || h.StartsWith(n)));
        var iText = Col("prompt", "noidung", "content", "text", "kichban", "script");
        var hasHeader = iText >= 0;

        int iTitle, iRatio, iDuration, iNotes, iModel = -1, iRefs = -1;
        if (hasHeader)
        {
            iTitle = Col("title", "ten", "name", "tenprompt", "tieude");
            iRatio = Col("ratio", "tyle", "tile");
            iDuration = Col("duration", "thoiluong", "giay", "seconds");
            iNotes = Col("notes", "note", "ghichu");
            iModel = Col("model", "mohinh", "seedance");
            iRefs = Col("anhthamchieu", "refimages", "images", "reference");
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
            // Ô model để trống mà thời lượng là 30 giây → chỉ Seedance 2.5 có 30 giây nên chọn 2.5; còn lại mặc định 2.0.
            var modelCell = Get(iModel);
            if (modelCell.Length == 0) modelCell = Get(iDuration).Any(char.IsDigit) && NormalizeDuration(Get(iDuration), "seedance-2.5") == 30 ? "seedance-2.5" : "seedance-2.0";
            result.Add(new PromptItem
            {
                Title = title.Length > 0 ? title : AutoTitle(body),
                Text = body,
                Ratio = NormalizeRatio(Get(iRatio)),
                Duration = NormalizeDuration(Get(iDuration), modelCell),
                Model = NormalizeModel(modelCell),
                ReferenceLocalPaths = Get(iRefs).Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                Notes = Get(iNotes) is { Length: > 0 } n ? n : null,
            });
        }
        return result;
    }

    /// <summary>CSV RFC 4180: ô trong ngoặc kép có thể chứa dấu phân cách, xuống dòng và "" (một dấu ngoặc kép).</summary>
    public static List<string[]> ReadCsv(string text)
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
        sb.Append("Title,Prompt,Ratio,Duration,Model,Notes\r\n");
        foreach (var p in prompts)
            sb.Append(string.Join(",", Q(p.Title), Q(p.Text), Q(p.Ratio), Q(p.Duration.ToString(CultureInfo.InvariantCulture)), Q(p.Model), Q(p.Notes))).Append("\r\n");
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

    public static int NormalizeDuration(string? value, string? model = null)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        var allowed = DurationsFor(model);
        return int.TryParse(digits, out var n) && n > 0 ? FitDuration(n, model) : allowed[^1];
    }

    private static string Norm(string s)
    {
        var d = s.Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }
}
