using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ITenantService
{
    Task<Result<List<TenantResponse>>> ListAsync();
    Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request);
    Task<Result> SetStatusAsync(int id, string action);
    Task<Result> ExtendDemoAsync(int id);
    Task<Result> SetDemoConfigAsync(int id, int? commentCapOverride, int? ttlHoursOverride);
    Task<Result> HardDeleteAsync(Guid workspaceId);

    /// <summary>Upsert the tenant's subscription to the given plan (super-admin), via the billing seam.</summary>
    Task<Result> ChangePlanAsync(int tenantId, int planId);

    /// <summary>DB-11a: resolves the int id (users.id) of a tenant's admin to its stable workspace
    /// id, for <see cref="HardDeleteAsync"/>. Null when not found or ambiguous (the identity
    /// administers more than one workspace).</summary>
    Task<Guid?> ResolveWorkspaceIdAsync(int adminUserId);
}
