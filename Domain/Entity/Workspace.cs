namespace Pointer.Domain.Entity;

/// <summary>
/// One row per workspace. <see cref="Id"/> equals every <c>owner_id</c> that belongs to it and the
/// JWT <c>tenant</c> claim. <see cref="Name"/> is the workspace's own name, never a person's. Not a
/// <see cref="BaseEntity"/>: the PK is a uuid chosen before insert.
/// </summary>
public class Workspace
{
    /// <summary>
    /// Seeded by the DB-03 backfill and used by every mint point that has no workspace name to
    /// offer. <c>Name == PlaceholderName</c> means 'not yet named by an admin' (DB-03b shows a
    /// prompt).
    /// </summary>
    public const string PlaceholderName = "Workspace";

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }

    /// <summary>
    /// DB-17 (F4). Demo state lives on the WORKSPACE: non-null DemoExpiresAt = a live demo that the sweep hard-deletes at expiry;
    /// DemoConvertedAt = it was kept ("convert to your workspace"); with Demo:ConvertRequiresVerification off the TTL is cleared at the same time, with it on the TTL runs until the new address is verified (no check constraint — DB-17 §3.1 lists the code-enforced invariants).
    /// The admin identity's users.is_demo remains the separate fact "synthetic demo login without a real address" (exempt from e-mail
    /// verification, excluded from password reset). The former users.expires_at / "DemoExtended" / "DemoCommentCapOverride" /
    /// "DemoTtlHoursOverride" copies were dropped by DB-11e; these columns are the only demo state.
    /// </summary>
    public DateTime? DemoExpiresAt { get; set; }
    public DateTime? DemoExtendedAt { get; set; }
    public DateTime? DemoConvertedAt { get; set; }
    public DateTime? DemoExpiryWarnedAt { get; set; }
    public int? DemoCommentCapOverride { get; set; }
    public int? DemoTtlHoursOverride { get; set; }

    /// <summary>
    /// DB-18. Workspace lifecycle. Frozen (read-only for members, widget and CLI) ⇔ PausedAt != null || DeletionScheduledFor != null
    /// (enforced by API/Auth/WorkspaceFrozenFilter via IWorkspaceStateService). PausedByOperator = only the operator may resume.
    /// DeletionRequestedAt = newest e-mailed request (the scoped token binds it; a newer request or a cancel voids older links);
    /// DeletionScheduledFor = confirmed with password, deleted by WorkspaceDeletionService via TenantService.HardDeleteAsync(id, "owner_requested").
    /// DeletedAt is NOT used for the grace period (members must still sign in to export or cancel). PausedBy/DeletionRequestedBy are
    /// users.public_id content references (R14, no FK). Check constraints: DB-18 §3.2.
    /// </summary>
    public DateTime? PausedAt { get; set; }
    public Guid? PausedBy { get; set; }
    public bool PausedByOperator { get; set; }
    public DateTime? DeletionRequestedAt { get; set; }
    public Guid? DeletionRequestedBy { get; set; }
    public DateTime? DeletionConfirmedAt { get; set; }
    public DateTime? DeletionScheduledFor { get; set; }
    public DateTime? DeletionReminderSentAt { get; set; }
}
