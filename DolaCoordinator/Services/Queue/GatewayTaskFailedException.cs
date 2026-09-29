using System;

namespace DolaCoordinator.Services.Queue;

/// <summary>
/// Gateway báo tác vụ thất bại kèm mã lỗi máy đọc được (failure_code) để bộ điều phối quyết định có đổi tài khoản không:
/// account_limited | credit | risk_control | login_required | unhealthy | timeout | 429 | no_account | error.
/// </summary>
public sealed class GatewayTaskFailedException : Exception
{
    public string? FailureCode { get; }

    public GatewayTaskFailedException(string message, string? failureCode) : base(message)
    {
        FailureCode = failureCode;
    }
}
