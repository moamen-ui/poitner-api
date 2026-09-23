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
    AuditActorKind? ActorKindOverride = null
);
