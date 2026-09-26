namespace Pointer.Application.DTOs.Tenant;

/// <summary>Body for <c>PATCH /api/admin/tenants/{workspaceId}/plan</c>. Moved out of
/// <c>TenantsController</c> (review finding #4) so <c>ChangeTenantPlanRequestValidator</c>
/// (<c>Application/Validators/</c>) — picked up by <c>AddValidatorsFromAssembly</c> over the
/// Application assembly — can auto-run on model binding, same as every other write DTO.</summary>
public class ChangeTenantPlanRequest
{
    public int PlanId { get; set; }

    /// <summary>DB-20 §3.6e: only meaningful when the plan is paid (comp marker) — free text, no
    /// personal data. Null defaults to "Assigned by operator".</summary>
    public string? CompReason { get; set; }

    /// <summary>DB-20 §3.6e: optional comp expiry, must be in the future when set.</summary>
    public DateTime? CompEndsAt { get; set; }
}
