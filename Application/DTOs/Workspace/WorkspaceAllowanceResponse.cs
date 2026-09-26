namespace Pointer.Application.DTOs.Workspace;

/// <summary>
/// DB-19 §3.5: GET /api/me/workspaces/allowance 200 body. <see cref="Max"/> is the resolved
/// MaxOwnedWorkspaces lever (-1 = unlimited); <see cref="CanCreate"/> is true when the caller
/// passes the §3.3 gate AND (max == -1 || owned &lt; max).
/// </summary>
public class WorkspaceAllowanceResponse
{
    public int Owned { get; set; }
    public int Max { get; set; }
    public bool RequiresApproval { get; set; }
    public bool CanCreate { get; set; }
}
