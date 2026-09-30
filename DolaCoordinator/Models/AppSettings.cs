using System;
using System.IO;

namespace DolaCoordinator.Models;

/// <summary>Cách chọn tài khoản cho từng video.</summary>
public enum AccountStrategy
{
    /// <summary>Chia đều: chạy hết các tài khoản (mỗi tài khoản một video) rồi mới sang lượt 2.</summary>
    RoundRobin = 0,

    /// <summary>Dùng hết từng tài khoản: dồn video vào tài khoản đầu cho tới khi hết lượt/lỗi mới sang tài khoản kế.</summary>
    Sequential = 1,
}

public class AppSettings
{
    public int Id { get; set; } = 1;

    /// <summary>
    /// Địa chỉ dola-render-gateway (ví dụ: http://127.0.0.1:8000)
    /// </summary>
    public string GatewayUrl { get; set; } = "http://127.0.0.1:8000";

    /// <summary>
    /// Admin key cấu hình trong gateway (nếu có)
    /// </summary>
    public string? AdminKey { get; set; }

    /// <summary>
    /// API key nếu gateway bật xác thực DOLA_API_KEYS
    /// </summary>
    public string? ClientApiKey { get; set; }

    /// <summary>
    /// Giới hạn quota mặc định cho mỗi session / ngày (1-2 tác vụ)
    /// </summary>
    public int DefaultDailyQuota { get; set; } = 5;

    /// <summary>Đã đổi hạn ngạch mặc định cũ (2) sang 5 một lần cho bản cài cũ (xem LiteDbDatabaseService.GetSettings).</summary>
    public bool QuotaDefaultMigrated { get; set; }

    /// <summary>
    /// Thư mục lưu video tải về. Mặc định là thư mục Videos của Windows; chọn lại ngay trên trang Vận hành.
    /// </summary>
    public string DownloadDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// Số luồng tối đa: số video chạy cùng lúc. Chỉnh ở trang Vận hành.
    /// </summary>
    public int ConcurrencyLimit { get; set; } = 2;

    /// <summary>Cách chọn tài khoản cho từng video (mặc định chia đều theo lượt).</summary>
    public AccountStrategy AccountStrategy { get; set; } = AccountStrategy.RoundRobin;

    /// <summary>Tài khoản vừa lỗi được bỏ qua ở các video khác trong một lúc (thay vì lần nào cũng thử lại).</summary>
    public bool SkipFailedAccounts { get; set; } = true;

    /// <summary>
    /// Câu app tự gõ vào khung chat khi Dola hỏi lại một câu KHÔNG phải về thời lượng ("bạn có ảnh khuôn mặt không, A hay B?").
    /// Câu hỏi về thời lượng thì gateway tự trả lời bằng đúng thời lượng đã chọn ("30秒", "B. 10秒"). Tuyệt đối không bảo Dola
    /// "làm theo đề xuất của bạn": nó sẽ nén kịch bản hoặc tách thành nhiều video ngắn.
    /// Không chứa dấu hỏi để không bị nhận nhầm là một câu hỏi nữa.
    /// </summary>
    public const string DefaultAskBackReply =
        "Có, tiếp tục ngay. Nếu bạn hỏi về ảnh khuôn mặt thì chọn B, tự tạo nhân vật và giữ nhân vật đó ở mọi cảnh. " +
        "Chỉ tạo MỘT video duy nhất đúng thời lượng đã chọn, không chia thành nhiều video, không rút ngắn. " +
        "Không cần hỏi lại, hãy tạo video ngay.";

    /// <summary>Nội dung mặc định của các bản trước (bảo Dola dùng thời lượng gần nhất và nén kịch bản → video bị ngắn / tách đôi). Gặp lại thì tự thay bằng bản mới.</summary>
    public const string LegacyAskBackReply =
        "Có, tiếp tục ngay với phương án bạn đề xuất: dùng thời lượng gần nhất được hỗ trợ và nén kịch bản cho vừa. " +
        "Nếu bạn hỏi về ảnh khuôn mặt thì chọn B, tự tạo nhân vật và giữ nhân vật đó ở mọi cảnh. " +
        "Không cần hỏi lại, hãy tạo video ngay. (Yes, go ahead now with your suggestion, do not ask again.)";

    /// <summary>Khi Dola hỏi lại, tự trả lời để nó tạo video (tối đa 3 lần mỗi video).</summary>
    public bool AutoAnswerAskBack { get; set; } = true;

    /// <summary>Nội dung tự trả lời (có thể sửa). Để trống = dùng mặc định.</summary>
    public string AskBackReply { get; set; } = DefaultAskBackReply;

    /// <summary>Ẩn cửa sổ Chromium khi chạy vận hành (cửa sổ nằm ngoài màn hình). Đăng nhập tài khoản luôn hiện.</summary>
    public bool HideRenderWindow { get; set; }

    /// <summary>
    /// Chu kỳ polling kiểm tra trạng thái video (giây)
    /// </summary>
    public int PollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Thời lượng video mặc định (30 giây)
    /// </summary>
    public int DefaultDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Tỉ lệ khung hình mặc định ("9:16" hoặc "16:9")
    /// </summary>
    public string DefaultRatio { get; set; } = "9:16";

    /// <summary>
    /// Bật/Tắt Windows Toast Notification khi hoàn tất hàng đợi
    /// </summary>
    public bool EnableToastNotification { get; set; } = true;

    /// <summary>
    /// Tự động retry khi gặp lỗi tạm thời (mạng, gateway busy)
    /// </summary>
    public bool AutoRetryOnFailure { get; set; } = true;

    /// <summary>
    /// Thư mục dola-render-gateway (chứa server.py; các profile nằm ở accounts/). Để trống = tự dò từ vị trí phần mềm.
    /// </summary>
    public string? GatewayDir { get; set; }

    /// <summary>
    /// Lệnh Python để chạy script gateway (mở profile...). Mặc định "py -3" như các file .bat của dự án.
    /// </summary>
    public string PythonCommand { get; set; } = "py -3";

    /// <summary>
    /// Tự bật dola-render-gateway khi mở app.
    /// </summary>
    public bool AutoStartGateway { get; set; } = true;

    /// <summary>
    /// URL HTTPS của file version.json để kiểm tra cập nhật từ xa. Để trống = tắt tự cập nhật (mặc định).
    /// Gói cập nhật phải nằm cùng host và có sha256Checksum khớp mới được cài.
    /// </summary>
    public string UpdateCheckUrl { get; set; } = string.Empty;
}
