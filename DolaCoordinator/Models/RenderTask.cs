using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace DolaCoordinator.Models;

public enum RenderTaskStatus
{
    Pending,
    Queued,
    Processing,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

public partial class RenderTask : ObservableObject
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string? GatewayTaskId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string Ratio { get; set; } = "9:16"; // 9:16 (dọc), 16:9 (ngang), 1:1

    public int Duration { get; set; } = 30; // 10, 15, 30s (mặc định 30s theo yêu cầu)

    public string Model { get; set; } = "seedance-2.0";

    /// <summary>URL ảnh tham chiếu công khai (gateway tải về).</summary>
    public List<string> ReferenceImages { get; set; } = new();

    /// <summary>Ảnh tham chiếu chọn từ máy này (đường dẫn tuyệt đối).</summary>
    public List<string> ReferenceLocalPaths { get; set; } = new();

    public RenderTaskStatus Status { get; set; } = RenderTaskStatus.Pending;

    public int RetryCount { get; set; } = 0;

    public int MaxRetries { get; set; } = 3;

    public string? AssignedSessionId { get; set; }

    public string? AssignedSessionName { get; set; }

    public string? VideoUrl { get; set; }

    public string? LocalFilePath { get; set; }

    public long FileSizeBytes { get; set; } = 0;

    public int ProgressPercent { get; set; } = 0;

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    [BsonIgnore]
    public string DisplayPrompt => Prompt.Length > 60 ? Prompt[..57] + "..." : Prompt;

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isSelected;

    /// <summary>Giai đoạn hiện tại của tác vụ (kiểm tra tài khoản, mở chat mới, tạo video, tải về...).</summary>
    [ObservableProperty]
    [property: BsonIgnore]
    private string? _stageText;

    [BsonIgnore]
    public string ReferenceSummary => ReferenceLocalPaths.Count + ReferenceImages.Count == 0
        ? "—"
        : $"{ReferenceLocalPaths.Count + ReferenceImages.Count} ảnh";
}
