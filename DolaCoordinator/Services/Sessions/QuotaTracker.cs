using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DolaCoordinator.Models;
using DolaCoordinator.Services.Storage;

namespace DolaCoordinator.Services.Sessions;

public class QuotaTracker : IQuotaTracker, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly Timer _midnightTimer;
    private readonly object _lock = new();

    public QuotaTracker(IDatabaseService databaseService)
    {
        _databaseService = databaseService;

        // Schedule timer to run at 00:00 midnight daily
        var initialDelay = GetTimeUntilMidnight();
        _midnightTimer = new Timer(OnMidnightTriggered, null, initialDelay, TimeSpan.FromHours(24));
    }

    private TimeSpan GetTimeUntilMidnight()
    {
        var now = DateTime.Now;
        var tomorrow = now.Date.AddDays(1);
        var diff = tomorrow - now;
        return diff.TotalMilliseconds > 0 ? diff : TimeSpan.FromSeconds(60);
    }

    public DateTime GetNextResetTime()
    {
        return DateTime.Now.Date.AddDays(1);
    }

    private void OnMidnightTriggered(object? state)
    {
        lock (_lock)
        {
            ResetAllQuotas();
        }
    }

    public void EnsureDailyQuotaReset(IEnumerable<DolaSession> sessions)
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var updated = new List<DolaSession>();

        foreach (var s in sessions)
        {
            if (s.LastResetDate != today)
            {
                s.UsedToday = 0;
                s.LastResetDate = today;
                if (s.Status == SessionStatus.Exhausted)
                {
                    s.Status = SessionStatus.Active;
                }
                updated.Add(s);
            }
        }

        if (updated.Count > 0)
        {
            _databaseService.UpsertSessions(updated);
        }
    }

    public bool HasRemainingQuota(DolaSession session)
    {
        EnsureDailyQuotaReset(new[] { session });
        return session.UsedToday < session.DailyLimit;
    }

    public void IncrementUsedQuota(string sessionId)
    {
        lock (_lock)
        {
            var session = _databaseService.GetSessionById(sessionId);
            if (session == null) return;

            session.UsedToday++;
            if (session.UsedToday >= session.DailyLimit)
            {
                session.Status = SessionStatus.Exhausted;
            }

            _databaseService.UpsertSession(session);
        }
    }

    public void ReleaseQuota(string sessionId)
    {
        lock (_lock)
        {
            var session = _databaseService.GetSessionById(sessionId);
            if (session == null) return;

            session.UsedToday = Math.Max(0, session.UsedToday - 1);
            if (session.Status == SessionStatus.Exhausted && session.UsedToday < session.DailyLimit)
            {
                session.Status = SessionStatus.Active; // đã bị khóa chỉ vì lượt vừa tính trước
            }

            _databaseService.UpsertSession(session);
        }
    }

    public void ResetQuota(string sessionId)
    {
        lock (_lock)
        {
            var session = _databaseService.GetSessionById(sessionId);
            if (session == null) return;

            session.UsedToday = 0;
            session.LastResetDate = DateTime.Today.ToString("yyyy-MM-dd");
            if (session.Status == SessionStatus.Exhausted)
            {
                session.Status = SessionStatus.Active;
            }
            _databaseService.UpsertSession(session);
        }
    }

    public void ApplyDailyLimit(int limit)
    {
        lock (_lock)
        {
            var all = _databaseService.GetAllSessions();
            foreach (var s in all)
            {
                s.DailyLimit = limit;
                if (s.Status == SessionStatus.Exhausted && s.UsedToday < limit)
                    s.Status = SessionStatus.Active;
                else if (s.Status == SessionStatus.Active && s.UsedToday >= limit)
                    s.Status = SessionStatus.Exhausted;
            }
            _databaseService.UpsertSessions(all);
        }
    }

    public void ResetAllQuotas()
    {
        lock (_lock)
        {
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var all = _databaseService.GetAllSessions();
            foreach (var s in all)
            {
                s.UsedToday = 0;
                s.LastResetDate = today;
                if (s.Status == SessionStatus.Exhausted)
                {
                    s.Status = SessionStatus.Active;
                }
            }
            _databaseService.UpsertSessions(all);
        }
    }

    public void Dispose()
    {
        _midnightTimer.Dispose();
    }
}
