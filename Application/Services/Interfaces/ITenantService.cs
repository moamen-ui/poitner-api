using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ITenantService
{
    Task<Result<List<TenantResponse>>> ListAsync();
    Task<Result<TenantResponse>> CreateAsync(CreateTenantRequest request);
    Task<Result> SetStatusAsync(int id, string action);
    Task<Result> HardDeleteAsync(Guid tenantId);
}
