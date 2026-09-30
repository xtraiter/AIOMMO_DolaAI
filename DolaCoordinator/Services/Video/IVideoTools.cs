using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DolaCoordinator.Services.Video;

/// <summary>Công cụ xử lý video (ffmpeg): lấy khung hình cuối của một video và ghép nhiều video thành một.</summary>
public interface IVideoTools
{
    /// <summary>Đường dẫn ffmpeg.exe (thư mục tools cạnh app, hoặc trong PATH); null nếu không tìm thấy.</summary>
    string? FfmpegPath { get; }

    bool IsAvailable { get; }

    /// <summary>Lưu khung hình cuối cùng của video thành ảnh PNG.</summary>
    Task<(bool Ok, string? Error)> ExtractLastFrameAsync(string videoPath, string outImagePath, CancellationToken ct = default);

    /// <summary>
    /// Ghép các video theo đúng thứ tự thành một file MP4. Thử ghép nguyên bản (nhanh, không giảm chất lượng) trước;
    /// nếu các video khác thông số thì mã hóa lại.
    /// </summary>
    Task<(bool Ok, string? Error)> MergeAsync(IReadOnlyList<string> videoPaths, string outPath, CancellationToken ct = default);
}
