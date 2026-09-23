using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// Append-only (DB-12). Never updated or deleted; Postgres trigger
/// <c>trg_audit_events_append_only</c> + <c>AppDbContext.SaveChangesAsync</c> guard. The single
/// permitted UPDATE shape is the <c>fk_audit_events_workspaces_owner_id</c> SET NULL action
/// detaching the row from a hard-deleted workspace — operator record, R8.8, never swept (R17).
/// </summary>
public class AuditEvent
{
    public long Id { get; init; }

    public DateTime OccurredAt { get; init; }

    /// <summary>The workspace the action happened in. NULL = operator-level action, or the workspace was hard-deleted (FK detaches the row).</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>Identity <see cref="User.PublicId"/> (content reference, no FK — R14). NULL for System and anonymous failures.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary><see cref="WorkspaceMembership.Id"/> of the acting membership, when the actor acted inside a workspace. No FK — the row survives the membership.</summary>
    public int? ActorMembershipId { get; init; }

    public AuditActorKind ActorKind { get; init; }

    /// <summary>Dotted, lower-case, from the <c>AuditActions</c> catalogue — never a literal at a call site.</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>From the <c>AuditTargets</c> constants.</summary>
    public string TargetType { get; init; } = string.Empty;

    /// <summary>The target's id as text (uuid / int / 16-hex email hash). Never a raw e-mail.</summary>
    public string? TargetId { get; init; }

    /// <summary>Whitelisted keys only (<c>AuditFields.Allowed</c>) — never names, addresses, secrets or content. Shape versions by adding keys, never repurposing (R12).</summary>
    public Dictionary<string, string> Before { get; init; } = new();

    /// <summary>Same shape as <see cref="Before"/>.</summary>
    public Dictionary<string, string> After { get; init; } = new();

    /// <summary>From the request-id middleware; NULL for hosted jobs.</summary>
    public string? RequestId { get; init; }

    /// <summary>Keyed HMAC-SHA256 of the remote IP — pseudonymous, not reversible without the key.</summary>
    public string? IpHash { get; init; }

    /// <summary>Truncated to 256 chars.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Logical reference to DB-13's impersonation_sessions.id (no FK; NULL until DB-13 ships).</summary>
    public long? ImpersonationSessionId { get; init; }
}
