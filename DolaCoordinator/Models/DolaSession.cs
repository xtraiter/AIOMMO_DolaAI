using System;
using LiteDB;

namespace DolaCoordinator.Models;

public class DolaSession : System.ComponentModel.INotifyPropertyChanged
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Chuỗi session/cookie đã được mã hóa an toàn bằng DPAPI (ProtectedData).
    /// </summary>
    public string EncryptedToken { get; set; } = string.Empty;

    public SessionStatus Status { get; set; } = SessionStatus.Unknown;

    /// <summary>
    /// Số lần render đã dùng trong ngày.
    /// </summary>
    public int UsedToday { get; set; } = 0;

    /// <summary>
    /// Giới hạn quota cho phiên (mặc định 2).
    /// </summary>
    public int DailyLimit { get; set; } = 2;

    /// <summary>
    /// Ngày ghi nhận quota gần nhất dạng yyyy-MM-dd để reset lúc 00:00.
    /// </summary>
    public string LastResetDate { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    public DateTime? LastValidatedAt { get; set; }

    public string? LastErrorMessage { get; set; }

    public bool IsEnabled { get; set; } = true;

    public int? CreditBalance { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Runtime-only (không lưu DB)
    [BsonIgnore]
    public string? PlainToken { get; set; }

    [BsonIgnore]
    public bool IsSchedulable => IsEnabled && Status == SessionStatus.Active && UsedToday < DailyLimit;

    [BsonIgnore]
    public int RemainingQuota => Math.Max(0, DailyLimit - UsedToday);

    // Observable support for UI selection
    [BsonIgnore]
    private bool _isSelected;
    [BsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}
