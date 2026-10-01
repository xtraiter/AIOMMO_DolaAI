using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DolaCoordinator.Helpers;

/// <summary>
/// Tìm gateway và dựng lệnh chạy nó. Gateway có hai dạng:
/// bản đóng gói (thư mục "gateway" chứa dola-gateway.exe — không cần cài Python) và bản mã nguồn
/// (thư mục dola-render-gateway chứa server.py, chạy bằng Python). Mọi profile là thư mục con
/// accounts/&lt;tên&gt; của gateway nên đây là điểm gốc của toàn bộ chức năng profile.
/// </summary>
public static class GatewayLocator
{
    public const string ExeName = "dola-gateway.exe";

    /// <summary>Gateway đã đóng gói thành exe (không cần Python).</summary>
    public static bool IsPackaged(string gatewayDir) => File.Exists(Path.Combine(gatewayDir, ExeName));

    /// <summary>
    /// Lệnh chạy gateway: verb = "serve" (server API), "open-profile" (mở/đăng nhập một profile) hoặc "install-browser" (tải Chromium).
    /// Bản đóng gói: dola-gateway.exe &lt;verb&gt; ...; bản mã nguồn: python -m uvicorn server:app ... / python open_profile.py ...
    /// </summary>
    public static (string FileName, List<string> Args) BuildCommand(string gatewayDir, string? pythonCommand, string verb, IEnumerable<string> args)
    {
        var list = new List<string>();
        if (IsPackaged(gatewayDir))
        {
            list.Add(verb);
            list.AddRange(args);
            return (Path.Combine(gatewayDir, ExeName), list);
        }

        var (python, pythonArgs) = ParsePython(pythonCommand);
        list.AddRange(pythonArgs);
        if (verb == "serve") list.AddRange(new[] { "-m", "uvicorn", "server:app" });
        else if (verb == "install-browser") list.Add("gateway_main.py");
        else list.Add("open_profile.py");
        if (verb == "install-browser") list.Add(verb);
        list.AddRange(args);
        return (ResolveExecutable(python), list);
    }

    /// <summary>Thư mục gateway: ưu tiên đường dẫn cấu hình, sau đó đi ngược lên từ vị trí exe / thư mục hiện tại.</summary>
    public static string? FindDir(string? configuredDir = null)
    {
        if (IsGatewayDir(configuredDir)) return configuredDir;

        foreach (var start in StartPoints())
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                foreach (var name in new[] { "gateway", "dola-render-gateway" })
                {
                    var candidate = Path.Combine(dir.FullName, name);
                    if (IsGatewayDir(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    public static bool IsGatewayDir(string? dir)
        => !string.IsNullOrWhiteSpace(dir)
           && (File.Exists(Path.Combine(dir, "server.py")) || File.Exists(Path.Combine(dir, ExeName)));

    /// <summary>
    /// Thư mục chạy của gateway = nơi gateway giữ DỮ LIỆU (accounts/, tasks.db, pool_usage.db, downloads/, refs/...).
    /// Bản đã cài bằng bộ cài: %APPDATA%\AIOMMO DolaAI\gateway (thư mục chương trình ở Program Files chỉ đọc).
    /// Bản chạy thẳng từ thư mục hoặc chạy bằng Python từ mã nguồn: chính thư mục gateway như trước.
    /// </summary>
    public static string RuntimeDir(string gatewayDir)
        => AppPaths.IsInstalled && IsPackaged(gatewayDir) ? AppPaths.InstalledGatewayDataDir : gatewayDir;

    public static string AccountsDir(string gatewayDir) => Path.Combine(RuntimeDir(gatewayDir), "accounts");

    /// <summary>
    /// Khi dữ liệu tách khỏi thư mục chương trình, báo cho gateway biết extension Dola30 và trang web nằm ở đâu
    /// (chúng vẫn nằm trong thư mục cài đặt, chỉ đọc).
    /// </summary>
    public static void ApplyEnvironment(ProcessStartInfo psi, string gatewayDir)
    {
        if (string.Equals(RuntimeDir(gatewayDir), gatewayDir, StringComparison.OrdinalIgnoreCase)) return;
        psi.Environment["DOLA_EXTENSION_DIR"] = Path.Combine(gatewayDir, "extensions", "dola30");
        psi.Environment["DOLA_WEB_DIR"] = Path.Combine(gatewayDir, "web");
    }

    /// <summary>Tên tài khoản hợp lệ theo gateway (NAME_RE trong server.py): A-Z a-z 0-9 _ - tối đa 32 ký tự.</summary>
    public static bool IsValidAccountName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32) return false;
        foreach (var ch in name)
        {
            var ok = ch < 128 && (char.IsLetterOrDigit(ch) || ch is '_' or '-');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Chuyển tên bất kỳ thành tên hợp lệ cho gateway (bỏ dấu, thay ký tự lạ bằng '_').</summary>
    public static string SanitizeAccountName(string? name)
    {
        var normalized = (name ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            var c = ch == 'đ' ? 'd' : ch == 'Đ' ? 'D' : ch;
            sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c is '_' or '-') ? c : '_');
        }
        var s = sb.ToString().Trim('_');
        if (s.Length == 0) s = "account";
        return s.Length > 32 ? s[..32] : s;
    }

    /// <summary>"py -3" → ("py", ["-3"]). Mặc định giống các file .bat của dự án: py -3.</summary>
    public static (string FileName, List<string> Args) ParsePython(string? command)
    {
        var parts = (string.IsNullOrWhiteSpace(command) ? "py -3" : command)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (parts[0], new List<string>(parts[1..]));
    }

    /// <summary>
    /// "py" → đường dẫn .exe đầy đủ theo thứ tự PATH (giống where.exe). Truyền tên trần cho Process.Start có thể
    /// trỏ nhầm vào một mục không phải py.exe và launcher báo "Unable to create process", nên luôn dùng đường dẫn đầy đủ.
    /// </summary>
    public static string ResolveExecutable(string name)
    {
        if (Path.IsPathRooted(name) || name.Contains('\\') || name.Contains('/')) return name;

        var fileName = Path.HasExtension(name) ? name : name + ".exe";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* mục PATH chứa ký tự không hợp lệ: bỏ qua */ }
        }
        return name;
    }

    private static IEnumerable<string> StartPoints()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir)) yield return exeDir;
        yield return AppDomain.CurrentDomain.BaseDirectory;
        yield return Environment.CurrentDirectory;
    }
}
