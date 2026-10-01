using System;
using System.Collections.Generic;
using System.Linq;
using DolaCoordinator.Models;

namespace DolaCoordinator.Helpers;

/// <summary>Một dòng proxy đã đọc được (kèm mật khẩu dạng chữ thường, chỉ dùng tạm để mã hóa rồi lưu).</summary>
public sealed record ParsedProxy(string Scheme, string Host, int Port, string? Username, string? Password);

/// <summary>
/// Đọc danh sách proxy dán vào (mỗi dòng một proxy). Hỗ trợ:
/// <c>scheme://user:pass@host:port</c>, <c>scheme://host:port</c>, <c>user:pass@host:port</c>, <c>host:port</c>,
/// <c>host:port:user:pass</c>. Không ghi scheme thì là http.
/// </summary>
public static class ProxyParser
{
    public static (List<ParsedProxy> Proxies, List<string> Errors) ParseLines(string? text)
    {
        var proxies = new List<ParsedProxy>();
        var errors = new List<string>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (TryParse(line, out var p, out var error)) proxies.Add(p!);
            else errors.Add($"Dòng {i + 1}: {error}");
        }
        return (proxies, errors);
    }

    public static bool TryParse(string raw, out ParsedProxy? proxy, out string error)
    {
        proxy = null;
        error = string.Empty;
        var s = raw.Trim();
        // nhiều nhà cung cấp thêm đuôi sau proxy ("host:port:user:pass | ID: 17685"): bỏ phần sau dấu '|' hoặc khoảng trắng
        var bar = s.IndexOf('|');
        if (bar >= 0) s = s[..bar].Trim();
        var space = s.IndexOfAny(new[] { ' ', '\t' });
        if (space > 0) s = s[..space];
        var scheme = "http";

        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd > 0)
        {
            scheme = s[..schemeEnd].ToLowerInvariant();
            s = s[(schemeEnd + 3)..];
            if (scheme == "socks5h" || scheme == "socks") scheme = "socks5";
            if (Array.IndexOf(ProxyItem.Schemes, scheme) < 0)
            {
                error = $"không hỗ trợ kiểu '{scheme}' (dùng http, https hoặc socks5)";
                return false;
            }
        }
        s = s.TrimEnd('/');

        string? user = null, pass = null;
        string hostPort;

        // host:port:user:pass (mật khẩu có thể chứa '@' hoặc ':'): nhận ra bằng ô thứ hai là số port
        var colonParts = s.Split(':');
        var hostPortUserPass = schemeEnd <= 0 && colonParts.Length >= 4 && int.TryParse(colonParts[1], out _);

        var at = hostPortUserPass ? -1 : s.LastIndexOf('@');
        if (hostPortUserPass)
        {
            hostPort = colonParts[0] + ":" + colonParts[1];
            user = colonParts[2];
            pass = string.Join(":", colonParts.Skip(3));
        }
        else if (at >= 0)
        {
            var cred = s[..at];
            hostPort = s[(at + 1)..];
            var colon = cred.IndexOf(':');
            user = Uri.UnescapeDataString(colon >= 0 ? cred[..colon] : cred);
            pass = colon >= 0 ? Uri.UnescapeDataString(cred[(colon + 1)..]) : string.Empty;
        }
        else
        {
            var parts = s.Split(':');
            if (parts.Length == 2) hostPort = s;
            else if (parts.Length == 4)
            {
                hostPort = parts[0] + ":" + parts[1];
                user = parts[2];
                pass = parts[3];
            }
            else
            {
                error = "không đúng dạng (host:port, host:port:user:pass, user:pass@host:port hoặc scheme://...)";
                return false;
            }
        }

        var hp = hostPort.Split(':');
        if (hp.Length != 2 || string.IsNullOrWhiteSpace(hp[0]) || !int.TryParse(hp[1], out var port) || port is < 1 or > 65535)
        {
            error = "host hoặc port không hợp lệ";
            return false;
        }

        proxy = new ParsedProxy(scheme, hp[0].Trim(), port, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(user) ? null : pass);
        return true;
    }
}
