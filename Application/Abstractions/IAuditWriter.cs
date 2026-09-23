using Pointer.Domain.Enums;

namespace Pointer.Application.Abstractions;

/// <summary>DB-12. Writes one append-only audit row for a security-relevant mutation. Call it AFTER the mutation's
/// SaveChangesAsync succeeded (or inside the same ExecuteInTransactionAsync block, after the mutation's save). The writer
/// fills occurred_at, actor (from ICurrentUser), request id, ip hash, user agent and impersonation session itself; the
/// caller supplies action, target and the whitelisted before/after fields. Best-effort by default (§3.9 D12.1): a write
/// failure is logged at Error and swallowed unless Audit:FailClosed=true.</summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);
}

/// <summary>What a call site knows. Owner = the workspace the action happened in (null = operator-level).</summary>
public sealed record AuditEntry(
    string Action,
    string TargetType,
    string? TargetId,
    Guid? OwnerId,
    IReadOnlyDictionary<string, string>? Before = null,
    IReadOnlyDictionary<string, string>? After = null,
    Guid? ActorUserIdOverride = null,
    AuditActorKind? ActorKindOverride = null,
    // DB-13 review fix #3: same shape as ActorKindOverride, for the same reason — ICurrentUser
    // carries no impersonation session id on the caller's OWN token at exactly the moments the row
    // needs one: ImpersonationService.StartAsync (the caller is still a plain super admin; the
    // session doesn't exist yet when the row is written), EndAsync (a plain super-admin token ending
    // a session by id, per §3.6), and the expiry sweep (no ICurrentUser at all). Falls back to
    // currentUser.ImpersonationSessionId (the ordinary in-session case) when omitted.
    long? ImpersonationSessionIdOverride = null
);
