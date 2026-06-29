using Pointer.Application.DTOs.Role;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IRoleService
{
    Task<Result<RoleResponse>> CreateAsync(CreateRoleRequest request);
    Task<Result<List<RoleResponse>>> ListAsync();
    Task<Result<RoleResponse>> UpdateAsync(int id, UpdateRoleRequest request);

    /// <summary>
    /// Soft-deletes a role. If it has assigned users, they're reassigned to
    /// <paramref name="reassignToRoleId"/> first (required in that case).
    /// </summary>
    Task<Result<RoleDeleteResponse>> DeleteAsync(int id, int? reassignToRoleId);

    /// <summary>
    /// Active, NON-admin roles only — safe for anonymous signup dropdowns.
    /// When <paramref name="projectKey"/> is provided, also includes roles owned by
    /// the project's tenant (in addition to global roles). Falls back to global-only
    /// when the key is absent or the project cannot be resolved.
    /// </summary>
    Task<Result<List<PublicRoleResponse>>> ListPublicAsync(string? projectKey = null);
}
