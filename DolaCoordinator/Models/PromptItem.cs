using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace DolaCoordinator.Models;

/// <summary>
/// Một prompt trong thư viện: có tên, nội dung NHIỀU DÒNG (giữ nguyên xuống dòng, bảng, tab), thiết lập mặc định
/// (tỷ lệ, thời lượng, ảnh tham chiếu). Khi "Thêm vào hàng đợi" mỗi prompt được tạo thành một hoặc nhiều tác vụ.
/// </summary>
public partial class PromptItem : ObservableObject
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    /// <summary>Nội dung gửi cho Dola, giữ nguyên mọi xuống dòng (có thể là bảng phân cảnh, danh sách…).</summary>
    public string Text { get; set; } = string.Empty;

    public string Ratio { get; set; } = "9:16";

    public int Duration { get; set; } = 30;

    /// <summary>Model Dola/Seedance: seedance-2.0 hoặc seedance-2.5.</summary>
    public string Model { get; set; } = "seedance-2.0";

    /// <summary>Ảnh tham chiếu mặc định (đường dẫn trên máy này).</summary>
    public List<string> ReferenceLocalPaths { get; set; } = new();

    /// <summary>Nhân vật của prompt (mỗi nhân vật có mô tả + ảnh tham chiếu riêng). Có thể nhiều nhân vật.</summary>
    public List<PromptCharacter> Characters { get; set; } = new();

    /// <summary>Mô tả bối cảnh và ảnh tham chiếu bối cảnh.</summary>
    public string SceneText { get; set; } = string.Empty;

    public List<string> SceneImages { get; set; } = new();

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastQueuedAt { get; set; }

    /// <summary>Đã thêm vào hàng đợi bao nhiêu video từ prompt này.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Số video của prompt này đang chờ / đang chạy.</summary>
    public int ActiveCount { get; set; }

    /// <summary>Số video của prompt này đã làm xong.</summary>
    public int DoneCount { get; set; }

    /// <summary>Số video của prompt này bị lỗi.</summary>
    public int FailedCount { get; set; }

    /// <summary>Trạng thái gộp: none (chưa làm) | active (đang làm) | done (đã xong) | partial (xong một phần, có lỗi) | failed (lỗi).</summary>
    [BsonIgnore]
    public string StatusKey => ActiveCount > 0 ? "active"
        : FailedCount > 0 ? (DoneCount > 0 ? "partial" : "failed")
        : DoneCount > 0 ? "done"
        : "none";

    [BsonIgnore]
    public string StatusText => StatusKey switch
    {
        "active" => "Đang làm",
        "done" => "Đã xong",
        "partial" => "Xong một phần",
        "failed" => "Lỗi",
        _ => "Chưa làm",
    };

    [BsonIgnore]
    public string StatusDetail
    {
        get
        {
            var parts = new List<string>();
            if (ActiveCount > 0) parts.Add($"{ActiveCount} đang làm");
            if (DoneCount > 0) parts.Add($"{DoneCount} xong");
            if (FailedCount > 0) parts.Add($"{FailedCount} lỗi");
            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isSelected;

    /// <summary>Nội dung gọn trên một dòng để hiện trong bảng.</summary>
    [BsonIgnore]
    public string Preview
    {
        get
        {
            var oneLine = string.Join(" ⏎ ", Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0));
            return oneLine.Length > 220 ? oneLine[..217] + "…" : oneLine;
        }
    }

    [BsonIgnore]
    public int LineCount => string.IsNullOrEmpty(Text) ? 0 : Text.Split('\n').Length;

    [BsonIgnore]
    public string ModelLabel => DolaCoordinator.Helpers.PromptFileParser.ModelLabel(Model);

    [BsonIgnore]
    public string ReferenceSummary
    {
        get
        {
            var n = DolaCoordinator.Helpers.PromptComposer.CountImages(ReferenceLocalPaths, Characters, SceneImages);
            return n == 0 ? "—" : $"{n} ảnh";
        }
    }

    [BsonIgnore]
    public string CastSummary
    {
        get
        {
            var parts = new List<string>();
            var named = Characters.Count(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Description) || c.Images.Count > 0);
            if (named > 0) parts.Add($"{named} nhân vật");
            if (!string.IsNullOrWhiteSpace(SceneText) || SceneImages.Count > 0) parts.Add("bối cảnh");
            return parts.Count == 0 ? "—" : string.Join(" + ", parts);
        }
    }

    /// <summary>Báo giao diện đọc lại mọi thuộc tính sau khi sửa.</summary>
    public void NotifyChanged() => OnPropertyChanged(string.Empty);
}
