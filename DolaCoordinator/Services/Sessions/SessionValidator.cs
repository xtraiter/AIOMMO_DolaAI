using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Network;
using DolaCoordinator.Services.Security;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Sessions;

/// <summary>
/// Kiểm tra phiên bằng chính cơ chế của gateway: mở profile accounts/&lt;tên&gt; bằng Chromium headless
/// và xem còn đăng nhập Dola không (browser.check_login_state). Không tự suy đoán từ chuỗi cookie.
/// </summary>
public class SessionValidator : ISessionValidator
{
    private readonly IDolaGatewayClient _gatewayClient;
    private readonly ISecurityService _securityService;
    private readonly IDatabaseService _databaseService;
    private readonly IQuotaTracker _quotaTracker;

    public SessionValidator(
        IDolaGatewayClient gatewayClient,
        ISecurityService securityService,
        IDatabaseService databaseService,
        IQuotaTracker quotaTracker)
    {
        _gatewayClient = gatewayClient;
        _securityService = securityService;
        _databaseService = databaseService;
        _quotaTracker = quotaTracker;
    }

    public async Task<DolaSession> ValidateSessionAsync(DolaSession session, CancellationToken ct = default)
    {
        session.Status = SessionStatus.Validating;

        string plainToken = session.PlainToken ?? _securityService.Decrypt(session.EncryptedToken);
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return Finish(session, SessionStatus.Invalid, "Chưa có cookie đăng nhập. Mở profile và đăng nhập Dola, hoặc dán cookie.");
        }

        // Cookie Facebook: gateway tự đăng nhập Dola bằng Facebook rồi trả về cookie Dola (fb_to_dola.py)
        if (plainToken.Contains("c_user=") || plainToken.Contains("xs="))
        {
            session.LastErrorMessage = "Phát hiện Cookie Facebook. Đang nhờ gateway đăng nhập Dola...";
            _databaseService.UpsertSession(session);

            var (importOk, dolaCookie, importErr) = await _gatewayClient.ImportCookieWithResultAsync(session.Name, plainToken, ct);
            if (!importOk || string.IsNullOrWhiteSpace(dolaCookie))
            {
                return Finish(session, SessionStatus.Invalid,
                    importErr ?? "Không thể chuyển Cookie Facebook sang Dola. Kiểm tra Cookie Facebook còn sống không.");
            }

            plainToken = dolaCookie;
            session.PlainToken = plainToken;
            session.EncryptedToken = _securityService.Encrypt(plainToken);
        }

        // Kiểm tra thật bằng gateway. Không kết luận được (gateway tắt, tài khoản đang render...) thì để Unknown,
        // không đánh dấu Invalid oan.
        var (ok, loginOk, error) = await _gatewayClient.VerifyAccountAsync(session.Name, ct);
        if (!ok)
        {
            return Finish(session, SessionStatus.Unknown, error);
        }

        var accounts = await _gatewayClient.GetAccountsAsync(ct);
        var meta = accounts?.FirstOrDefault(a => a.Name.Equals(session.Name, StringComparison.OrdinalIgnoreCase));
        if (meta?.CreditBalance is int credit)
        {
            session.CreditBalance = credit;
        }

        if (loginOk != true)
        {
            return Finish(session, SessionStatus.Invalid, "Chưa đăng nhập Dola (cookie hết hạn hoặc sai). Mở profile và đăng nhập lại.");
        }

        _quotaTracker.EnsureDailyQuotaReset(new[] { session });
        return Finish(session,
            session.UsedToday >= session.DailyLimit ? SessionStatus.Exhausted : SessionStatus.Active,
            null);
    }

    private DolaSession Finish(DolaSession session, SessionStatus status, string? message)
    {
        session.Status = status;
        session.LastErrorMessage = message;
        session.LastValidatedAt = DateTime.UtcNow;
        _databaseService.UpsertSession(session);
        return session;
    }

    public async Task<List<DolaSession>> ValidateAllSessionsAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var sessions = _databaseService.GetAllSessions();
        _quotaTracker.EnsureDailyQuotaReset(sessions);

        int total = sessions.Count;
        int completed = 0;

        foreach (var session in sessions)
        {
            if (ct.IsCancellationRequested) break;
            await ValidateSessionAsync(session, ct);
            completed++;
            progress?.Report((int)((double)completed / total * 100));
        }

        return _databaseService.GetAllSessions();
    }
}
