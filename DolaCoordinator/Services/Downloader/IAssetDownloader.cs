using System;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Downloader;

public interface IAssetDownloader
{
    Task<string> DownloadVideoAsync(
        RenderTask task,
        string videoUrl,
        IProgress<int>? progress = null,
        CancellationToken ct = default);

    string GenerateFilename(string taskId, string prompt);
}
