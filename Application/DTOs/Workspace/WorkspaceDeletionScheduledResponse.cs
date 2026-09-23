namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-18 §3.4/§3.8. Response of POST /api/auth/workspace-deletion/confirm.</summary>
public class WorkspaceDeletionScheduledResponse
{
    public Guid WorkspaceId { get; set; }
    public DateTime DeletionScheduledFor { get; set; }
}
