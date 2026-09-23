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

    /// <summary>DB-17 §3.3: keyed on the workspace id (the demo authority now); dual-writes the
    /// current admin identity for one release.</summary>
    Task<Result> ExtendDemoAsync(Guid workspaceId);

    /// <summary>DB-17 §3.3: keyed on the workspace id; dual-writes the current admin identity for
    /// one release.</summary>
    Task<Result> SetDemoConfigAsync(
        Guid workspaceId,
        int? commentCapOverride,
        int? ttlHoursOverride
    );

    /// <summary>DB-12: `reason` names who/why for the `tenant.hard_deleted` audit row — "admin" (the
    /// default, `TenantsController.Delete`) or "demo_expired" (`DemoCleanupService`'s sweep).</summary>
    Task<Result> HardDeleteAsync(Guid workspaceId, string reason = "admin");

    /// <summary>Upsert the tenant's subscription to the given plan (super-admin), via the billing
    /// seam. F9 (DB-11a cross-review): keyed on the workspace id, never an admin's `users.id`.</summary>
    Task<Result> ChangePlanAsync(Guid workspaceId, int planId);
}
