using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Helpers;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Gateway;

public sealed class GatewayHost : IGatewayHost, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(20);

    private readonly IDatabaseService _db;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly object _logLock = new();

    private Process? _process;
    private StreamWriter? _log;
    private JobObject? _job;
    private DateTime _lastFailureUtc = DateTime.MinValue;
    private string? _lastError;

    public GatewayHost(IDatabaseService db, HttpClient http)
    {
        _db = db;
        _http = http;
    }

    public string LogPath => Path.Combine(AppPaths.DataDir, "gateway.log");

    public async Task<(bool Ok, string? Error)> EnsureRunningAsync(CancellationToken ct = default)
    {
        var settings = _db.GetSettings();
        var url = string.IsNullOrWhiteSpace(settings.GatewayUrl) ? "http://127.0.0.1:8000" : settings.GatewayUrl.Trim();
        if (await IsHealthyAsync(url, ct)) return (true, null);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (false, $"Địa chỉ gateway không hợp lệ: {url}");
        if (!uri.IsLoopback)
            return (false, $"Gateway ở {uri.Host} không phản hồi (app chỉ tự bật gateway chạy trên máy này).");

        await _gate.WaitAsync(ct);
        try
        {
            if (await IsHealthyAsync(url, ct)) return (true, null); // luồng khác vừa bật xong

            if (DateTime.UtcNow - _lastFailureUtc < FailureCooldown && _lastError != null)
                return (false, _lastError);

            var result = await StartAsync(settings.GatewayDir, settings.PythonCommand, uri.Port, url, ct);
            if (!result.Ok)
            {
                _lastFailureUtc = DateTime.UtcNow;
                _lastError = result.Error;
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool Ok, string? Error)> StartAsync(string? configuredDir, string? pythonCommand, int port, string url, CancellationToken ct)
    {
        var gatewayDir = GatewayLocator.FindDir(configuredDir);
        if (gatewayDir == null)
            return (false, "Không tìm thấy gateway (thư mục 'gateway' có dola-gateway.exe, hoặc dola-render-gateway có server.py). Chọn thư mục trong tab Cài đặt.");

        StopProcess(); // dọn tiến trình cũ đã chết/treo (nếu có)

        var (fileName, args) = GatewayLocator.BuildCommand(gatewayDir, pythonCommand, "serve",
            new[] { "--host", "127.0.0.1", "--port", port.ToString() });
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = GatewayLocator.RuntimeDir(gatewayDir),
            UseShellExecute = false,
            CreateNoWindow = true, // chạy ngầm: không có cửa sổ console
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["DOLA_MAX_CONCURRENCY"] = GatewayCapacity().ToString(); // đủ chỗ cho mọi tài khoản chạy song song
        GatewayLocator.ApplyEnvironment(psi, gatewayDir);

        try
        {
            lock (_logLock)
            {
                _log?.Dispose();
                _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            // Gắn vào Job Object: app bị tắt đột ngột thì Windows tự dọn gateway + Chromium con, không để mồ côi
            _job ??= JobObject.TryCreate();
            _job?.TryAssign(process);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, GatewayLocator.IsPackaged(gatewayDir)
                ? $"Không chạy được {GatewayLocator.ExeName}.\n{ex.Message}"
                : $"Không chạy được '{fileName}'. Cài Python 3 (lệnh 'py -3') hoặc đổi lệnh Python trong Cài đặt.\n{ex.Message}");
        }

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
                return (false, $"Gateway thoát ngay khi khởi động (mã {_process.ExitCode}). Thường do thiếu thư viện: chạy 'pip install -r requirements.txt'.\n{Tail(LogPath)}");
            if (await IsHealthyAsync(url, ct)) return (true, null);
            await Task.Delay(500, ct);
        }

        StopProcess();
        return (false, $"Gateway không sẵn sàng sau {StartupTimeout.TotalSeconds:0}s.\n{Tail(LogPath)}");
    }

    public bool IsBrowserInstalled
    {
        get
        {
            try
            {
                var cache = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
                if (string.IsNullOrWhiteSpace(cache) || cache == "0")
                    cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
                bool Complete(string pattern) => Directory.Exists(cache)
                    && Directory.EnumerateDirectories(cache, pattern).Any(d => File.Exists(Path.Combine(d, "INSTALLATION_COMPLETE")));
                // Chromium đầy đủ (mở profile) + headless shell (kiểm tra phiên) đều phải có
                return Complete("chromium-*") && Complete("chromium_headless_shell-*");
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public async Task<(bool Ok, string? Error)> EnsureBrowserAsync(CancellationToken ct = default)
    {
        var gatewayDir = GatewayLocator.FindDir(_db.GetSettings().GatewayDir);
        // Bản chạy bằng Python: người dùng tự cài (patchright install chromium) hoặc dùng nút "Cài đặt trình duyệt"
        if (gatewayDir == null || !GatewayLocator.IsPackaged(gatewayDir) || IsBrowserInstalled) return (true, null);
        return await InstallBrowserAsync(null, ct);
    }

    public async Task<(bool Ok, string? Error)> InstallBrowserAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var settings = _db.GetSettings();
        var gatewayDir = GatewayLocator.FindDir(settings.GatewayDir);
        if (gatewayDir == null)
            return (false, "Không tìm thấy gateway (thư mục 'gateway' có dola-gateway.exe, hoặc dola-render-gateway có server.py).");

        await _browserGate.WaitAsync(ct);
        try
        {
            if (IsBrowserInstalled) { progress?.Report("Trình duyệt đã được cài."); return (true, null); }

            var (fileName, args) = GatewayLocator.BuildCommand(gatewayDir, settings.PythonCommand, "install-browser", Array.Empty<string>());
            var psi = new ProcessStartInfo(fileName)
            {
                WorkingDirectory = GatewayLocator.RuntimeDir(gatewayDir),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var log = new StringBuilder();
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                lock (log) log.AppendLine(line);
                progress?.Report(line.Trim());
            }

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => OnLine(e.Data);
            process.ErrorDataReceived += (_, e) => OnLine(e.Data);
            progress?.Report("Đang tải trình duyệt Chromium (~150 MB), vui lòng chờ...");
            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMinutes(20));
                await process.WaitForExitAsync(cts.Token);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                return (false, $"Không tải được trình duyệt Chromium cho gateway: {ex.Message}");
            }

            if (process.ExitCode != 0 && !IsBrowserInstalled)
            {
                string tail;
                lock (log) tail = string.Join(Environment.NewLine, log.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(6));
                return (false, $"Không tải được trình duyệt Chromium (mã {process.ExitCode}). Kiểm tra mạng / proxy / VPN rồi bấm Cài đặt lại.\n{tail}");
            }

            progress?.Report("Đã cài xong trình duyệt.");
            return (true, null);
        }
        finally
        {
            _browserGate.Release();
        }
    }

    /// <summary>Số video gateway được render cùng lúc: bằng số luồng tối đa trong cài đặt (có dư chỗ để tăng số luồng mà không phải bật lại gateway).</summary>
    private int GatewayCapacity() => Math.Clamp(_db.GetSettings().ConcurrencyLimit + 2, 8, 32);

    private async Task<bool> IsHealthyAsync(string url, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync($"{url.TrimEnd('/')}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    private void WriteLog(string? line)
    {
        if (line == null) return;
        lock (_logLock)
        {
            try { _log?.WriteLine(line); } catch (ObjectDisposedException) { } catch (IOException) { }
        }
    }

    private static string Tail(string path, int lines = 8)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines));
        }
        catch (IOException) { return string.Empty; }
    }

    private void StopProcess()
    {
        var p = _process;
        _process = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { p.Dispose(); }
    }

    public void Stop() => StopProcess();

    public void Dispose()
    {
        StopProcess();
        lock (_logLock) { _log?.Dispose(); _log = null; }
        _job?.Dispose();
    }

    /// <summary>Windows Job Object với KILL_ON_JOB_CLOSE: mọi tiến trình con bị tắt khi app thoát (kể cả khi crash).</summary>
    private sealed class JobObject : IDisposable
    {
        private IntPtr _handle;

        public static JobObject? TryCreate()
        {
            try
            {
                var h = CreateJobObject(IntPtr.Zero, null);
                if (h == IntPtr.Zero) return null;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(h, 9, ptr, (uint)size)) { CloseHandle(h); return null; }
                }
                finally { Marshal.FreeHGlobal(ptr); }
                return new JobObject { _handle = h };
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
        }

        public void TryAssign(Process p)
        {
            try { AssignProcessToJobObject(_handle, p.Handle); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
