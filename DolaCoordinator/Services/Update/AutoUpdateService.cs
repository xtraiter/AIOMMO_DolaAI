using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Update;

public class AutoUpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;

    /// <summary>Host của URL kiểm tra cập nhật vừa dùng: gói tải về chỉ được phép nằm trên đúng host này.</summary>
    private string? _trustedHost;

    public string CurrentVersionString => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public AutoUpdateService(HttpClient httpClient, IDatabaseService databaseService)
    {
        _httpClient = httpClient;
        _databaseService = databaseService;
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(string? customUrl = null, CancellationToken ct = default)
    {
        try
        {
            var settings = _databaseService.GetSettings();
            var url = !string.IsNullOrWhiteSpace(customUrl) ? customUrl.Trim() : settings.UpdateCheckUrl;
            if (string.IsNullOrWhiteSpace(url)) return null; // để trống = tắt tự cập nhật

            _trustedHost = RequireHttps(url, "URL kiểm tra cập nhật").Host;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));

            var json = await _httpClient.GetStringAsync(url, cts.Token);
            var updateInfo = JsonSerializer.Deserialize<UpdateInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (updateInfo == null || string.IsNullOrWhiteSpace(updateInfo.Version))
            {
                return null;
            }

            if (Version.TryParse(updateInfo.Version, out var remoteVer) &&
                Version.TryParse(CurrentVersionString, out var localVer))
            {
                if (remoteVer > localVer)
                {
                    return updateInfo;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Chỉ chấp nhận URL HTTPS hợp lệ. Cập nhật thay thế mã đang chạy nên không được đi qua kênh không mã hóa.</summary>
    private static Uri RequireHttps(string url, string what)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"{what} phải là địa chỉ HTTPS hợp lệ.");
        return uri;
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo updateInfo, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Gói cập nhật sẽ được giải nén đè lên thư mục chạy và thực thi → bắt buộc: HTTPS, cùng host với file version.json,
        // và có mã SHA-256 để đối chiếu. Thiếu một trong ba thì từ chối, không tải.
        var downloadUri = RequireHttps(updateInfo.DownloadUrl, "Địa chỉ tải gói cập nhật");
        if (_trustedHost == null || !downloadUri.Host.Equals(_trustedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Gói cập nhật nằm ở máy chủ khác ({downloadUri.Host}) với nơi công bố phiên bản ({_trustedHost ?? "?"}). Đã từ chối.");
        if (string.IsNullOrWhiteSpace(updateInfo.Sha256Checksum))
            throw new InvalidOperationException("Bản cập nhật không có mã SHA-256 (sha256Checksum) nên không thể xác minh. Đã từ chối.");

        var tempFolder = Path.Combine(Path.GetTempPath(), "DolaCoordinator_Update");
        Directory.CreateDirectory(tempFolder);
        var targetZipPath = Path.Combine(tempFolder, $"update_v{updateInfo.Version}.zip");

        string actualHash;
        using (var response = await _httpClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var sourceStream = await response.Content.ReadAsStreamAsync(ct);
            using var destStream = new FileStream(targetZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await destStream.WriteAsync(buffer, 0, bytesRead, ct);
                hasher.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    int percent = (int)((double)totalRead / totalBytes * 100);
                    progress?.Report(percent);
                }
            }

            actualHash = Convert.ToHexString(hasher.GetHashAndReset());
        }

        if (!actualHash.Equals(updateInfo.Sha256Checksum.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(targetZipPath);
            throw new InvalidOperationException("Gói cập nhật sai mã SHA-256 (file hỏng hoặc bị thay đổi). Đã xóa gói và hủy cài đặt.");
        }

        progress?.Report(100);
        return targetZipPath;
    }

    /// <summary>Từ chối gói có mục trỏ ra ngoài thư mục ứng dụng (zip-slip: "..\\..\\" hoặc đường dẫn tuyệt đối).</summary>
    private static void EnsureArchiveStaysInside(string zipPath, string targetDir)
    {
        var root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var full = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Gói cập nhật chứa đường dẫn không an toàn: {entry.FullName}");
        }
    }

    public void ApplyUpdateAndRestart(string downloadedPackagePath)
    {
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            throw new InvalidOperationException("Không thể xác định vị trí thực thi của ứng dụng.");
        }

        var appDir = Path.GetDirectoryName(currentExe)!;
        EnsureArchiveStaysInside(downloadedPackagePath, appDir);
        var updaterScriptPath = Path.Combine(Path.GetTempPath(), $"dola_updater_{Guid.NewGuid():N}.cmd");

        // Batch script to wait, kill process, extract update, restart app, and clean up
        var scriptContent = $@"@echo off
chcp 65001 > nul
echo [Dola Updater] Đang chờ ứng dụng đóng hoàn toàn...
timeout /t 2 /nobreak > nul

taskkill /f /im DolaCoordinator.exe > nul 2>&1

echo [Dola Updater] Đang giải nén và cập nhật file mới...
powershell -NoProfile -ExecutionPolicy Bypass -Command ""Expand-Archive -LiteralPath '{downloadedPackagePath.Replace("'", "''")}' -DestinationPath '{appDir.Replace("'", "''")}' -Force""

echo [Dola Updater] Khởi động lại ứng dụng...
start """" ""{currentExe}""

timeout /t 1 /nobreak > nul
del ""%~f0""
exit
";

        File.WriteAllText(updaterScriptPath, scriptContent);

        // Start updater script detached
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{updaterScriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        Environment.Exit(0);
    }
}
