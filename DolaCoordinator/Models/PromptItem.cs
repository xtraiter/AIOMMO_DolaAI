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

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastQueuedAt { get; set; }

    /// <summary>Đã thêm vào hàng đợi bao nhiêu video từ prompt này.</summary>
    public int QueuedCount { get; set; }

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
    public string ReferenceSummary => ReferenceLocalPaths.Count == 0 ? "—" : $"{ReferenceLocalPaths.Count} ảnh";

    /// <summary>Báo giao diện đọc lại mọi thuộc tính sau khi sửa.</summary>
    public void NotifyChanged() => OnPropertyChanged(string.Empty);
}
