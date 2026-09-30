namespace DolaCoordinator.Helpers;

/// <summary>
/// Một đoạn "prompt quy tắc" duy nhất để dán cho AI (ChatGPT, Claude…) khi nhờ nó viết prompt video cho Dola.
/// Soạn theo repo gateway đang dùng (github.com/coll3879xx-cyber/dola-render-gateway): thời lượng, tỷ lệ, model, ảnh là
/// các ô chọn riêng; README của extension Dola30 dặn không ghi chữ thời lượng trong nội dung prompt.
/// </summary>
public static class PromptRules
{
    public const string SourceRepo = "coll3879xx-cyber/dola-render-gateway";

    public const string RulesPrompt =
        "Bạn là chuyên gia viết prompt tạo video AI cho Dola (Seedance). Hãy viết prompt video theo đúng các quy tắc sau:\n" +
        "\n" +
        "1. TUYỆT ĐỐI không ghi thời lượng trong nội dung prompt: không viết \"30s\", \"30 seconds\", \"15 giây\", \"0-3s\", \"00:00 - 00:03\", \"Giây 0 đến 3\"… " +
        "Thời lượng, tỷ lệ khung hình và model do tôi chọn ở ô chọn riêng của phần mềm, không nằm trong chữ.\n" +
        "2. Chia cảnh theo thứ tự \"Cảnh 1:\", \"Cảnh 2:\", \"Cảnh 3:\"… mỗi cảnh một dòng, không kèm mốc thời gian hay số giây.\n" +
        "3. Mỗi cảnh mô tả rõ: góc máy/cỡ cảnh, hành động, bối cảnh, ánh sáng, phong cách hình ảnh; có thể thêm lệnh máy quay ngắn như --motion 3, --zoom in, --pan right.\n" +
        "4. Giữ nhân vật và sản phẩm đồng nhất xuyên suốt: mô tả nhân vật y hệt ở mọi cảnh (tuổi, ngoại hình, trang phục) để video không đổi mặt.\n" +
        "5. Chữ hiện trên màn hình và lời thoại (nếu có) ghi trong dấu ngoặc kép, ngắn gọn, mỗi cảnh một câu.\n" +
        "6. Viết thẳng nội dung video, không đặt câu hỏi, không hỏi lại tôi, không giải thích thêm.\n" +
        "7. Toàn bộ kết quả nằm trong MỘT khối văn bản duy nhất (xuống dòng giữa các cảnh) để tôi sao chép dán thẳng vào ô prompt.\n" +
        "\n" +
        "Ý tưởng video của tôi là: ";
}
