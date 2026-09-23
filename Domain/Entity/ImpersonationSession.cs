using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// Operator record (DB-13): FK to workspaces SET NULL; survives the workspace; excluded from
/// HardDeleteOrder. One row per "View as…" session (F2): a super admin's audited, time-boxed,
/// read-only look at a workspace's content. NOT a <see cref="BaseEntity"/> — no OwnerId/CreatedAt
/// tenant-audit fields other than the ones declared here, same shape family as
/// <see cref="UsageEvent"/> and <see cref="AuditEvent"/>. <see cref="Id"/> is the JWT "imp" claim.
/// </summary>
public class ImpersonationSession
{
    public long Id { get; init; }

    /// <summary>The impersonated workspace. NULL after it is hard-deleted (FK SET NULL).</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>The super admin's identity <see cref="User.PublicId"/> (content reference, no FK — R14).</summary>
    public Guid OperatorUserId { get; set; }

    /// <summary>Free text, 10–500 chars (validator) — shown to the workspace's admins.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; }

    /// <summary>StartedAt + Minutes; the token's hard `exp`.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set by the manual end call or by the sweep.</summary>
    public DateTime? EndedAt { get; set; }

    public ImpersonationEndReason? EndReason { get; set; }

    /// <summary>Incremented by ImpersonationRequestCounter on every authenticated request carrying "imp".</summary>
    public int RequestCount { get; set; }

    public DateTime? LastRequestAt { get; set; }
}
