using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DolaCoordinator.Helpers;

/// <summary>
/// Trích các quy tắc liên quan tới prompt / thời lượng / model / ảnh tham chiếu từ README (markdown) của repo:
/// bảng, gạch đầu dòng và đoạn văn có nhắc tới các từ khóa đó, kèm tiêu đề mục gần nhất. Giữ nguyên văn (không dịch).
/// </summary>
public static class RepoRuleParser
{
    private static readonly Regex Keywords = new(
        @"prompt|提示词|duration|时长|reference|参考|seedance|模型|图片|image|\b(?:5|10|15|30)\s?(?:s\b|sec|seconds?|秒|giây)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex EnvRow = new(@"^\s*\|\s*`?DOLA_", RegexOptions.Compiled);
    private static readonly Regex PathCell = new(@"\.(?:py|js|json|md|txt|html|yml|yaml)(?![A-Za-z])|/\s*$|^\s*[\w.-]+/", RegexOptions.Compiled);
    private static readonly Regex ListMarker = new(@"^\s*(?:[-*+]|\d+[.)])\s+", RegexOptions.Compiled);

    public static List<PromptRule> Extract(string markdown, string source)
    {
        var rules = new List<PromptRule>();
        var seen = new HashSet<string>();
        var heading = string.Empty;
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inFence = false;

        void Add(string title, string detail)
        {
            detail = detail.Trim();
            if (detail.Length == 0 || !seen.Add(detail)) return;
            rules.Add(new PromptRule(title, detail, source));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = Heading.Match(line);
            if (h.Success) { heading = Clean(h.Groups[1].Value); continue; }

            if (line.TrimStart().StartsWith('|'))
            {
                var table = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('|')) table.Add(lines[i++]);
                i--;
                var rows = table.Where(r => !TableSeparator.IsMatch(r)).ToList();
                if (rows.Count > 1 && rows.Skip(1).Count(r => PathCell.IsMatch(TableRow(r).Split('|')[0])) * 2 >= rows.Count - 1) continue; // bảng cấu trúc thư mục
                if (rows.Count > 0 && rows.All(r => !EnvRow.IsMatch(r) || rows.IndexOf(r) == 0)
                    && rows.Any(r => !EnvRow.IsMatch(r) && Keywords.IsMatch(r)))
                {
                    var kept = rows.Where((r, idx) => idx == 0 || !EnvRow.IsMatch(r));
                    Add(heading, string.Join("\n", kept.Select(TableRow)));
                }
                continue;
            }

            var text = Clean(ListMarker.Replace(line, string.Empty));
            if (text.Length < 8 || text.StartsWith('>') || text.StartsWith("---", StringComparison.Ordinal)) continue;
            if (!Keywords.IsMatch(text) || text.EndsWith(':')) continue;
            Add(heading, (ListMarker.IsMatch(line) ? "• " : string.Empty) + text);
        }
        return rules.Take(40).ToList();
    }

    private static string TableRow(string row)
    {
        var cells = row.Trim().Trim('|').Split('|').Select(c => Clean(c));
        return string.Join("  |  ", cells);
    }

    private static string Clean(string s)
    {
        s = Link.Replace(s, "$1");
        s = s.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
