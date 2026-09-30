using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Gateway;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Profiles;

public class AccountProfileService : IAccountProfileService
{
    private const string StatusFileName = ".profile_status.json";
    private const string CloseFlagName = ".close_request";
    private const string CookieFileName = "cookie.txt";
    private static readonly TimeSpan StatusFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(90);

    private readonly IDatabaseService _db;
    private readonly ISecurityService _security;
    private readonly IDolaGatewayClient _gateway;
    private readonly IGatewayHost _host;

    public AccountProfileService(IDatabaseService db, ISecurityService security, IDolaGatewayClient gateway, IGatewayHost host)
    {
        _db = db;
        _security = security;
        _gateway = gateway;
        _host = host;
    }

    // ------------------------------------------------------------------ gateway location

    public string? GatewayDir => GatewayLocator.FindDir(_db.GetSettings().GatewayDir);

    public string? AccountsDir => GatewayDir is { } dir ? GatewayLocator.AccountsDir(dir) : null;

    public string? EnvironmentProblem => GatewayDir == null
        ? "Không tìm thấy gateway (thư mục 'gateway' có dola-gateway.exe, hoặc dola-render-gateway có server.py). Chọn thư mục trong tab Cài đặt."
        : null;

    private string RequireAccountsDir()
        => AccountsDir ?? throw new InvalidOperationException(EnvironmentProblem);

    public void ResolvePaths(AccountProfile profile)
        => profile.FolderPath = AccountsDir is { } dir ? Path.Combine(dir, profile.Name) : string.Empty;

    // ------------------------------------------------------------------ create / adopt / discover

    public AccountProfile CreateProfile(string name, string? notes)
    {
        name = name.Trim();
        if (!GatewayLocator.IsValidAccountName(name))
            throw new ArgumentException("Tên tài khoản chỉ gồm A-Z a-z 0-9 _ - (tối đa 32 ký tự), giống quy định của gateway.");

        var profile = new AccountProfile { Name = name, Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() };
        profile.FolderPath = Path.Combine(RequireAccountsDir(), name);
        Directory.CreateDirectory(profile.FolderPath); // gateway tự liệt kê thư mục này thành một tài khoản
        _db.UpsertProfile(profile);
        return profile;
    }

    public AccountProfile AdoptSession(DolaSession session)
    {
        var taken = _db.GetAllProfiles().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = GatewayLocator.SanitizeAccountName(session.Name);
        var name = baseName;
        for (var i = 2; taken.Contains(name); i++)
        {
            var suffix = $"_{i}";
            name = (baseName.Length + suffix.Length > 32 ? baseName[..(32 - suffix.Length)] : baseName) + suffix;
        }

        // Gateway nhận tài khoản theo tên thư mục nên phiên phải mang đúng tên đó khi gửi request
        if (session.Name != name)
        {
            session.Name = name;
            _db.UpsertSession(session);
        }

        var profile = CreateProfile(name, null);
        var token = session.PlainToken ?? _security.Decrypt(session.EncryptedToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            WriteCookieFileIfMissing(profile, token);
            var sid = SessionIdOf(token);
            if (sid != null) profile.SessionHash = HashOf(sid);
        }
        profile.LinkedSessionId = session.Id;
        _db.UpsertProfile(profile);
        return profile;
    }

    public List<AccountProfile> DiscoverGatewayAccounts(IEnumerable<string> knownNames)
    {
        var found = new List<AccountProfile>();
        var accountsDir = AccountsDir;
        if (accountsDir == null || !Directory.Exists(accountsDir)) return found;

        var known = knownNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(accountsDir))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || known.Contains(name) || !GatewayLocator.IsValidAccountName(name)) continue;
            if (!LooksLikeAccountFolder(dir, name)) continue; // rác Chromium nằm nhầm ở gốc accounts/

            var profile = new AccountProfile { Name = name, FolderPath = dir };

            // Tài khoản có sẵn của gateway: cookie.txt chính là phiên của nó
            var cookieFile = Path.Combine(dir, CookieFileName);
            var cookie = File.Exists(cookieFile) ? SafeReadAllText(cookieFile)?.Trim() : null;
            if (!string.IsNullOrEmpty(cookie))
            {
                var session = new DolaSession
                {
                    Name = name,
                    PlainToken = cookie,
                    EncryptedToken = _security.Encrypt(cookie),
                    DailyLimit = _db.GetSettings().DefaultDailyQuota,
                };
                _db.UpsertSession(session);
                profile.LinkedSessionId = session.Id;
                var sid = SessionIdOf(cookie);
                if (sid != null) profile.SessionHash = HashOf(sid);
            }

            _db.UpsertProfile(profile);
            found.Add(profile);
        }
        return found;
    }

    /// <summary>
    /// Tên các thư mục nội bộ của Chromium. Khi ai đó chạy script gateway với tên tài khoản rỗng (vd. THEM_COOKIE.bat bỏ
    /// trống tên), chính thư mục accounts/ trở thành user-data-dir và các thư mục này xuất hiện ngay ở gốc — gateway
    /// (pool.accounts) coi mỗi thư mục là một tài khoản, app thì không được làm vậy.
    /// </summary>
    private static readonly HashSet<string> ChromiumInternalFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ActorSafetyLists", "AmountExtractionHeuristicRegexes", "BrowserMetrics", "CaptchaProviders", "CertificateRevocation",
        "Crashpad", "Default", "FileTypePolicies", "FirstPartySetsPreloaded", "GPUPersistentCache", "GrShaderCache",
        "GraphiteDawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "MEIPreload", "OnDeviceHeadSuggestModel",
        "OptimizationGuideModelsManifest", "OriginTrials", "PKIMetadata", "PrivacySandboxAttestationsPreloaded",
        "SSLErrorAssistant", "SafetyTips", "ShaderCache", "TrustTokenKeyCommitments", "WasmTtsEngine", "WidevineCdm",
        "ZxcvbnData", "component_crx_cache", "extensions_crx_cache", "hyphen-data", "segmentation_platform",
        "optimization_guide_model_store", "Variations",
    };

    /// <summary>Thư mục có giống một tài khoản không: không thuộc rác Chromium và có dấu hiệu profile (cookie.txt / Local State / Default) hoặc còn trống.</summary>
    private static bool LooksLikeAccountFolder(string dir, string name)
    {
        if (ChromiumInternalFolders.Contains(name)) return false;

        if (File.Exists(Path.Combine(dir, CookieFileName)) || File.Exists(Path.Combine(dir, "Local State"))
            || Directory.Exists(Path.Combine(dir, "Default")))
            return true;

        return !Directory.EnumerateFileSystemEntries(dir).Any(); // vừa tạo, chưa đăng nhập
    }

    public DolaSession AttachCookie(AccountProfile profile, string cookieHeader)
    {
        var header = cookieHeader.Trim();
        if (header.Length == 0) throw new ArgumentException("Cookie rỗng.", nameof(cookieHeader));

        Directory.CreateDirectory(profile.FolderPath);
        // Cookie Facebook do gateway tự chuyển thành cookie Dola khi kiểm tra, không ghi vào cookie.txt
        if (!IsFacebookCookie(header))
            File.WriteAllText(Path.Combine(profile.FolderPath, CookieFileName), header, Encoding.UTF8);

        var session = UpsertSession(profile, header, SessionIdOf(header));
        _db.UpsertProfile(profile);
        return session;
    }

    // ------------------------------------------------------------------ launch / close / probe

    public async Task LaunchAsync(AccountProfile profile, LoginOptions? options = null, CancellationToken ct = default)
    {
        var gatewayDir = GatewayDir ?? throw new InvalidOperationException(EnvironmentProblem);
        if (!GatewayLocator.IsValidAccountName(profile.Name))
            throw new InvalidOperationException($"Tên '{profile.Name}' không hợp lệ với gateway (chỉ A-Z a-z 0-9 _ -).");
        if (Probe(profile).Running)
            throw new InvalidOperationException("Profile này đang mở.");

        // Gateway đang render bằng đúng profile này thì Chromium khóa thư mục: không mở song song được
        var accounts = await _gateway.GetAccountsAsync(ct);
        if (accounts?.FirstOrDefault(a => a.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))?.Busy == true)
            throw new InvalidOperationException($"Tài khoản '{profile.Name}' đang render trên gateway. Chờ render xong rồi mở profile.");

        var browser = await _host.EnsureBrowserAsync(ct); // bản đóng gói: tải Chromium lần đầu nếu máy chưa có
        if (!browser.Ok) throw new BrowserMissingException(browser.Error ?? "Chưa có trình duyệt Chromium cho gateway.");

        var (fileName, launchArgs) = GatewayLocator.BuildCommand(gatewayDir, _db.GetSettings().PythonCommand, "open-profile", new[] { profile.Name });
        var python = fileName;
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = gatewayDir, // các script gateway dùng đường dẫn tương đối "accounts/<tên>"
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options?.IsAutomatic == true, // thông tin đăng nhập đi qua stdin, không qua dòng lệnh
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in launchArgs) psi.ArgumentList.Add(a);
        if (options?.IsAutomatic == true)
        {
            psi.ArgumentList.Add("--login");
            psi.ArgumentList.Add(options.ScriptMode!);
        }
        if (options != null)
        {
            psi.ArgumentList.Add("--after");
            psi.ArgumentList.Add(options.After == AfterLogin.Close ? "close" : "keep");
        }

        var output = new Queue<string>();
        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (output)
            {
                output.Enqueue(line);
                while (output.Count > 15) output.Dequeue();
            }
        }

        Process proc;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => Capture(e.Data);
            proc.ErrorDataReceived += (_, e) => Capture(e.Data);
            proc.Start();
            proc.BeginOutputReadLine(); // phải đọc liên tục, nếu không pipe đầy sẽ treo script
            proc.BeginErrorReadLine();

            if (options?.IsAutomatic == true)
            {
                // Một dòng JSON rồi đóng stdin: mật khẩu/khóa 2FA không xuất hiện trong danh sách tiến trình hay file nào
                var credentials = options.Method == LoginMethod.FacebookCookie
                    ? JsonSerializer.Serialize(new { cookie = options.Cookie.Trim() })
                    : JsonSerializer.Serialize(new { email = options.Email, password = options.Password, totp = options.Totp });
                try
                {
                    await proc.StandardInput.WriteLineAsync(credentials);
                    proc.StandardInput.Close();
                }
                catch (IOException)
                {
                    // Script thoát trước khi đọc (lỗi tham số...): vòng chờ bên dưới sẽ báo lỗi kèm nội dung script in ra
                }
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(GatewayLocator.IsPackaged(gatewayDir)
                ? $"Không chạy được {GatewayLocator.ExeName}.\n{ex.Message}"
                : $"Không chạy được '{python}'. Cài Python 3 (lệnh 'py -3') hoặc đổi lệnh Python trong Cài đặt.\n{ex.Message}", ex);
        }

        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (!Probe(profile).Running)
        {
            if (proc.HasExited)
            {
                string tail;
                lock (output) tail = string.Join(Environment.NewLine, output);
                var hint = tail.Contains("No module named", StringComparison.OrdinalIgnoreCase)
                    ? $"{Environment.NewLine}{Environment.NewLine}Python trên máy này thiếu thư viện của gateway. Chạy một lần trong thư mục gateway:{Environment.NewLine}" +
                      $"  {python} -m pip install -r requirements.txt{Environment.NewLine}  {python} -m patchright install chromium{Environment.NewLine}" +
                      "(hoặc dùng bản đóng gói có thư mục 'gateway' chứa dola-gateway.exe — không cần Python)."
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Không mở được profile '{profile.Name}' (mã thoát {proc.ExitCode}).{Environment.NewLine}{tail}{hint}");
            }
            if (DateTime.UtcNow > deadline)
            {
                TryKillTree(proc);
                throw new TimeoutException($"Chromium không sẵn sàng sau {LaunchTimeout.TotalSeconds:0} giây.");
            }
            await Task.Delay(500, ct);
        }

        profile.LastLaunchAt = DateTime.UtcNow;
        _db.UpsertProfile(profile);
    }

    public async Task CloseAsync(AccountProfile profile, CancellationToken ct = default)
    {
        var probe = Probe(profile);
        if (!probe.Running) return;

        // Lưu phiên lần cuối trước khi đóng
        if (probe.LoggedIn && !string.IsNullOrWhiteSpace(probe.CookieHeader))
            CaptureSession(profile, probe.CookieHeader);

        var pid = ReadStatus(profile)?.Pid;
        try
        {
            File.WriteAllText(Path.Combine(profile.FolderPath, CloseFlagName), "1");
        }
        catch (IOException) { /* không ghi được cờ thì chuyển sang tắt tiến trình bên dưới */ }

        for (var i = 0; i < 40; i++) // tối đa ~20 giây để Chromium đóng êm
        {
            await Task.Delay(500, ct);
            if (!Probe(profile).Running) return;
        }

        // Không đóng êm được: tắt cả cây tiến trình (script + Chromium) để nhả khóa profile cho gateway
        if (pid is int p) TryKillTree(p);
        TryDelete(Path.Combine(profile.FolderPath, StatusFileName));
        TryDelete(Path.Combine(profile.FolderPath, CloseFlagName));
        await Task.Delay(500, ct);
    }

    public ProfileProbe Probe(AccountProfile profile)
    {
        var st = ReadStatus(profile);
        if (st == null) return ProfileProbe.Closed;

        string? cookie = null;
        if (st.LoggedIn)
        {
            var cookieFile = Path.Combine(profile.FolderPath, CookieFileName);
            cookie = File.Exists(cookieFile) ? SafeReadAllText(cookieFile)?.Trim() : null;
        }
        return new ProfileProbe(true, st.LoggedIn, cookie, st.NeedHuman);
    }

    public bool IsAccountOpen(string accountName)
    {
        var dir = AccountsDir;
        if (dir == null) return false;
        return ReadStatus(new AccountProfile { Name = accountName, FolderPath = Path.Combine(dir, accountName) }) != null;
    }

    private sealed record StatusInfo(int Pid, bool LoggedIn, string? NeedHuman);

    /// <summary>Đọc .profile_status.json; null nếu không có, đã cũ (script treo) hoặc tiến trình đã chết.</summary>
    private static StatusInfo? ReadStatus(AccountProfile profile)
    {
        if (string.IsNullOrEmpty(profile.FolderPath)) return null;
        var file = Path.Combine(profile.FolderPath, StatusFileName);
        if (!File.Exists(file)) return null;

        try
        {
            using var doc = JsonDocument.Parse(SafeReadAllText(file) ?? "{}");
            var root = doc.RootElement;
            var pid = root.GetProperty("pid").GetInt32();
            var updated = DateTimeOffset.FromUnixTimeMilliseconds((long)(root.GetProperty("updated").GetDouble() * 1000));
            var loggedIn = root.TryGetProperty("logged_in", out var li) && li.ValueKind == JsonValueKind.True;

            if (DateTimeOffset.UtcNow - updated > StatusFreshness) return null;
            if (!IsProcessAlive(pid)) { TryDelete(file); return null; }
            var needHuman = root.TryGetProperty("need_human", out var nh) && nh.ValueKind == JsonValueKind.String ? nh.GetString() : null;
            return new StatusInfo(pid, loggedIn, needHuman);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            return null; // file đang được ghi dở hoặc hỏng: coi như chưa sẵn sàng, lượt sau đọc lại
        }
    }

    // ------------------------------------------------------------------ session capture

    public (bool LoggedInThisRun, bool SessionChanged) CaptureFromCookieFile(AccountProfile profile)
    {
        var file = Path.Combine(profile.FolderPath, CookieFileName);
        if (!File.Exists(file) || profile.LastLaunchAt is not { } launchedAt) return (false, false);

        // cookie.txt chỉ được ghi mới khi script phát hiện đăng nhập → ghi sau lúc mở nghĩa là đăng nhập lần này
        if (File.GetLastWriteTimeUtc(file) < launchedAt.AddSeconds(-2)) return (false, false);

        var header = SafeReadAllText(file)?.Trim();
        if (string.IsNullOrEmpty(header) || SessionIdOf(header) == null) return (false, false);

        return (true, CaptureSession(profile, header));
    }

    public bool CaptureSession(AccountProfile profile, string cookieHeader)
    {
        var header = cookieHeader.Trim();
        var sid = SessionIdOf(header);
        var sessionChanged = false;

        if (!string.IsNullOrEmpty(sid) && HashOf(sid) != profile.SessionHash)
        {
            UpsertSession(profile, header, sid);
            sessionChanged = true;
        }

        if (profile.LoginStatus != ProfileLoginStatus.LoggedIn || profile.LastLoginAt == null)
            profile.LastLoginAt = DateTime.UtcNow;
        _db.UpsertProfile(profile);
        return sessionChanged;
    }

    /// <summary>Tạo/cập nhật DolaSession của profile. Token mới → trạng thái Unknown, cần kiểm tra lại với gateway.</summary>
    private DolaSession UpsertSession(AccountProfile profile, string header, string? sid)
    {
        var session = (profile.LinkedSessionId != null ? _db.GetSessionById(profile.LinkedSessionId) : null)
                      ?? new DolaSession { DailyLimit = _db.GetSettings().DefaultDailyQuota };
        session.Name = profile.Name;
        session.PlainToken = header;
        session.EncryptedToken = _security.Encrypt(header);
        session.Status = SessionStatus.Unknown;
        session.LastErrorMessage = null;
        _db.UpsertSession(session);

        profile.LinkedSessionId = session.Id;
        profile.SessionHash = string.IsNullOrEmpty(sid) ? null : HashOf(sid);
        return session;
    }

    private static bool IsFacebookCookie(string header) => header.Contains("c_user=") || header.Contains("xs=");

    /// <summary>Lấy giá trị sessionid (hoặc sessionid_ss) từ chuỗi "a=1; b=2"; null nếu là dạng khác (JSON, token thô).</summary>
    private static string? SessionIdOf(string header)
    {
        string? sid = null, sidSs = null;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (value.Length == 0) continue;
            if (key == "sessionid") sid = value;
            else if (key == "sessionid_ss") sidSs = value;
        }
        return sid ?? sidSs;
    }

    private static string HashOf(string sid)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..12];

    private static void WriteCookieFileIfMissing(AccountProfile profile, string token)
    {
        if (IsFacebookCookie(token)) return;
        var file = Path.Combine(profile.FolderPath, CookieFileName);
        if (!File.Exists(file)) File.WriteAllText(file, token.Trim(), Encoding.UTF8);
    }

    // ------------------------------------------------------------------ folders / delete

    public void OpenFolder(AccountProfile profile)
    {
        Directory.CreateDirectory(profile.FolderPath);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{profile.FolderPath}\"") { UseShellExecute = true });
    }

    public void OpenAccountsDir()
    {
        var dir = RequireAccountsDir();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    public async Task DeleteAsync(AccountProfile profile, CancellationToken ct = default)
    {
        if (Probe(profile).Running)
            throw new InvalidOperationException("Profile đang mở. Đóng trước khi xóa.");

        // Xóa qua gateway để nó dọn luôn metadata (accounts_meta) và từ chối khi đang render
        var (ok, unreachable, error) = await _gateway.DeleteAccountAsync(profile.Name, ct);
        if (!ok && !unreachable)
            throw new InvalidOperationException(error ?? "Gateway từ chối xóa tài khoản.");

        // Gateway tắt (hoặc đã xóa rồi): dọn thư mục trực tiếp. IOException nếu file còn bị khóa → giữ nguyên bản ghi.
        if (Directory.Exists(profile.FolderPath))
            Directory.Delete(profile.FolderPath, recursive: true);

        if (profile.LinkedSessionId != null)
            _db.DeleteSession(profile.LinkedSessionId);
        _db.DeleteProfile(profile.Id);
    }

    // ------------------------------------------------------------------ helpers

    private static string? SafeReadAllText(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }      // không có tiến trình với pid này
        catch (InvalidOperationException) { return false; }
    }

    private static void TryKillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static void TryKillTree(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
