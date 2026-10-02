namespace DolaCoordinator.Helpers;

/// <summary>
/// Một đoạn "prompt quy tắc" duy nhất để dán cho AI (ChatGPT, Claude…) khi nhờ nó viết prompt video cho Dola.
/// Soạn theo repo gateway đang dùng (github.com/Roins-hub/dola-pool): thời lượng, tỷ lệ, model, ảnh là các ô chọn riêng;
/// file "必读" của extension Dola30 dặn video 30 giây ở cuộc hội thoại mới đừng ghi "XX秒", "X~X秒" trong nội dung prompt.
/// </summary>
public static class PromptRules
{
    public const string SourceRepo = "Roins-hub/dola-pool";

    public const string RulesPrompt =
        "Bạn là chuyên gia viết prompt tạo video AI cho Dola (Seedance). Hãy viết prompt video theo đúng các quy tắc sau:\n" +
        "\n" +
        "1. TUYỆT ĐỐI không ghi thời lượng trong nội dung prompt: không viết \"30s\", \"30 seconds\", \"15 giây\", \"30秒\", \"5~10秒\", \"0-3s\", \"00:00 - 00:03\", \"Giây 0 đến 3\"… " +
        "(với video 30 giây ở cuộc hội thoại mới, những cụm như \"XX秒\", \"X~X秒\" có thể làm Dola báo không tạo được). " +
        "Thời lượng, tỷ lệ khung hình và model do tôi chọn ở ô chọn riêng của phần mềm, không nằm trong chữ. " +
        "Độ dài hợp lệ: Seedance 2.5 có 5 / 10 / 30 giây, Seedance 2.0 có 5 / 10 / 15 giây; độ dài khác bị từ chối.\n" +
        "2. Viết thẳng nội dung video, không đặt câu hỏi, không hỏi lại tôi, không giải thích thêm.\n" +
        "3. Toàn bộ kết quả nằm trong MỘT khối văn bản duy nhất (xuống dòng giữa các cảnh) để tôi sao chép dán thẳng vào ô prompt.\n" +
        "\n" +
        "Ý tưởng video của tôi là: ";
}
