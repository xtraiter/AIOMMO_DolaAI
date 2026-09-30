using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using LiteDB;

namespace DolaCoordinator.Models;

/// <summary>Một proxy trong danh sách. Mật khẩu được mã hóa bằng Windows DPAPI; tài khoản nào gán proxy này sẽ mở Chromium qua nó.</summary>
public partial class ProxyItem : ObservableObject
{
    public static readonly string[] Schemes = { "http", "https", "socks5" };

    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>http | https | socks5.</summary>
    public string Scheme { get; set; } = "http";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8080;

    public string? Username { get; set; }

    /// <summary>DPAPI(mật khẩu). Rỗng nếu proxy không cần đăng nhập.</summary>
    public string? EncryptedPassword { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ---- kết quả kiểm tra gần nhất ----
    public DateTime? LastTestAt { get; set; }
    public bool? LastOk { get; set; }
    public string? LastIp { get; set; }
    public string? LastCountry { get; set; }
    public string? LastCity { get; set; }
    public int? LastLatencyMs { get; set; }
    public string? LastError { get; set; }

    // ---- chỉ dùng khi chạy (không lưu) ----

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isSelected;

    [ObservableProperty]
    [property: BsonIgnore]
    private bool _isTesting;

    /// <summary>Số tài khoản đang dùng proxy này.</summary>
    [BsonIgnore] public int AccountCount { get; set; }

    [BsonIgnore] public string AccountNames { get; set; } = string.Empty;

    [BsonIgnore] public string Address => $"{Scheme}://{Host}:{Port}";

    [BsonIgnore] public bool HasAuth => !string.IsNullOrEmpty(Username);

    [BsonIgnore]
    public string AuthText => HasAuth ? $"Có (tài khoản {Username})" : "Không";

    /// <summary>Kết quả kiểm tra gần nhất, một dòng.</summary>
    [BsonIgnore]
    public string TestText
    {
        get
        {
            if (IsTesting) return "Đang kiểm tra...";
            if (LastOk == null) return "Chưa kiểm tra";
            if (LastOk == true)
            {
                var place = string.Join(", ", new[] { LastCity, LastCountry }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return $"{LastIp} · {(string.IsNullOrEmpty(place) ? "?" : place)} · {LastLatencyMs} ms";
            }
            return "Lỗi: " + LastError;
        }
    }

    /// <summary>Dola thường cần IP thoát ở Nhật / Hàn: báo nếu proxy đã kiểm tra nhưng ra nước khác.</summary>
    [BsonIgnore]
    public bool CountryWarning => LastOk == true
        && !string.IsNullOrEmpty(LastCountry)
        && !(LastCountry.Equals("JP", StringComparison.OrdinalIgnoreCase) || LastCountry.Equals("KR", StringComparison.OrdinalIgnoreCase));

    [BsonIgnore]
    public string UsageText => AccountCount == 0 ? "Chưa gán" : $"{AccountCount} tài khoản: {AccountNames}";

    [BsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

    public void NotifyChanged() => OnPropertyChanged(string.Empty);
}
