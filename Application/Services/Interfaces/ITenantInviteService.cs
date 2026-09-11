using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Workspace invitations — the primary way a super admin onboards a tenant. Separate from
/// <see cref="IInviteService"/> so the Tenants surface can never act on a customer's own invites.
/// </summary>
public interface ITenantInviteService
{
    Task<Result<TenantInviteResponse>> CreateAsync(CreateTenantInviteRequest request);
    Task<Result<List<TenantInviteResponse>>> ListAsync();
    Task<Result<TenantInviteResponse>> ResendAsync(int id, bool rotate);
    Task<Result> RevokeAsync(int id);
}
