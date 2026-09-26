namespace Pointer.Application.DTOs.Workspace;

/// <summary>
/// DB-19 §3.4 step 6: POST /api/me/workspaces 200 body. <see cref="Status"/> is
/// <c>"active"</c> or <c>"pending_approval"</c> depending on the plan's
/// <c>NewWorkspaceRequiresApproval</c> lever.
/// </summary>
public class CreateWorkspaceResponse
{
    public Guid WorkspaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}
