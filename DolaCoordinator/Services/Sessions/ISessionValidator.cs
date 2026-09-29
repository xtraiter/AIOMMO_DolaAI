using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DolaCoordinator.Models;

namespace DolaCoordinator.Services.Sessions;

public interface ISessionValidator
{
    Task<DolaSession> ValidateSessionAsync(DolaSession session, CancellationToken ct = default);
    Task<List<DolaSession>> ValidateAllSessionsAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
