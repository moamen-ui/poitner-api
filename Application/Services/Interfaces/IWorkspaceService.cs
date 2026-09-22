using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-03b: read and rename the caller's own workspace row (<c>workspaces.name</c>) — a workspace's
/// own name, distinct from the admin's DisplayName. Mirrors ICommentFieldService's ownership
/// guards: super admins and quick-access users have no workspace of their own.
/// </summary>
public interface IWorkspaceService
{
    /// <summary>GET /api/admin/workspace — id, own name, placeholder flag.</summary>
    Task<Result<WorkspaceResponse>> GetAsync();

    /// <summary>
    /// PUT /api/admin/workspace/name — renames the caller's workspace (1-120 chars after trim, no
    /// control characters). Setting the name back to the DB-03 placeholder is allowed.
    /// </summary>
    Task<Result<WorkspaceResponse>> RenameAsync(UpdateWorkspaceNameRequest request);
}
