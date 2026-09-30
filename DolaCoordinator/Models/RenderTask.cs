using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>Tên prompt trong thư viện (để dễ nhận ra trong hàng đợi); null nếu tác vụ tạo trực tiếp.</summary>
    public string? PromptTitle { get; set; }

    /// <summary>Độ ưu tiên: số lớn chạy trước (0 = thường, 1 = cao, 2 = khẩn, -1 = thấp). Cùng mức thì theo thứ tự tạo.</summary>
    public int Priority { get; set; }

    public string Ratio { get; set; } = "9:16"; // 9:16 (dọc), 16:9 (ngang), 1:1

    public int Duration { get; set; } = 30; // 5, 10, 30s theo ô "長さ" của Dola (mặc định 30s)

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

    /// <summary>Dòng đầu của thông báo lỗi (thông báo đầy đủ có thể nhiều dòng, xem ở tooltip).</summary>
    [BsonIgnore]
    public string ErrorShort
    {
        get
        {
            var first = (ErrorMessage ?? string.Empty).Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return first.Length > 140 ? first[..137] + "..." : first;
        }
    }

    [BsonIgnore]
    public string ModelLabel => DolaCoordinator.Helpers.PromptFileParser.ModelLabel(Model);

    [BsonIgnore]
    public string PriorityText => Priority switch
    {
        >= 2 => "Khẩn",
        1 => "Cao",
        0 => "Thường",
        _ => "Thấp",
    };

    [BsonIgnore]
    public string NameText => string.IsNullOrWhiteSpace(PromptTitle) ? DisplayPrompt : PromptTitle;

    [BsonIgnore]
    public string DisplayPrompt
    {
        get
        {
            // Prompt nhiều dòng: gộp thành một dòng để hiện trong bảng / nhật ký
            var one = string.Join(" ⏎ ", Prompt.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0));
            return one.Length > 60 ? one[..57] + "..." : one;
        }
    }

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isSelected;

    /// <summary>Giai đoạn hiện tại của tác vụ (kiểm tra tài khoản, mở chat mới, tạo video, tải về...).</summary>
    [ObservableProperty]
    [property: BsonIgnore]
    private string? _stageText;

    /// <summary>Báo giao diện đọc lại mọi thuộc tính (dispatcher sửa trực tiếp trên cùng một đối tượng).</summary>
    public void NotifyChanged() => OnPropertyChanged(string.Empty);

    [BsonIgnore]
    public string ReferenceSummary => ReferenceLocalPaths.Count + ReferenceImages.Count == 0
        ? "—"
        : $"{ReferenceLocalPaths.Count + ReferenceImages.Count} ảnh";
}
