using Pointer.Application.DTOs.AppEnvironment;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAppEnvironmentService
{
    /// <summary>The caller's own environments plus the global (super-admin-seeded) catalog.</summary>
    Task<Result<List<AppEnvironmentResponse>>> ListAsync();

    /// <summary>Super admin creates a GLOBAL environment; any other admin creates one owned by
    /// their own tenant — mirrors RoleService.CreateAsync's TenantStamp.OwnerFor split.</summary>
    Task<Result<AppEnvironmentResponse>> CreateAsync(CreateAppEnvironmentRequest request);

    Task<Result<AppEnvironmentResponse>> UpdateAsync(int id, UpdateAppEnvironmentRequest request);

    Task<Result> DeleteAsync(int id);
}
