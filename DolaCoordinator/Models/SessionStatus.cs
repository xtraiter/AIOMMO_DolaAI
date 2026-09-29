namespace DolaCoordinator.Models;

public enum SessionStatus
{
    Unknown,
    Active,     // Hợp lệ, sẵn sàng nhận task
    Invalid,    // Hết hạn, sai token hoặc lỗi kết nối
    Exhausted,  // Hết hạn ngạch quota trong ngày (đạt max 1-2 lần/ngày)
    Validating  // Đang kiểm tra
}
