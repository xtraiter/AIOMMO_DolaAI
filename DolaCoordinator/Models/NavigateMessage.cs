namespace DolaCoordinator.Models;

/// <summary>Yêu cầu chuyển sang một trang (0 = Vận hành, 1 = Quản lý tài khoản, 2 = Cài đặt, 3 = Quản lý prompt, 4 = Kịch bản lớn).</summary>
public sealed record NavigateMessage(int TabIndex);
