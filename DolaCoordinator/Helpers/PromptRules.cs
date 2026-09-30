using System.Collections.Generic;

namespace DolaCoordinator.Helpers;

/// <summary>Một quy tắc đặt prompt lấy từ README của repo (tiêu đề mục, nội dung nguyên văn, nguồn = file trong repo).</summary>
public sealed record PromptRule(string Title, string Detail, string Source)
{
    public string AsText() => string.IsNullOrWhiteSpace(Title) ? Detail : $"{Title}\n{Detail}";
}

/// <summary>Phần do app tự viết (cách app xử lý prompt), tách riêng với quy tắc lấy từ repo.</summary>
public static class PromptRules
{
    /// <summary>Repo mặc định + các repo đã biết; chọn repo nào thì hiện quy tắc của repo đó.</summary>
    public static readonly string[] KnownRepos =
    {
        "coll3879xx-cyber/dola-render-gateway",
        "Roins-hub/dola-pool",
    };

    public const string DefaultRepo = "coll3879xx-cyber/dola-render-gateway";
    public const string DefaultBranch = "main";

    /// <summary>File tài liệu của repo được đọc để tìm quy tắc (file nào repo không có thì bỏ qua).</summary>
    public static readonly string[] Docs = { "README.md", "extensions/dola30/README.md", "API.md" };

    public static readonly IReadOnlyList<PromptRule> AppNotes = new[]
    {
        new PromptRule("Một ô = một prompt",
            "Dán cả kịch bản nhiều dòng vào MỘT ô: app gõ từng dòng bằng Shift+Enter nên không bị gửi sớm. Muốn nhiều video thì tạo nhiều prompt.", "App"),
        new PromptRule("Bỏ chữ thời lượng khi gửi",
            "Các chỗ như \"30s\", \"15 giây\", \"0-3s\", \"00:00 - 00:03\", \"Giây 0 đến 3\" trong nội dung bị gỡ tự động khi gửi (gateway: DOLA_STRIP_DURATION_WORDS=0 để tắt). Thời lượng chọn ở ô Thời lượng.", "App"),
        new PromptRule("30 giây dùng Seedance 2.5",
            "Chọn 30 giây mà model là 2.0 thì app tự chuyển sang Seedance 2.5 và ghi vào log.", "App"),
    };

    public const string Template =
        "Cảnh 1: <mô tả cảnh mở đầu: nhân vật, bối cảnh, ánh sáng, góc máy>\n" +
        "Cảnh 2: <cảnh tiếp theo, giữ nguyên nhân vật>\n" +
        "Cảnh 3: <sản phẩm / hành động chính>\n" +
        "Cảnh 4: <cận cảnh chi tiết>\n" +
        "Cảnh 5: <kết quả / cảm xúc>\n" +
        "Cảnh 6: <khung kết: logo, lời kêu gọi>\n" +
        "Phong cách: điện ảnh, chân thực, ánh sáng mềm.";
}
