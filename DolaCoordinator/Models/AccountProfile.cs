using System;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace DolaCoordinator.Models;

public enum ProfileLoginStatus
{
    Unknown,    // Chưa từng mở / chưa kiểm tra
    LoggedIn,   // Phát hiện đăng nhập Dola trong cửa sổ trình duyệt
    LoggedOut   // Đã mở nhưng chưa đăng nhập
}

/// <summary>Trạng thái hiển thị gộp: đăng nhập + phiên Dola + trạng thái thật từ gateway.</summary>
public enum ProfileState
{
    NotLoggedIn,
    LoggedIn,   // có đăng nhập nhưng chưa kiểm tra phiên với Gateway
    Ready,
    Exhausted,  // hết hạn ngạch hôm nay (quota của app hoặc gateway báo rate limit)
    NoCredit,   // gateway: quota_blocked (thiếu credit)
    Cooldown,   // gateway: cooldown 30 phút sau risk control
    Paused,     // gateway: tắt lập lịch cho tài khoản
    Rendering,  // gateway: tài khoản đang render
    NeedsAction, // profile đang mở và chờ bạn giải captcha/2FA/xác minh
    Invalid,
    Validating
}

/// <summary>
/// Một tài khoản Dola = thư mục dola-render-gateway/accounts/&lt;Name&gt; (profile Chromium do
/// gateway quản lý) + phiên Dola (cookie) dùng khi điều phối.
/// </summary>
public partial class AccountProfile : ObservableObject
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Tên tài khoản = tên thư mục trong accounts/ = tên gateway dùng (không đổi được sau khi tạo).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>DolaSession gắn với tài khoản này.</summary>
    public string? LinkedSessionId { get; set; }

    /// <summary>Dấu vân tay ngắn của sessionid lần lưu gần nhất, để biết khi nào cookie đổi.</summary>
    public string? SessionHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLaunchAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    [ObservableProperty]
    private ProfileLoginStatus _loginStatus = ProfileLoginStatus.Unknown;

    partial void OnLoginStatusChanged(ProfileLoginStatus value) => RefreshState();

    [ObservableProperty]
    private string? _notes;

    // ---- Thông tin đăng nhập đã ghi nhớ (mật khẩu/2FA/cookie mã hóa bằng Windows DPAPI, chỉ giải mã được trên máy này) ----

    public LoginMethod SavedMethod { get; set; } = LoginMethod.Manual;

    /// <summary>Email Google hoặc email/SĐT Facebook đã lưu (không phải bí mật).</summary>
    public string? SavedEmail { get; set; }

    /// <summary>DPAPI(JSON { password, totp, cookie }). Rỗng nếu chưa lưu.</summary>
    public string? SavedSecret { get; set; }

    public AfterLogin SavedAfter { get; set; } = AfterLogin.Keep;

    [BsonIgnore]
    public bool HasSavedLogin => SavedMethod != LoginMethod.Manual && !string.IsNullOrEmpty(SavedSecret);

    /// <summary>Cách đăng nhập đã lưu, ví dụ "Google · email@..." hoặc "Chưa lưu đăng nhập".</summary>
    [BsonIgnore]
    public string LoginSummary => HasSavedLogin
        ? SavedMethod switch
        {
            LoginMethod.Google => $"Google · {SavedEmail}",
            LoginMethod.Facebook => $"Facebook · {SavedEmail}",
            LoginMethod.FacebookCookie => "Cookie Facebook",
            _ => "Thủ công",
        }
        : "Chưa lưu đăng nhập";

    /// <summary>Dòng phụ dưới tên tài khoản: cách đăng nhập đã lưu (+ ghi chú nếu có).</summary>
    [BsonIgnore]
    public string SubText => string.IsNullOrWhiteSpace(Notes) ? LoginSummary : $"{LoginSummary}  ·  {Notes}";

    /// <summary>Báo giao diện cập nhật dòng phụ sau khi đổi thông tin đăng nhập đã lưu.</summary>
    public void NotifyLoginChanged() => OnPropertyChanged(nameof(SubText));

    partial void OnNotesChanged(string? value) => OnPropertyChanged(nameof(SubText));

    // ---- Runtime only ----

    /// <summary>accounts/&lt;Name&gt; — do service gán khi nạp, không lưu DB (gateway có thể đổi vị trí).</summary>
    [BsonIgnore]
    public string FolderPath { get; set; } = string.Empty;

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isRunning;

    partial void OnIsRunningChanged(bool value) => RefreshState();

    /// <summary>Lý do script đang chờ bạn (captcha, 2FA, checkpoint, sai mật khẩu...); null khi không cần.</summary>
    [ObservableProperty]
    [property: BsonIgnore]
    private string? _humanNote;

    partial void OnHumanNoteChanged(string? value) => RefreshState();

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isBusy;

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isSelected;

    /// <summary>Phiên Dola gắn với profile (token, hạn ngạch của app). Gán lại mỗi lần nạp từ DB.</summary>
    [ObservableProperty]
    [property: BsonIgnore]
    private DolaSession? _session;

    partial void OnSessionChanged(DolaSession? value) => RefreshState();

    /// <summary>Trạng thái thật do gateway giữ (/api/admin/accounts). Null khi gateway chưa chạy.</summary>
    [ObservableProperty]
    [property: BsonIgnore]
    private GatewayAccountDto? _gateway;

    partial void OnGatewayChanged(GatewayAccountDto? value) => RefreshState();

    [ObservableProperty]
    [property: BsonIgnore]
    private ProfileState _state = ProfileState.NotLoggedIn;

    [BsonIgnore]
    public string StatusText => State switch
    {
        ProfileState.Ready => "Sẵn sàng",
        ProfileState.Exhausted => "Hết hạn ngạch",
        ProfileState.NoCredit => "Hết credit",
        ProfileState.Cooldown => "Cooldown",
        ProfileState.Paused => "Tắt lập lịch",
        ProfileState.Rendering => "Đang render",
        ProfileState.NeedsAction => "Cần bạn xử lý",
        ProfileState.Invalid => "Hết phiên",
        ProfileState.Validating => "Đang kiểm tra",
        ProfileState.LoggedIn => "Đã đăng nhập",
        _ => "Chưa đăng nhập",
    };

    [BsonIgnore]
    public string QuotaText => Session == null ? "—" : $"{Session.UsedToday} / {Session.DailyLimit}";

    /// <summary>Credit: ưu tiên số liệu gateway (do worker cập nhật khi render), dự phòng số đã lưu ở phiên.</summary>
    [BsonIgnore]
    public string CreditText => (Gateway?.CreditBalance ?? Session?.CreditBalance)?.ToString() ?? "—";

    /// <summary>Chú thích lý do trạng thái (tooltip): lỗi phiên hoặc lý do gateway chặn.</summary>
    [BsonIgnore]
    public string? StatusDetail => State switch
    {
        ProfileState.Exhausted when Gateway?.RateLimited == true => Gateway.LimitReason,
        ProfileState.NoCredit => Gateway?.QuotaReason,
        ProfileState.NeedsAction => HumanNote,
        _ => Session?.LastErrorMessage,
    };

    [BsonIgnore]
    public DateTime? LastValidatedAt => Session?.LastValidatedAt;

    /// <summary>Tính lại trạng thái hiển thị từ đăng nhập + phiên + trạng thái gateway.</summary>
    public void RefreshState()
    {
        var s = Session;
        var g = Gateway;
        var loggedIn = LoginStatus == ProfileLoginStatus.LoggedIn;
        // login_ok=false do gateway ghi khi verify/worker phát hiện phiên chết: đó là tín hiệu gốc của dự án
        var dead = g?.LoginOk == false;
        // Từng có phiên (cookie) hoặc từng đăng nhập nhưng giờ kiểm tra thấy hết hạn → "Hết phiên"; chưa từng có gì → "Chưa đăng nhập"
        var hadSession = loggedIn || !string.IsNullOrEmpty(s?.EncryptedToken) || !string.IsNullOrEmpty(s?.PlainToken);

        if (IsRunning && !string.IsNullOrEmpty(HumanNote)) State = ProfileState.NeedsAction;
        else if (s?.Status == SessionStatus.Validating) State = ProfileState.Validating;
        else if (g?.Busy == true) State = ProfileState.Rendering;
        else if (dead) State = hadSession ? ProfileState.Invalid : ProfileState.NotLoggedIn;
        else if (g?.RateLimited == true) State = ProfileState.Exhausted;
        else if (g?.QuotaBlocked == true) State = ProfileState.NoCredit;
        else if (g?.Cooling == true) State = ProfileState.Cooldown;
        else if (g?.Scheduling == false) State = ProfileState.Paused;
        else
        {
            State = s?.Status switch
            {
                SessionStatus.Active => s.UsedToday >= s.DailyLimit ? ProfileState.Exhausted : ProfileState.Ready,
                SessionStatus.Exhausted => ProfileState.Exhausted,
                SessionStatus.Invalid => hadSession ? ProfileState.Invalid : ProfileState.NotLoggedIn,
                _ => loggedIn ? ProfileState.LoggedIn : ProfileState.NotLoggedIn,
            };
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(CreditText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(LastValidatedAt));
    }
}
