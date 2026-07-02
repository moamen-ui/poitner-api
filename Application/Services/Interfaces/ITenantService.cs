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
    Task<Result> HardDeleteAsync(Guid tenantId);

    /// <summary>Upsert the tenant's subscription to the given plan (super-admin), via the billing seam.</summary>
    Task<Result> ChangePlanAsync(int tenantId, int planId);
}
