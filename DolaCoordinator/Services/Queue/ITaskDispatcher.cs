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

    /// <summary>Tạm dừng: không nhận tác vụ mới, các tác vụ đang chạy vẫn chạy nốt.</summary>
    bool IsPaused { get; }
    void Pause();
    void Resume();

    /// <summary>Đặt độ ưu tiên (số lớn chạy trước). Chỉ có tác dụng với tác vụ chưa bắt đầu.</summary>
    void SetPriority(string taskId, int priority);
    int ActiveWorkersCount { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    void EnqueueTask(RenderTask task);
    void EnqueueTasks(IEnumerable<RenderTask> tasks);
    Task RetryTaskAsync(string taskId);
    void CancelTask(string taskId);
}
