namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-18 §3.4/§3.8. Response of POST /api/admin/workspace/deletion/request.</summary>
public class WorkspaceDeletionRequestResponse
{
    public DateTime ExpiresAt { get; set; }
}
