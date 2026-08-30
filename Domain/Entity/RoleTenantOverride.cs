namespace Pointer.Domain.Entity;

/// <summary>
/// A tenant's own override of a GLOBAL, non-system role's active status (e.g. disabling the seeded
/// "Tester" role for just this workspace) — never mutates the shared Role row every tenant reads.
/// One row per (RoleId, OwnerId); its absence means "use the global role's own IsActive as-is".
/// System roles (Admin, Workspace Admin, Workspace Admin Deputy) are never overridable this way —
/// RoleService blocks that before an override row could ever be created for one.
/// </summary>
public class RoleTenantOverride : BaseEntity
{
    public int RoleId { get; set; }
    public Guid OwnerId { get; set; }
    public bool IsActive { get; set; }
}
