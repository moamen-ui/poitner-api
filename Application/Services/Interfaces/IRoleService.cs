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

    /// <summary>Active, NON-admin roles only — safe for anonymous signup dropdowns.</summary>
    Task<Result<List<PublicRoleResponse>>> ListPublicAsync();
}
