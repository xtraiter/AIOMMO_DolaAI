using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Downloader;

public class AssetDownloader : IAssetDownloader
{
    private readonly IDolaGatewayClient _gatewayClient;
    private readonly IDatabaseService _databaseService;

    public AssetDownloader(
        IDolaGatewayClient gatewayClient,
        IDatabaseService databaseService)
    {
        _gatewayClient = gatewayClient;
        _databaseService = databaseService;
    }

    public string GenerateFilename(string taskId, string prompt)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var shortId = taskId.Length > 8 ? taskId[..8] : taskId;

        // Create a clean slug from prompt (max 30 chars)
        var slug = Regex.Replace(prompt, @"[^\w\s-]", "");
        slug = Regex.Replace(slug, @"[\s-]+", "_").Trim('_');
        if (slug.Length > 30) slug = slug[..30].TrimEnd('_');
        if (string.IsNullOrWhiteSpace(slug)) slug = "video";

        return $"{timestamp}_{shortId}_{slug}.mp4";
    }

    public async Task<string> DownloadVideoAsync(
        RenderTask task,
        string videoUrl,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var settings = _databaseService.GetSettings();
        var downloadDir = settings.DownloadDirectory;
        Directory.CreateDirectory(downloadDir);

        var filename = GenerateFilename(task.GatewayTaskId ?? task.Id, task.Prompt);
        var targetPath = Path.Combine(downloadDir, filename);

        task.Status = RenderTaskStatus.Downloading;
        _databaseService.UpsertTask(task);

        using var responseStream = await _gatewayClient.OpenVideoStreamAsync(videoUrl, ct);
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int bytesRead;
        long totalRead = 0;

        while ((bytesRead = await responseStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
            totalRead += bytesRead;
            // Report smooth download progress (from 80% to 100%)
            progress?.Report(Math.Min(99, 80 + (int)(totalRead % 19)));
        }

        progress?.Report(100);

        task.LocalFilePath = targetPath;
        task.FileSizeBytes = totalRead;
        task.Status = RenderTaskStatus.Completed;
        task.ProgressPercent = 100;
        task.FinishedAt = DateTime.UtcNow;
        _databaseService.UpsertTask(task);

        return targetPath;
    }
}
