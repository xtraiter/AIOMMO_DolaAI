using System;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Update;

public interface IUpdateService
{
    string CurrentVersionString { get; }
    Task<UpdateInfo?> CheckForUpdatesAsync(string? customUrl = null, CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(UpdateInfo updateInfo, IProgress<int>? progress = null, CancellationToken ct = default);
    void ApplyUpdateAndRestart(string downloadedPackagePath);
}
