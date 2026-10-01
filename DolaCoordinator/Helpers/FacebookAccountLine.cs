using System;
using System.Linq;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>
/// Dòng tài khoản Facebook theo kiểu các shop hay bán: <c>UID|Mật khẩu|Cookie|Token|Ngày|Nước</c>
/// (thứ tự có thể xê dịch). App chỉ cần phần cookie để đăng nhập; UID và mật khẩu được giữ lại để
/// đặt tên tài khoản và đăng nhập lại kiểu Facebook thường khi cookie hết hạn. Token, ngày, nước bị bỏ.
/// </summary>
public sealed record FacebookAccountLine(string Uid, string Password, string Cookie, string Token)
{
    /// <summary>Tách một dòng dán vào. Trả false nếu không phải định dạng nhiều cột (không có dấu '|' hoặc không thấy cookie Facebook).</summary>
    public static bool TryParse(string? raw, out FacebookAccountLine line)
    {
        line = new FacebookAccountLine(string.Empty, string.Empty, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim().TrimStart('﻿');
        if (!text.Contains('|')) return false;

        var parts = text.Split('|').Select(p => p.Trim()).ToArray();
        var ci = Array.FindIndex(parts, LoginOptions.LooksLikeFacebookCookie);
        if (ci < 0) return false;

        var cookie = parts[ci];

        // UID: cột toàn số đứng trước cookie; nếu không có thì lấy từ c_user=... trong cookie.
        var uid = parts.Take(ci).FirstOrDefault(IsDigits) ?? CUserFrom(cookie) ?? string.Empty;

        // Mật khẩu: cột ngay trước cookie mà không phải UID (toàn số) và không phải token.
        var password = string.Empty;
        for (var i = ci - 1; i >= 0; i--)
        {
            var p = parts[i];
            if (p.Length == 0 || IsDigits(p) || LooksLikeToken(p)) continue;
            password = p;
            break;
        }

        // Token (EAAA...): cột sau cookie — app không dùng, chỉ nhận ra để không nhầm là mật khẩu.
        var token = parts.Skip(ci + 1).FirstOrDefault(LooksLikeToken) ?? string.Empty;

        line = new FacebookAccountLine(uid, password, cookie, token);
        return true;
    }

    private static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    private static bool LooksLikeToken(string s) => s.StartsWith("EAA", StringComparison.Ordinal);

    private static string? CUserFrom(string cookie)
    {
        foreach (var item in cookie.Split(';'))
        {
            var kv = item.Trim();
            if (kv.StartsWith("c_user=", StringComparison.OrdinalIgnoreCase))
            {
                var v = kv["c_user=".Length..].Trim();
                return IsDigits(v) ? v : null;
            }
        }
        return null;
    }
}
