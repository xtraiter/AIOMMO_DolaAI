using System.Threading;
using System.Threading.Tasks;

namespace DolaCoordinator.Services.Gateway;

/// <summary>
/// Chạy ngầm dola-render-gateway khi app cần đến nó (không hiện cửa sổ console) và tắt cùng app.
/// Nếu gateway đã chạy sẵn (tự bật bằng `python gateway_main.py serve` hoặc ở máy khác) thì dùng luôn, không đụng tới.
/// </summary>
public interface IGatewayHost
{
    /// <summary>
    /// Bảo đảm gateway đang trả lời /health: chưa chạy thì bật ngầm rồi chờ sẵn sàng.
    /// Chỉ tự bật khi GatewayUrl trỏ về máy này (127.0.0.1 / localhost); địa chỉ từ xa thì chỉ kiểm tra.
    /// </summary>
    Task<(bool Ok, string? Error)> EnsureRunningAsync(CancellationToken ct = default);

    /// <summary>
    /// Bản gateway đóng gói (exe): tải Chromium cho patchright một lần trên mỗi máy (lần đầu có thể mất vài phút).
    /// Bản chạy bằng Python thì bỏ qua (bạn tự chạy `patchright install chromium`).
    /// </summary>
    Task<(bool Ok, string? Error)> EnsureBrowserAsync(CancellationToken ct = default);

    /// <summary>Đường dẫn log của gateway do app bật (ghi đè mỗi lần bật).</summary>
    string LogPath { get; }

    /// <summary>Tắt gateway do chính app bật (cả cây tiến trình, kể cả Chromium). Không tắt gateway bật từ bên ngoài.</summary>
    void Stop();
}
