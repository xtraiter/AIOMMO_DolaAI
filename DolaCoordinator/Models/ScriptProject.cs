using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace DolaCoordinator.Models;

public enum ScriptPartStatus
{
    NotStarted,
    Waiting,   // đã vào hàng đợi
    Running,   // đang tạo / tải video
    Done,
    Failed,
}

/// <summary>Một phần của kịch bản lớn = một video. Phần sau lấy khung hình cuối của video phần trước làm ảnh mở đầu.</summary>
public class ScriptPart : ObservableObject
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Nội dung kịch bản của phần này (giữ nguyên xuống dòng).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Tác vụ trong hàng đợi đang/đã làm phần này.</summary>
    public string? TaskId { get; set; }

    public ScriptPartStatus Status { get; set; } = ScriptPartStatus.NotStarted;

    public string? VideoPath { get; set; }

    /// <summary>Khung hình cuối của video (PNG) — ảnh mở đầu của phần kế tiếp.</summary>
    public string? LastFramePath { get; set; }

    public double? ActualSeconds { get; set; }

    public string? Error { get; set; }

    public string? Note { get; set; }

    // ---- hiển thị (không lưu) ----

    [BsonIgnore] public int Number { get; set; }

    [BsonIgnore] public string StageText { get; set; } = string.Empty;

    [BsonIgnore]
    public string Preview
    {
        get
        {
            var one = string.Join(" ⏎ ", Text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0));
            return one.Length > 260 ? one[..257] + "…" : one;
        }
    }

    [BsonIgnore]
    public string StatusText => Status switch
    {
        ScriptPartStatus.NotStarted => "Chưa chạy",
        ScriptPartStatus.Waiting => "Chờ trong hàng đợi",
        ScriptPartStatus.Running => string.IsNullOrEmpty(StageText) ? "Đang tạo video" : StageText,
        ScriptPartStatus.Done => "Xong",
        ScriptPartStatus.Failed => "Lỗi",
        _ => string.Empty,
    };

    [BsonIgnore] public bool HasVideo => !string.IsNullOrEmpty(VideoPath) && File.Exists(VideoPath);

    [BsonIgnore] public bool HasLastFrame => !string.IsNullOrEmpty(LastFramePath) && File.Exists(LastFramePath);

    [BsonIgnore]
    public string VideoText => !HasVideo ? "—" : $"{Path.GetFileName(VideoPath)}" + (ActualSeconds is double d ? $" · {d:0.#}s" : string.Empty);

    [BsonIgnore] public bool CanRun => Status is ScriptPartStatus.NotStarted or ScriptPartStatus.Failed or ScriptPartStatus.Done;

    public void NotifyChanged() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// Kịch bản lớn: một kịch bản dài tách thành nhiều phần (mỗi phần một video), cùng nhân vật + bối cảnh, chạy nối tiếp
/// (khung hình cuối của video trước làm ảnh mở đầu video sau) rồi ghép thành một video.
/// </summary>
public class ScriptProject : ObservableObject
{
    public const string DefaultContinueHeader =
        "Đây là phần {n}/{total} của một video dài và nối tiếp NGAY sau phần trước. Ảnh tham chiếu đầu tiên là khung hình cuối của phần trước: " +
        "bắt đầu đúng từ khung hình đó, giữ nguyên nhân vật, trang phục, bối cảnh và ánh sáng, không chuyển cảnh đột ngột.";

    /// <summary>Dùng khi phần trước chưa có khung hình cuối (chưa cài ffmpeg / phần trước chưa xong).</summary>
    public const string ContinueHeaderWithoutFrame =
        "Đây là phần {n}/{total} của một video dài và nối tiếp phần trước: giữ nguyên nhân vật, trang phục, bối cảnh và ánh sáng.";

    public const string DefaultContinueFooter =
        "Đây là phần {n}/{total}, phía sau còn phần tiếp nối. Hãy kết thúc phần này bằng một khung hình rõ nét và ổn định: " +
        "nhân vật và bối cảnh nằm trọn trong khung, không bị mờ, không chuyển cảnh, không hiện chữ kết thúc hay logo " +
        "— để khung hình cuối dùng làm khung mở đầu của phần tiếp theo.";

    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;

    public string Model { get; set; } = "seedance-2.5";

    public string Ratio { get; set; } = "16:9";

    /// <summary>Thời lượng MỖI phần (5, 10 hoặc 30 giây).</summary>
    public int Duration { get; set; } = 10;

    public List<PromptCharacter> Characters { get; set; } = new();

    public string SceneText { get; set; } = string.Empty;

    public List<string> SceneImages { get; set; } = new();

    public List<ScriptPart> Parts { get; set; } = new();

    /// <summary>Lấy khung hình cuối của video trước làm ảnh mở đầu video sau.</summary>
    public bool UseLastFrame { get; set; } = true;

    /// <summary>Câu dặn gắn vào đầu các phần từ phần 2 (dùng {n} và {total}).</summary>
    public string ContinueHeader { get; set; } = DefaultContinueHeader;

    /// <summary>Câu dặn gắn vào cuối các phần trừ phần cuối: báo còn phần sau, kết thúc bằng khung hình chuẩn.</summary>
    public string ContinueFooter { get; set; } = DefaultContinueFooter;

    /// <summary>Tự ghép thành một video khi mọi phần đã xong.</summary>
    public bool AutoMerge { get; set; }

    public string? MergedPath { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [BsonIgnore]
    public string Summary
    {
        get
        {
            var done = Parts.Count(p => p.Status == ScriptPartStatus.Done);
            return $"{Title} ({done}/{Parts.Count} phần xong)";
        }
    }

    public void NotifyChanged() => OnPropertyChanged(string.Empty);
}
