using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Network;

public interface IDolaGatewayClient
{
    Task<GatewayHealthResponse?> CheckHealthAsync(string? gatewayUrl = null, CancellationToken ct = default);
    Task<TaskApiResponse?> CreateVideoTaskAsync(VideoGenApiRequest request, string? clientApiKey = null, CancellationToken ct = default);
    Task<TaskApiResponse?> GetTaskStatusAsync(string taskId, string? clientApiKey = null, CancellationToken ct = default);

    /// <summary>Hủy tác vụ đang chạy trên gateway (DELETE /v1/videos/{id}): đóng Chromium của tác vụ để tài khoản được giải phóng. Trả false nếu không gọi được.</summary>
    Task<bool> CancelTaskAsync(string taskId, string? clientApiKey = null, CancellationToken ct = default);

    /// <summary>Danh sách tài khoản trong pool gateway (GET /api/admin/accounts). Null = gateway chưa chạy hoặc sai Admin Key.</summary>
    Task<List<GatewayAccountDto>?> GetAccountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Nhờ gateway kiểm tra đăng nhập thật bằng Chromium headless trên profile của tài khoản
    /// (POST /api/admin/accounts/{name}/verify). Kết quả ghi vào login_ok của gateway.
    /// Lỗi 409 khi tài khoản đang render hoặc profile đang mở.
    /// </summary>
    Task<(bool Ok, bool? LoginOk, string? Error)> VerifyAccountAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Xóa tài khoản (thư mục accounts/&lt;name&gt; + metadata) qua gateway. Unreachable=true khi gateway không chạy
    /// (người gọi có thể tự xóa thư mục); Ok=false, Unreachable=false khi gateway từ chối (vd. đang render).
    /// </summary>
    Task<(bool Ok, bool Unreachable, string? Error)> DeleteAccountAsync(string name, CancellationToken ct = default);

    Task<(bool Success, string? ConvertedCookie, string? ErrorMessage)> ImportCookieWithResultAsync(string name, string cookieToken, CancellationToken ct = default);
    Task<Stream> OpenVideoStreamAsync(string videoUrl, CancellationToken ct = default);
}
