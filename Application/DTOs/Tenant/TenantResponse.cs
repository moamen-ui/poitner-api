namespace Pointer.Application.DTOs.Tenant;

public class TenantResponse
{
    public int Id { get; set; }
    public Guid PublicId { get; set; }

    // The tenant's STABLE identifier — unlike PublicId (the current Workspace Admin's own row id,
    // which changes on succession via UserService.TransferOwnershipAsync), OwnerId never moves.
    // Callers that need to reliably re-target this workspace later (e.g. the super-admin add-user/
    // invite workspace picker's TargetOwnerId) must use this, not PublicId.
    public Guid OwnerId { get; set; }

    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ApprovalStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Projects { get; set; }
    public int Comments { get; set; }

    // Effective plan (missing subscription ⇒ Free). Batch-loaded in ListAsync.
    public string? PlanName { get; set; }
    public string? SubscriptionStatus { get; set; }

    // Demo tenants: surfaced so the super-admin UI can offer a one-time "Extend demo" action
    // and per-tenant overrides of the demo comment cap / TTL (null = use the global default).
    public bool IsDemo { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool DemoExtended { get; set; }
    public int? DemoCommentCapOverride { get; set; }
    public int? DemoTtlHoursOverride { get; set; }
}
