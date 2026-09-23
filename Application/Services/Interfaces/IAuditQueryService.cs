using Pointer.Application.DTOs.Audit;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-12 §3.8a — the audit read API. <see cref="ListForWorkspaceAsync"/> is the workspace admin's
/// own Security log (a super admin is Forbidden there and uses <see cref="ListAllAsync"/>).
/// </summary>
public interface IAuditQueryService
{
    Task<Result<PagedData<AuditEventDto>>> ListForWorkspaceAsync(AuditQuery q);

    /// <summary>Super-admin view across all workspaces, including operator-level (owner_id NULL) rows.</summary>
    Task<Result<PagedData<AuditEventDto>>> ListAllAsync(AuditQuery q);
}
