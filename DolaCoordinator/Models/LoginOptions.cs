using CommunityToolkit.Mvvm.ComponentModel;

namespace DolaCoordinator.Models;

public enum LoginMethod
{
    Manual,          // tự đăng nhập trong cửa sổ trình duyệt
    Google,          // tự điền Google (email + mật khẩu + mã 2FA nếu có khóa TOTP)
    Facebook,        // tự điền Facebook (email/SĐT + mật khẩu + mã 2FA nếu có khóa TOTP)
    FacebookCookie   // nạp cookie Facebook → xác nhận vào được Facebook → Dola "Continue with Facebook" → lưu phiên Dola
}

public enum AfterLogin
{
    Keep,       // giữ profile mở sau khi đăng nhập xong
    Close       // tự đóng profile khi Dola xác nhận đã đăng nhập
}

/// <summary>
/// Tùy chọn mở profile: đăng nhập tự động hay thủ công. Mật khẩu, khóa 2FA và cookie Facebook được đưa cho script
/// qua stdin; chỉ khi bật Remember thì mới lưu lại (mã hóa DPAPI, xem AccountProfile.SavedSecret).
/// </summary>
public sealed partial class LoginOptions : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAutomatic))]
    private LoginMethod _method = LoginMethod.Manual;

    /// <summary>Email Google hoặc email/số điện thoại Facebook.</summary>
    [ObservableProperty]
    private string _email = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    /// <summary>Khóa bí mật TOTP (Base32) để tự điền mã 2FA; để trống thì 2FA do bạn nhập tay.</summary>
    [ObservableProperty]
    private string _totp = string.Empty;

    /// <summary>Chuỗi cookie Facebook (có c_user và/hoặc xs) cho phương thức FacebookCookie.</summary>
    [ObservableProperty]
    private string _cookie = string.Empty;

    [ObservableProperty]
    private AfterLogin _after = AfterLogin.Keep;

    /// <summary>Ghi nhớ thông tin đăng nhập (mã hóa DPAPI) để lần sau chỉ cần bấm "Đăng nhập tự động".</summary>
    [ObservableProperty]
    private bool _remember = true;

    public bool IsAutomatic => Method != LoginMethod.Manual;

    /// <summary>Giá trị --login của open_profile.py; null với đăng nhập thủ công.</summary>
    public string? ScriptMode => Method switch
    {
        LoginMethod.Google => "google",
        LoginMethod.Facebook => "facebook",
        LoginMethod.FacebookCookie => "facebook-cookie",
        _ => null,
    };

    /// <summary>Cookie có phải của Facebook không (c_user / xs), giống cách gateway nhận diện.</summary>
    public static bool LooksLikeFacebookCookie(string? cookie)
        => !string.IsNullOrWhiteSpace(cookie) && (cookie.Contains("c_user=") || cookie.Contains("xs="));

    /// <summary>Lỗi nhập liệu (tiếng Việt) hoặc null nếu hợp lệ.</summary>
    public string? Validate()
    {
        switch (Method)
        {
            case LoginMethod.Google:
            case LoginMethod.Facebook:
                if (string.IsNullOrWhiteSpace(Email)) return "Nhập email/tài khoản để đăng nhập tự động.";
                if (string.IsNullOrEmpty(Password)) return "Nhập mật khẩu để đăng nhập tự động.";
                return null;
            case LoginMethod.FacebookCookie:
                return LooksLikeFacebookCookie(Cookie) ? null : "Cookie Facebook phải chứa c_user=... hoặc xs=...";
            default:
                return null;
        }
    }
}
