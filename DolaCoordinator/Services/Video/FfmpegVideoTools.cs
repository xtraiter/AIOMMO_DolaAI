using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DolaCoordinator.Services.Video;

public sealed class FfmpegVideoTools : IVideoTools
{
    private string? _path;
    private bool _searched;

    public string? FfmpegPath
    {
        get
        {
            if (!_searched)
            {
                _path = Find();
                _searched = true;
            }
            return _path;
        }
    }

    public bool IsAvailable => FfmpegPath != null;

    /// <summary>Ưu tiên bản đóng gói cùng app (thư mục tools), sau đó tới ffmpeg có sẵn trong PATH.</summary>
    private static string? Find()
    {
        var dirs = new List<string>();
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(exeDir))
        {
            dirs.Add(Path.Combine(exeDir, "tools"));
            dirs.Add(exeDir);
        }
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
        dirs.Add(AppContext.BaseDirectory);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"')));

        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(dir, "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* mục PATH không hợp lệ */ }
        }
        return null;
    }

    public async Task<(bool Ok, string? Error)> ExtractLastFrameAsync(string videoPath, string outImagePath, CancellationToken ct = default)
    {
        if (!IsAvailable) return (false, NoFfmpeg);
        if (!File.Exists(videoPath)) return (false, $"Không thấy file video: {videoPath}");
        Directory.CreateDirectory(Path.GetDirectoryName(outImagePath)!);
        TryDelete(outImagePath);

        // -sseof -1: nhảy tới 1 giây trước điểm cuối rồi đọc tiếp; -update 1 ghi đè cùng một file ảnh → còn lại khung cuối
        var (ok, err) = await RunAsync(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-sseof", "-1", "-i", videoPath, "-update", "1", outImagePath,
        }, ct);
        if (ok && File.Exists(outImagePath) && new FileInfo(outImagePath).Length > 0) return (true, null);

        // Video quá ngắn / không tua được: lấy khung ở giây đầu
        TryDelete(outImagePath);
        (ok, err) = await RunAsync(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", videoPath, "-update", "1", outImagePath,
        }, ct);
        return ok && File.Exists(outImagePath) && new FileInfo(outImagePath).Length > 0
            ? (true, null)
            : (false, $"ffmpeg không lấy được khung hình cuối: {err}");
    }

    public async Task<(bool Ok, string? Error)> MergeAsync(IReadOnlyList<string> videoPaths, string outPath, CancellationToken ct = default)
    {
        if (!IsAvailable) return (false, NoFfmpeg);
        if (videoPaths.Count == 0) return (false, "Chưa có video nào để ghép.");
        var missing = videoPaths.FirstOrDefault(p => !File.Exists(p));
        if (missing != null) return (false, $"Không thấy file video: {missing}");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        if (videoPaths.Count == 1)
        {
            File.Copy(videoPaths[0], outPath, overwrite: true);
            return (true, null);
        }

        var list = Path.Combine(Path.GetTempPath(), $"aiommo_concat_{Guid.NewGuid():N}.txt");
        try
        {
            var sb = new StringBuilder();
            foreach (var p in videoPaths)
                sb.Append("file '").Append(Path.GetFullPath(p).Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
            await File.WriteAllTextAsync(list, sb.ToString(), new UTF8Encoding(false), ct);

            TryDelete(outPath);
            // 1) ghép nguyên bản: nhanh, không đổi chất lượng (các phần cùng model nên thường cùng thông số)
            var (ok, err) = await RunAsync(new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list,
                "-c", "copy", "-movflags", "+faststart", outPath,
            }, ct);
            if (ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0) return (true, null);

            // 2) khác thông số (độ phân giải, codec...): mã hóa lại toàn bộ
            TryDelete(outPath);
            (ok, err) = await RunAsync(new[]
            {
                "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", list,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outPath,
            }, ct);
            return ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0
                ? (true, null)
                : (false, $"ffmpeg không ghép được: {err}");
        }
        finally
        {
            TryDelete(list);
        }
    }

    private const string NoFfmpeg =
        "Không tìm thấy ffmpeg.exe. Bản đóng gói có sẵn trong thư mục 'tools' cạnh app; nếu chạy từ mã nguồn, cài ffmpeg và thêm vào PATH.";

    private async Task<(bool Ok, string Error)> RunAsync(IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            _ = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
                throw;
            }
            var text = (await stderr).Trim();
            return (proc.ExitCode == 0, text.Length > 400 ? text[^400..] : text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "ffmpeg chạy quá 10 phút, đã dừng.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, ex.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* file đang bị giữ: bỏ qua */ }
    }
}
