using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClosedXML.Excel;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

// ---- Dòng xuất của từng loại (record phẳng để ghi ra Excel) ----

public sealed record AccountExportRow(
    string Name, string Kind, string Email, string Password, string Totp,
    string Cookie, string Notes, bool Use, string After, string ProxyUrl, string DolaSession);

public sealed record ProxyExportRow(
    string Name, string Scheme, string Host, int Port, string Username, string Password, string Country);

/// <summary>Một phần của kịch bản ở dạng phẳng (mỗi phần một dòng); các cột cấp kịch bản lặp lại trên từng phần.</summary>
public sealed record ScriptExportRow(
    string Title, string Model, string Ratio, int Duration, bool UseLastFrame, bool AutoMerge,
    string Characters, string SceneText, string SceneImages, string ContinueHeader, string ContinueFooter,
    string Notes, int PartNumber, string PartText);

/// <summary>
/// Xuất / nhập Excel cho tài khoản, proxy và kịch bản lớn — và gộp tất cả (cùng prompt) vào một workbook
/// nhiều sheet cho nút "Sao lưu toàn bộ" trong Cài đặt. Prompt dùng lại PromptFileParser/ImportTemplates.
/// </summary>
public static class BackupIO
{
    public const string HeaderColor = "#0F766E";

    public const string SheetAccounts = "Tài khoản";
    public const string SheetProxies = "Proxy";
    public const string SheetPrompts = "Prompt";
    public const string SheetScripts = "Kịch bản";

    public static readonly string[] AccountHeaders =
    {
        "Tên tài khoản", "Loại đăng nhập", "Email / SĐT", "Mật khẩu", "Khóa 2FA (TOTP)", "Cookie",
        "Ghi chú", "Dùng để chạy (Có/Không)", "Sau khi đăng nhập (Đóng/Giữ)",
        "Proxy (scheme://user:pass@host:port)", "Phiên Dola (sessionid=...)",
    };

    public static readonly string[] ProxyHeaders =
    {
        "Tên", "Giao thức", "Host", "Cổng", "Tài khoản", "Mật khẩu", "Quốc gia IP (nếu đã kiểm tra)",
    };

    public static readonly string[] ScriptHeaders =
    {
        "Kịch bản", "Model", "Tỷ lệ", "Thời lượng/phần (giây)", "Dùng khung hình cuối (Có/Không)", "Tự ghép (Có/Không)",
        "Nhân vật (mỗi dòng: Tên :: Mô tả :: ảnh1;ảnh2)", "Bối cảnh", "Ảnh bối cảnh (cách nhau ;)",
        "Dặn đầu phần", "Dặn cuối phần", "Ghi chú", "Phần số", "Nội dung phần",
    };

    // ============================================================ GHI

    public static void WriteAccounts(string path, IEnumerable<AccountExportRow> rows)
    {
        using var wb = new XLWorkbook();
        AddAccountsSheet(wb, rows);
        wb.SaveAs(path);
    }

    public static void WriteProxies(string path, IEnumerable<ProxyExportRow> rows)
    {
        using var wb = new XLWorkbook();
        AddProxiesSheet(wb, rows);
        wb.SaveAs(path);
    }

    public static void WriteScripts(string path, IEnumerable<ScriptExportRow> rows)
    {
        using var wb = new XLWorkbook();
        AddScriptsSheet(wb, rows);
        wb.SaveAs(path);
    }

    /// <summary>Workbook nhiều sheet cho "Sao lưu toàn bộ".</summary>
    public static void WriteAll(string path, IEnumerable<AccountExportRow> accounts, IEnumerable<ProxyExportRow> proxies,
                                IEnumerable<PromptItem> prompts, IEnumerable<ScriptExportRow> scripts)
    {
        using var wb = new XLWorkbook();
        AddAccountsSheet(wb, accounts);
        AddProxiesSheet(wb, proxies);
        AddPromptsSheet(wb, prompts);
        AddScriptsSheet(wb, scripts);
        wb.SaveAs(path);
    }

    private static void AddAccountsSheet(XLWorkbook wb, IEnumerable<AccountExportRow> rows)
    {
        var ws = wb.Worksheets.Add(SheetAccounts);
        Header(ws, AccountHeaders);
        for (var c = 1; c <= AccountHeaders.Length; c++) ws.Column(c).Style.NumberFormat.Format = "@";
        var widths = new double[] { 20, 30, 24, 18, 22, 48, 28, 18, 24, 40, 44 };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        var r = 2;
        foreach (var a in rows)
        {
            ws.Cell(r, 1).Value = a.Name;
            ws.Cell(r, 2).Value = a.Kind;
            ws.Cell(r, 3).Value = a.Email;
            ws.Cell(r, 4).Value = a.Password;
            ws.Cell(r, 5).Value = a.Totp;
            ws.Cell(r, 6).Value = a.Cookie;
            ws.Cell(r, 7).Value = a.Notes;
            ws.Cell(r, 8).Value = a.Use ? "Có" : "Không";
            ws.Cell(r, 9).Value = a.After;
            ws.Cell(r, 10).Value = a.ProxyUrl;
            ws.Cell(r, 11).Value = a.DolaSession;
            r++;
        }
        ws.SheetView.FreezeRows(1);
    }

    private static void AddProxiesSheet(XLWorkbook wb, IEnumerable<ProxyExportRow> rows)
    {
        var ws = wb.Worksheets.Add(SheetProxies);
        Header(ws, ProxyHeaders);
        for (var c = 1; c <= ProxyHeaders.Length; c++) ws.Column(c).Style.NumberFormat.Format = "@";
        var widths = new double[] { 24, 12, 26, 10, 20, 22, 20 };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        var r = 2;
        foreach (var p in rows)
        {
            ws.Cell(r, 1).Value = p.Name;
            ws.Cell(r, 2).Value = p.Scheme;
            ws.Cell(r, 3).Value = p.Host;
            ws.Cell(r, 4).Value = p.Port.ToString(CultureInfo.InvariantCulture);
            ws.Cell(r, 5).Value = p.Username;
            ws.Cell(r, 6).Value = p.Password;
            ws.Cell(r, 7).Value = p.Country;
            r++;
        }
        ws.SheetView.FreezeRows(1);
    }

    private static void AddPromptsSheet(XLWorkbook wb, IEnumerable<PromptItem> items)
    {
        var ws = wb.Worksheets.Add(SheetPrompts);
        var headers = new[] { "Tên prompt", "Nội dung (nhiều dòng)", "Tỷ lệ", "Thời lượng (giây)", "Model", "Ghi chú", "Ảnh tham chiếu (cách nhau ;)" };
        Header(ws, headers);
        var widths = new double[] { 30, 80, 10, 14, 16, 30, 46 };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.Column(2).Style.Alignment.WrapText = true;
        var r = 2;
        foreach (var p in items)
        {
            ws.Cell(r, 1).Value = p.Title;
            ws.Cell(r, 2).Value = p.Text.Replace("\r\n", "\n");
            ws.Cell(r, 3).Value = p.Ratio;
            ws.Cell(r, 4).Value = p.Duration;
            ws.Cell(r, 5).Value = p.Model;
            ws.Cell(r, 6).Value = p.Notes ?? string.Empty;
            ws.Cell(r, 7).Value = string.Join(";", p.ReferenceLocalPaths);
            r++;
        }
        ws.SheetView.FreezeRows(1);
    }

    private static void AddScriptsSheet(XLWorkbook wb, IEnumerable<ScriptExportRow> rows)
    {
        var ws = wb.Worksheets.Add(SheetScripts);
        Header(ws, ScriptHeaders);
        var widths = new double[] { 24, 14, 8, 16, 20, 14, 46, 46, 30, 40, 40, 24, 8, 70 };
        for (var i = 0; i < widths.Length; i++) ws.Column(i + 1).Width = widths[i];
        ws.Column(7).Style.Alignment.WrapText = true;
        ws.Column(8).Style.Alignment.WrapText = true;
        ws.Column(14).Style.Alignment.WrapText = true;
        var r = 2;
        foreach (var s in rows)
        {
            ws.Cell(r, 1).Value = s.Title;
            ws.Cell(r, 2).Value = s.Model;
            ws.Cell(r, 3).Value = s.Ratio;
            ws.Cell(r, 4).Value = s.Duration;
            ws.Cell(r, 5).Value = s.UseLastFrame ? "Có" : "Không";
            ws.Cell(r, 6).Value = s.AutoMerge ? "Có" : "Không";
            ws.Cell(r, 7).Value = s.Characters;
            ws.Cell(r, 8).Value = s.SceneText;
            ws.Cell(r, 9).Value = s.SceneImages;
            ws.Cell(r, 10).Value = s.ContinueHeader;
            ws.Cell(r, 11).Value = s.ContinueFooter;
            ws.Cell(r, 12).Value = s.Notes;
            ws.Cell(r, 13).Value = s.PartNumber;
            ws.Cell(r, 14).Value = s.PartText;
            r++;
        }
        ws.SheetView.FreezeRows(1);
    }

    // ============================================================ ĐỌC

    /// <summary>Đọc các dòng (gồm cả tiêu đề) của một sheet theo tên; null nếu không có sheet đó.</summary>
    public static List<string[]>? ReadSheetRows(string path, string sheetName)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
        if (ws == null || ws.LastCellUsed() == null) return null;
        var used = ws.RangeUsed();
        if (used == null) return null;
        var rows = new List<string[]>();
        var lastCol = used.LastColumn().ColumnNumber();
        foreach (var row in used.Rows())
            rows.Add(Enumerable.Range(1, lastCol).Select(c => row.Cell(c).GetString().Trim()).ToArray());
        return rows;
    }

    /// <summary>Đọc các dòng proxy (gồm tiêu đề) thành (ProxyItem, mật khẩu thường). Chấp nhận cả cột 'url' nếu có.</summary>
    public static List<(ProxyItem Item, string Password)> ProxiesFromRows(List<string[]> rows)
    {
        var result = new List<(ProxyItem, string)>();
        if (rows.Count < 2) return result;
        var header = rows[0].Select(Norm).ToList();
        int Col(params string[] aliases) => header.FindIndex(h => aliases.Any(a => h == a || h.StartsWith(a)));
        var iName = Col("ten", "name");
        var iScheme = Col("giaothuc", "scheme", "protocol");
        var iHost = Col("host", "diachi", "ip");
        var iPort = Col("cong", "port");
        var iUser = Col("taikhoan", "user", "username");
        var iPass = Col("matkhau", "pass", "password");
        var iUrl = Col("url", "proxy", "duongdan");

        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Get(int idx) => idx >= 0 && idx < r.Length ? r[idx].Trim() : string.Empty;
            // Ưu tiên cột url nếu có; không thì ghép từ các cột rời.
            ParsedProxy? pp = null;
            var url = Get(iUrl);
            if (url.Length > 0 && ProxyParser.TryParse(url, out var parsed, out _)) pp = parsed;
            if (pp == null)
            {
                var host = Get(iHost);
                if (host.Length == 0 || !int.TryParse(Get(iPort), out var port)) continue;
                var scheme = Get(iScheme) is { Length: > 0 } sc ? sc.ToLowerInvariant() : "http";
                var user = Get(iUser);
                pp = new ParsedProxy(scheme, host, port, user.Length > 0 ? user : null, Get(iPass) is { Length: > 0 } pw ? pw : null);
            }
            var name = Get(iName) is { Length: > 0 } n ? n : $"{pp.Host}:{pp.Port}";
            result.Add((new ProxyItem { Name = name, Scheme = pp.Scheme, Host = pp.Host, Port = pp.Port, Username = pp.Username }, pp.Password ?? string.Empty));
        }
        return result;
    }

    /// <summary>Dựng lại kịch bản từ các dòng phần (nhóm theo tên kịch bản, theo đúng thứ tự xuất hiện).</summary>
    public static List<ScriptProject> ScriptsFromRows(List<string[]> rows)
    {
        var result = new List<ScriptProject>();
        if (rows.Count < 2) return result;

        var header = rows[0].Select(Norm).ToList();
        int Col(params string[] aliases) => header.FindIndex(h => aliases.Any(a => h == a || h.StartsWith(a)));
        var iTitle = Col("kichban", "title", "ten");
        var iModel = Col("model");
        var iRatio = Col("tyle", "ratio");
        var iDur = Col("thoiluong", "duration");
        var iLast = Col("khunghinhcuoi", "lastframe");
        var iMerge = Col("tughep", "merge");
        var iChars = Col("nhanvat", "character");
        var iScene = Col("boicanh", "scene");
        var iSceneImg = Col("anhboicanh", "sceneimage");
        var iHead = Col("danhdauphan", "dandauphan", "header", "danhdau");
        var iFoot = Col("dancuoiphan", "footer");
        var iNotes = Col("ghichu", "note");
        var iPartNo = Col("phanso", "partnumber", "phan");
        var iPartText = Col("noidungphan", "parttext", "noidung");

        var byTitle = new Dictionary<string, ScriptProject>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Get(int idx) => idx >= 0 && idx < r.Length ? r[idx].Trim() : string.Empty;
            var title = Get(iTitle);
            var partText = Get(iPartText);
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(partText)) continue;
            if (string.IsNullOrWhiteSpace(title)) title = "Kịch bản";

            if (!byTitle.TryGetValue(title, out var proj))
            {
                proj = new ScriptProject
                {
                    Title = title,
                    Model = Get(iModel) is { Length: > 0 } m ? PromptFileParser.NormalizeModel(m) : "seedance-2.5",
                    Ratio = Get(iRatio) is { Length: > 0 } ra ? ra : "16:9",
                    Duration = int.TryParse(Get(iDur), out var d) ? d : 10,
                    UseLastFrame = !IsNo(Get(iLast)),
                    AutoMerge = IsYes(Get(iMerge)),
                    SceneText = Get(iScene),
                    SceneImages = SplitList(Get(iSceneImg)),
                    Characters = ParseCharacters(Get(iChars)),
                    Notes = Get(iNotes) is { Length: > 0 } nt ? nt : null,
                };
                if (Get(iHead) is { Length: > 0 } h) proj.ContinueHeader = h;
                if (Get(iFoot) is { Length: > 0 } f) proj.ContinueFooter = f;
                byTitle[title] = proj;
                result.Add(proj);
            }
            if (!string.IsNullOrWhiteSpace(partText))
                proj.Parts.Add(new ScriptPart { Text = partText });
        }
        return result;
    }

    public static List<PromptCharacter> ParseCharacters(string cell)
    {
        var list = new List<PromptCharacter>();
        if (string.IsNullOrWhiteSpace(cell)) return list;
        foreach (var line in cell.Split(new[] { "\r\n", "\n", "||" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { "::" }, StringSplitOptions.None);
            var name = parts.Length > 0 ? parts[0].Trim() : string.Empty;
            if (name.Length == 0) continue;
            list.Add(new PromptCharacter
            {
                Name = name,
                Description = parts.Length > 1 ? parts[1].Trim() : string.Empty,
                Images = parts.Length > 2 ? SplitList(parts[2]) : new List<string>(),
            });
        }
        return list;
    }

    public static string SerializeCharacters(IEnumerable<PromptCharacter> chars) =>
        string.Join("\n", chars.Select(c => $"{c.Name} :: {c.Description} :: {string.Join(";", c.Images)}"));

    public static List<string> SplitList(string s) =>
        s.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private static bool IsYes(string v) => Norm(v) is "co" or "yes" or "true" or "1" or "x";
    private static bool IsNo(string v) => Norm(v) is "khong" or "no" or "false" or "0";

    private static string Norm(string s)
    {
        s = (s ?? string.Empty).Trim().ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(ch))
                sb.Append(ch);
        return sb.ToString().Replace("đ", "d");
    }

    private static void Header(IXLWorksheet ws, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];
        var range = ws.Range(1, 1, 1, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml(HeaderColor);
        range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 26;
    }
}
