using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ITenantService
{
    Task<Result<List<TenantResponse>>> ListAsync();
    Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request);

    /// <summary>F9 (DB-11a cross-review): keyed on the workspace id, never an admin's `users.id` —
    /// an identity administering several workspaces (D13) must be able to act on each one.</summary>
    Task<Result> SetStatusAsync(Guid workspaceId, string action);

    /// <summary>DB-17 §3.3: keyed on the workspace id (the only demo authority since DB-11e).</summary>
    Task<Result> ExtendDemoAsync(Guid workspaceId);

    /// <summary>DB-17 §3.3: keyed on the workspace id (the only demo authority since DB-11e).</summary>
    Task<Result> SetDemoConfigAsync(
        Guid workspaceId,
        int? commentCapOverride,
        int? ttlHoursOverride
    );

    /// <summary>DB-12: `reason` names who/why for the `tenant.hard_deleted` audit row — "admin" (the
    /// default, `TenantsController.Delete`) or "demo_expired" (`DemoCleanupService`'s sweep).</summary>
    Task<Result> HardDeleteAsync(Guid workspaceId, string reason = "admin");

    /// <summary>Upsert the tenant's subscription to the given plan (super-admin), via the billing
    /// seam. F9 (DB-11a cross-review): keyed on the workspace id, never an admin's `users.id`.
    /// DB-20 §3.6e: a paid plan (<c>PriceMonthly &gt; 0</c>) is granted complimentary (comp-stamped,
    /// never expires unless <paramref name="compEndsAt"/> is set); a price-0 plan clears any comp
    /// marker. Either way any pending billing request is cleared and its Pending redemption
    /// released.</summary>
    Task<Result> ChangePlanAsync(
        Guid workspaceId,
        int planId,
        string? compReason = null,
        DateTime? compEndsAt = null
    );
}
