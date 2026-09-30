using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Profiles;

/// <summary>Kết quả đọc trạng thái cửa sổ profile đang mở (file .profile_status.json do open_profile.py ghi).</summary>
public sealed record ProfileProbe(bool Running, bool LoggedIn, string? CookieHeader, string? NeedHuman = null)
{
    public static readonly ProfileProbe Closed = new(false, false, null);
}

/// <summary>
/// Quản lý profile = tài khoản của dola-render-gateway (thư mục accounts/&lt;tên&gt;, Chromium của patchright).
/// </summary>
public interface IAccountProfileService
{
    /// <summary>Thư mục dola-render-gateway; null nếu không tìm thấy.</summary>
    string? GatewayDir { get; }

    /// <summary>Thư mục accounts của gateway (nơi chứa mọi profile).</summary>
    string? AccountsDir { get; }

    /// <summary>Mô tả vấn đề môi trường (thiếu gateway...) hoặc null nếu ổn.</summary>
    string? EnvironmentProblem { get; }

    /// <summary>Gán FolderPath = accounts/&lt;Name&gt;.</summary>
    void ResolvePaths(AccountProfile profile);

    /// <summary>Tạo tài khoản mới: thư mục accounts/&lt;name&gt; + bản ghi profile. Tên phải hợp lệ theo gateway.</summary>
    AccountProfile CreateProfile(string name, string? notes);

    /// <summary>Thông tin đăng nhập đã ghi nhớ của tài khoản (giải mã DPAPI). Method = Manual nếu chưa lưu.</summary>
    LoginOptions GetSavedLogin(AccountProfile profile);

    /// <summary>Ghi nhớ (hoặc xóa, khi options.Method = Manual) thông tin đăng nhập và lưu tài khoản vào DB.</summary>
    void SaveLogin(AccountProfile profile, LoginOptions options);

    /// <summary>Tạo profile cho một DolaSession đã có (dữ liệu cũ chưa có profile). Tên được chuẩn hóa theo gateway.</summary>
    AccountProfile AdoptSession(DolaSession session);

    /// <summary>Tài khoản có sẵn trong accounts/ của gateway nhưng chưa có profile trong app → tạo profile (và phiên từ cookie.txt).</summary>
    List<AccountProfile> DiscoverGatewayAccounts(IEnumerable<string> knownNames);

    /// <summary>
    /// Nhập cookie cho tài khoản: ghi cookie.txt (đúng như add_account_cookie.py) và tạo/cập nhật DolaSession.
    /// Lần mở profile tiếp theo gateway tự nạp cookie này vào Chromium.
    /// </summary>
    DolaSession AttachCookie(AccountProfile profile, string cookieHeader);

    /// <summary>
    /// Mở cửa sổ Chromium của profile (py open_profile.py) và chờ sẵn sàng. Với options tự động, script tự điền
    /// đăng nhập Google/Facebook (captcha/2FA để bạn giải) và có thể tự đóng khi xong.
    /// </summary>
    Task LaunchAsync(AccountProfile profile, LoginOptions? options = null, CancellationToken ct = default);

    /// <summary>Lưu phiên rồi đóng cửa sổ profile.</summary>
    Task CloseAsync(AccountProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Nhặt phiên từ cookie.txt sau khi profile đóng: nếu cookie.txt được ghi trong lần chạy vừa rồi thì đã có đăng nhập.
    /// Cần thiết khi script tự đóng ngay sau khi đăng nhập, trước khi app kịp đọc trạng thái đang mở.
    /// </summary>
    (bool LoggedInThisRun, bool SessionChanged) CaptureFromCookieFile(AccountProfile profile);

    /// <summary>Profile có đang mở không, và đã đăng nhập chưa.</summary>
    ProfileProbe Probe(AccountProfile profile);

    /// <summary>Tài khoản (theo tên) đang được mở trong một cửa sổ profile — gateway không được dùng lúc này.</summary>
    bool IsAccountOpen(string accountName);

    /// <summary>
    /// Lưu cookie đăng nhập vào DolaSession của profile. Trả về true nếu phiên vừa được tạo hoặc đổi token
    /// (cần kiểm tra lại với gateway).
    /// </summary>
    bool CaptureSession(AccountProfile profile, string cookieHeader);

    void OpenFolder(AccountProfile profile);
    void OpenAccountsDir();

    /// <summary>Xóa hẳn tài khoản: thư mục accounts/&lt;tên&gt; (qua gateway nếu đang chạy), phiên Dola và bản ghi profile.</summary>
    Task DeleteAsync(AccountProfile profile, CancellationToken ct = default);
}
