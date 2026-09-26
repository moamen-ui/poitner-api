using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-19 (WS-NEW): signed-in "+ New workspace". Creates a workspace for the caller through the
/// same core as self-signup (<c>AuthService.RegisterAdminAsync</c>), with approval and a
/// per-identity cap driven by two plan levers governed by the plan of the caller's CURRENT
/// workspace. Registered by Scrutor (name ends in "Service").
/// </summary>
public interface IWorkspaceCreationService
{
    /// <summary>
    /// DB-19 §3.4. Validates the name (rename rules), runs the §3.3 gate, resolves the levers
    /// directly (never via <c>CheckCountAsync</c> — D19.5), then in one transaction: row-locks the
    /// identity, counts owned (§3.2), creates the workspace + the caller's admin membership (state
    /// per <c>NewWorkspaceRequiresApproval</c>), and writes exactly one
    /// <c>workspace.created</c> audit row with <c>source = "signed_in"</c>.
    /// </summary>
    Task<Result<CreateWorkspaceResponse>> CreateForCurrentIdentityAsync(
        CreateWorkspaceRequest request
    );

    /// <summary>
    /// DB-19 §3.5: GET /api/me/workspaces/allowance — <c>{ owned, max, requiresApproval, canCreate }</c>;
    /// <c>canCreate</c> = §3.3 passes AND (max == -1 || owned &lt; max). Never refuses: a caller who
    /// fails the gate gets <c>canCreate = false</c>.
    /// </summary>
    Task<Result<WorkspaceAllowanceResponse>> GetAllowanceAsync();
}
