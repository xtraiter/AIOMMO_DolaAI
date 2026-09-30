namespace DolaCoordinator.Models;

/// <summary>Yêu cầu chuyển sang một trang (0 = Tạo video, 1 = Quản lý tài khoản, 2 = Cài đặt, 3 = Quản lý prompt, 4 = Quản lý proxy).</summary>
public sealed record NavigateMessage(int TabIndex);
