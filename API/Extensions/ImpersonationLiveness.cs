using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pointer.Infrastructure;

namespace Pointer.API.Extensions;

/// <summary>
/// DB-13 §3.5: the per-request liveness check for an impersonation token — a live row for exactly
/// this operator/workspace/session, not yet ended or expired. Fails CLOSED (unlike the identity-stamp
/// lookup, which fails open on a transient DB error) because the whole point of this check is that
/// the session is provably live. Extracted as a static method (no cache) so it is directly unit
/// testable, mirroring RetentionServiceTests's SweepOnceAsync pattern.
/// </summary>
public static class ImpersonationLiveness
{
    public static Task<bool> IsLiveAsync(
        AppDbContext db,
        long impersonationSessionId,
        Guid targetWorkspaceId,
        Guid operatorPublicId
    ) =>
        db
            .ImpersonationSessions.IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(s =>
                s.Id == impersonationSessionId
                && s.OwnerId == targetWorkspaceId
                && s.OperatorUserId == operatorPublicId
                && s.EndedAt == null
                && s.ExpiresAt > DateTime.UtcNow
            );
}
