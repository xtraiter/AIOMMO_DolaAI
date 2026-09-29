using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Queue;

public interface ITaskDispatcher
{
    event Action<string>? LogReceived;
    event Action<RenderTask>? TaskUpdated;
    event Action? AllTasksCompleted;

    bool IsRunning { get; }
    int ActiveWorkersCount { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    void EnqueueTask(RenderTask task);
    void EnqueueTasks(IEnumerable<RenderTask> tasks);
    Task RetryTaskAsync(string taskId);
    void CancelTask(string taskId);
}
