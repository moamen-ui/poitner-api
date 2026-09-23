namespace Pointer.Application.DTOs.Tenant;

public class TenantResponse
{
    // LEGACY (F9, DB-11a cross-review): the current admin's `users.id`, kept only so existing
    // dashboard code that happens to read it does not immediately null-ref; it is 0 for an
    // admin-less workspace and — once one identity can administer several workspaces (D13) — the
    // SAME int can legitimately appear on more than one row. Never key a super-admin tenant action
    // on this; use WorkspaceId.
    public int Id { get; set; }
    public Guid PublicId { get; set; }

    // The tenant's STABLE identifier — unlike PublicId (the current Workspace Admin's own row id,
    // which changes on succession via UserService.TransferOwnershipAsync), OwnerId never moves.
    // Callers that need to reliably re-target this workspace later (e.g. the super-admin add-user/
    // invite workspace picker's TargetOwnerId) must use this, not PublicId.
    public Guid OwnerId { get; set; }

    // F9 (DB-11a cross-review): the canonical route key for every super-admin tenant action
    // (SetStatusAsync/ChangePlanAsync/HardDeleteAsync and the corresponding TenantsController
    // routes) — always the workspace's own id, always unique per row, unlike Id above. Same value
    // as OwnerId today (both are `Workspace.Id`); kept as its own field so the route contract reads
    // by name rather than by the historical "OwnerId happens to be the workspace id" convention.
    public Guid WorkspaceId { get; set; }

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    // The workspace's own name (workspaces.name); DisplayName above is the admin's.
    public string WorkspaceName { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Projects { get; set; }
    public int Comments { get; set; }

    // Effective plan (missing subscription ⇒ Free). Batch-loaded in ListAsync.
    public string? PlanName { get; set; }
    public string? SubscriptionStatus { get; set; }

    // Demo tenants (sourced from workspaces.demo_* — DB-17; the users copies were dropped by DB-11e): surfaced so the super-admin UI can offer a one-time "Extend demo" action
    // and per-tenant overrides of the demo comment cap / TTL (null = use the global default).
    public bool IsDemo { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool DemoExtended { get; set; }
    public int? DemoCommentCapOverride { get; set; }
    public int? DemoTtlHoursOverride { get; set; }

    // DB-18 — self-service pause/delete lifecycle, operator view.
    public DateTime? PausedAt { get; set; }
    public bool PausedByOperator { get; set; }
    public DateTime? DeletionScheduledFor { get; set; }
}
