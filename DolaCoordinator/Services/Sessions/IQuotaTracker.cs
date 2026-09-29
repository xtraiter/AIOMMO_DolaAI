using System;
using System.Collections.Generic;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Sessions;

public interface IQuotaTracker
{
    void EnsureDailyQuotaReset(IEnumerable<DolaSession> sessions);
    bool HasRemainingQuota(DolaSession session);
    void IncrementUsedQuota(string sessionId);

    /// <summary>Hoàn lại một lượt đã tính trước (tác vụ chưa được gateway nhận / chưa tốn lượt Dola).</summary>
    void ReleaseQuota(string sessionId);
    void ResetQuota(string sessionId);

    /// <summary>Đặt giới hạn số video/ngày cho TẤT CẢ tài khoản hiện có và tính lại trạng thái Sẵn sàng / Hết hạn ngạch.</summary>
    void ApplyDailyLimit(int limit);
    void ResetAllQuotas();
    DateTime GetNextResetTime();
}
