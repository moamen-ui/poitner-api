using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// One row per (identity, workspace) — DB-11a. Replaces the pre-DB-11a assumption that a person is
/// one <see cref="User"/> row per workspace: after DB-11a a person is one <see cref="User"/> row
/// (the identity) with zero or more memberships, one per workspace they ever joined. Every
/// per-workspace fact (role, active/disabled, approval, session revocation) lives here, never on
/// <see cref="User"/> directly (DB-RULES R8.7).
/// </summary>
public class WorkspaceMembership : BaseEntity
{
    /// <summary>The identity. Structural child of <see cref="User"/> — int FK, like <see cref="ApiKey"/> (R14).</summary>
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>The workspace. Named <c>OwnerId</c> so R8 (filter shape, HardDeleteOrder reflection
    /// test, TenantStamp) applies unchanged.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>The role in THIS workspace.</summary>
    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    /// <summary>Disable/enable lives here (per workspace).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Approval is per workspace (stakeholder self-signup is pending in that workspace).</summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;

    /// <summary>
    /// JWT <c>mstamp</c>. Rotated on role change / disable / removal — revokes THIS workspace's
    /// sessions only (DB-RULES R16).
    /// </summary>
    public Guid SecurityStamp { get; set; } = Guid.NewGuid();

    public DateTime JoinedAt { get; set; }

    /// <summary>Non-null = ended membership (removed / left / erased). Row is kept for audit.</summary>
    public DateTime? LeftAt { get; set; }

    /// <summary>Append-only (R10). Null while the membership is live.</summary>
    public MembershipEndReason? LeftReason { get; set; }

    /// <summary>Which invite created this membership (DB-11c's "also disable the invitee" needs it).</summary>
    public int? InviteId { get; set; }
}
