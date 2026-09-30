using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>Các kiểu tài khoản có thể nhập hàng loạt.</summary>
public enum AccountKind
{
    /// <summary>Không có thông tin: tạo tài khoản trống, tự đăng nhập tay sau.</summary>
    Manual,

    /// <summary>Gmail / tài khoản Google: email + mật khẩu (+ khóa 2FA nếu có).</summary>
    Google,

    /// <summary>Facebook thường: email hoặc số điện thoại + mật khẩu (+ khóa 2FA nếu có).</summary>
    Facebook,

    /// <summary>Cookie Facebook (có c_user và/hoặc xs): app đăng nhập Facebook bằng cookie rồi vào Dola bằng Facebook.</summary>
    FacebookCookie,

    /// <summary>Cookie Dola (có sessionid=...): dùng thẳng, không cần đăng nhập.</summary>
    DolaCookie,
}

/// <summary>Một dòng tài khoản đọc từ file, đã kiểm tra hợp lệ.</summary>
public sealed class AccountRow
{
    public int Line { get; init; }
    public string Name { get; set; } = string.Empty;
    public AccountKind Kind { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Totp { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool Use { get; set; } = true;
    public AfterLogin After { get; set; } = AfterLogin.Close;

    /// <summary>Lỗi (tiếng Việt) làm dòng này bị bỏ qua; null nếu hợp lệ.</summary>
    public string? Error { get; set; }

    public LoginMethod LoginMethod => Kind switch
    {
        AccountKind.Google => LoginMethod.Google,
        AccountKind.Facebook => LoginMethod.Facebook,
        AccountKind.FacebookCookie => LoginMethod.FacebookCookie,
        _ => LoginMethod.Manual,
    };

    public bool IsAutomatic => Kind is AccountKind.Google or AccountKind.Facebook or AccountKind.FacebookCookie;

    public string KindLabel => Kind switch
    {
        AccountKind.Google => "Google (Gmail)",
        AccountKind.Facebook => "Facebook (email/SĐT + mật khẩu)",
        AccountKind.FacebookCookie => "Facebook cookie",
        AccountKind.DolaCookie => "Cookie Dola",
        _ => "Thủ công",
    };
}

/// <summary>
/// Đọc danh sách tài khoản từ Excel (.xlsx), CSV/TSV hoặc TXT (kiểu cũ "Tên|Cookie").
/// Cột nhận theo tiêu đề (không phân biệt hoa thường, dấu): Tên, Loại đăng nhập, Email/SĐT, Mật khẩu, Khóa 2FA, Cookie,
/// Ghi chú, Dùng để chạy, Sau khi đăng nhập. Cột "Loại" bỏ trống thì tự nhận từ nội dung.
/// </summary>
public static class AccountFileParser
{
    public static List<AccountRow> Parse(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var rows = ext == ".txt" ? LegacyTextRows(File.ReadAllText(path, Encoding.UTF8)) : TableFile.ReadRows(path);
        return FromRows(rows);
    }

    /// <summary>Dòng "Tên|Cookie" (hoặc chỉ cookie); dòng bắt đầu bằng # là chú thích.</summary>
    private static List<string[]> LegacyTextRows(string text)
    {
        var rows = new List<string[]> { new[] { "name", "cookie" } };
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var bar = line.IndexOf('|');
            rows.Add(bar >= 0 ? new[] { line[..bar].Trim(), line[(bar + 1)..].Trim() } : new[] { string.Empty, line });
        }
        return rows;
    }

    public static List<AccountRow> FromRows(List<string[]> rows)
    {
        var result = new List<AccountRow>();
        if (rows.Count == 0) return result;

        var header = rows[0].Select(Norm).ToList();
        int Col(params string[] aliases) => header.FindIndex(h => aliases.Any(a => h == a || h.StartsWith(a)));

        var iName = Col("tentaikhoan", "tenprofile", "ten", "name", "profile");
        var iKind = Col("loaidangnhap", "loai", "cachdangnhap", "kieu", "method", "type");
        var iEmail = Col("emailsdt", "email", "mail", "gmail", "username", "user", "sdt", "sodienthoai", "taikhoandangnhap", "login");
        var iPass = Col("matkhau", "password", "pass", "mk", "pw");
        var iTotp = Col("khoa2fa", "2fa", "totp", "secret", "key2fa");
        var iCookie = Col("cookie");
        var iNotes = Col("ghichu", "notes", "note");
        var iUse = Col("dungdechay", "dungvanhanh", "chayvanhanh", "use", "enabled", "active");
        var iAfter = Col("saukhidangnhap", "after", "sauklogin");

        if (new[] { iName, iKind, iEmail, iPass, iCookie }.All(i => i < 0))
            throw new InvalidDataException("Không nhận ra tiêu đề cột. Cần ít nhất một trong: Tên tài khoản, Loại đăng nhập, Email/SĐT, Mật khẩu, Cookie. Hãy tải file mẫu.");

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string Get(int idx) => idx >= 0 && idx < r.Length ? r[idx].Trim() : string.Empty;

            var row = new AccountRow
            {
                Line = i + 1, // số dòng như thấy trong Excel (dòng 1 là tiêu đề)
                Name = Get(iName),
                Email = Get(iEmail),
                Password = Get(iPass),
                Totp = Get(iTotp).Replace(" ", string.Empty),
                Cookie = Get(iCookie),
                Notes = Get(iNotes) is { Length: > 0 } n ? n : null,
            };

            if (new[] { row.Name, row.Email, row.Password, row.Cookie, Get(iKind) }.All(string.IsNullOrEmpty)) continue; // dòng trống

            // Dòng ví dụ trong file mẫu ("(mẫu) ...") không bao giờ được nhập
            if (row.Name.StartsWith("(mẫu)", StringComparison.OrdinalIgnoreCase) || row.Name.StartsWith("(mau)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var usage = Get(iUse);
            row.Use = usage.Length == 0 || !IsNo(usage);
            row.After = Norm(Get(iAfter)) is { } a && (a.Contains("giu") || a.Contains("keep") || a.Contains("mo")) ? AfterLogin.Keep : AfterLogin.Close;

            row.Kind = ParseKind(Get(iKind), row, out var kindError);
            if (kindError != null) { row.Error = kindError; result.Add(row); continue; }

            // Tên: bỏ trống thì lấy từ email, cuối cùng là Account_n; luôn chuẩn hóa cho hợp lệ với gateway
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                var seed = row.Email.Contains('@') ? row.Email[..row.Email.IndexOf('@')] : row.Email;
                row.Name = string.IsNullOrWhiteSpace(seed) ? "Account_" + i : seed;
            }
            var clean = GatewayLocator.SanitizeAccountName(row.Name);
            if (string.IsNullOrEmpty(clean)) clean = "Account_" + i;
            row.Name = clean;
            if (!usedNames.Add(row.Name)) { row.Error = $"Trùng tên '{row.Name}' với dòng phía trên trong file."; result.Add(row); continue; }

            row.Error = Validate(row);
            result.Add(row);
        }
        return result;
    }

    private static string? Validate(AccountRow r)
    {
        switch (r.Kind)
        {
            case AccountKind.Google:
            case AccountKind.Facebook:
                if (string.IsNullOrWhiteSpace(r.Email)) return "Thiếu Email/SĐT.";
                if (string.IsNullOrEmpty(r.Password)) return "Thiếu mật khẩu.";
                return null;
            case AccountKind.FacebookCookie:
                return LoginOptions.LooksLikeFacebookCookie(r.Cookie) ? null : "Cookie Facebook phải chứa c_user=... hoặc xs=...";
            case AccountKind.DolaCookie:
                return r.Cookie.Contains("sessionid=", StringComparison.OrdinalIgnoreCase) ? null : "Cookie Dola phải chứa sessionid=...";
            default:
                return null;
        }
    }

    private static AccountKind ParseKind(string text, AccountRow row, out string? error)
    {
        error = null;
        var k = Norm(text);
        if (k.Length == 0) return Detect(row);

        if (k.Contains("dola")) return AccountKind.DolaCookie;
        var hasCookie = k.Contains("cookie");
        var fb = k.Contains("facebook") || k == "fb" || k.StartsWith("fb");
        if (fb && hasCookie) return AccountKind.FacebookCookie;
        if (fb) return AccountKind.Facebook;
        if (k.Contains("google") || k.Contains("gmail") || k == "gg") return AccountKind.Google;
        if (hasCookie) return Detect(row); // chỉ ghi "cookie": nhìn nội dung để biết của Facebook hay Dola
        if (k.Contains("thucong") || k.Contains("manual") || k.Contains("tay") || k.Contains("khong")) return AccountKind.Manual;

        error = $"Loại đăng nhập '{text}' không nhận ra. Dùng: Thủ công, Google, Facebook, Facebook cookie, Cookie Dola.";
        return AccountKind.Manual;
    }

    /// <summary>Đoán loại khi ô "Loại" bỏ trống: cookie Facebook / cookie Dola / Gmail / Facebook thường.</summary>
    private static AccountKind Detect(AccountRow r)
    {
        if (LoginOptions.LooksLikeFacebookCookie(r.Cookie)) return AccountKind.FacebookCookie;
        if (r.Cookie.Length > 0) return AccountKind.DolaCookie;
        var mail = r.Email.ToLowerInvariant();
        if (mail.EndsWith("@gmail.com") || mail.EndsWith("@googlemail.com")) return r.Password.Length > 0 ? AccountKind.Google : AccountKind.Manual;
        if (r.Email.Length > 0 && r.Password.Length > 0) return AccountKind.Facebook;
        return AccountKind.Manual;
    }

    private static bool IsNo(string v)
    {
        var n = Norm(v);
        return n is "khong" or "no" or "false" or "0" or "off" or "tat" or "x";
    }

    private static string Norm(string s)
    {
        var d = (s ?? string.Empty).Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }
}
