using Pointer.Application.DTOs.Role;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IRoleService
{
    Task<Result<RoleResponse>> CreateAsync(CreateRoleRequest request);
    Task<Result<List<RoleResponse>>> ListAsync();
    Task<Result<RoleResponse>> UpdateAsync(int id, UpdateRoleRequest request);

    /// <summary>Active, NON-admin roles only — safe for anonymous signup dropdowns.</summary>
    Task<Result<List<PublicRoleResponse>>> ListPublicAsync();
}
