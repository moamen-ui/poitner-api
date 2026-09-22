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
    Task<Result> ExtendDemoAsync(int id);
    Task<Result> SetDemoConfigAsync(int id, int? commentCapOverride, int? ttlHoursOverride);
    Task<Result> HardDeleteAsync(Guid workspaceId);

    /// <summary>Upsert the tenant's subscription to the given plan (super-admin), via the billing
    /// seam. F9 (DB-11a cross-review): keyed on the workspace id, never an admin's `users.id`.</summary>
    Task<Result> ChangePlanAsync(Guid workspaceId, int planId);
}
