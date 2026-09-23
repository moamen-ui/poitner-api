namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-18 §3.4. Response of the anonymous POST /api/auth/workspace-deletion/preview — counts
/// only, no content (R18).</summary>
public class WorkspaceDeletionPreviewResponse
{
    public Guid WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public int ProjectCount { get; set; }
    public int CommentCount { get; set; }
    public int MemberCount { get; set; }

    /// <summary>Count only (no names/e-mails) — <c>TenantService.IdentitiesDeletedWithWorkspace</c>.</summary>
    public int AccountsDeletedWithWorkspace { get; set; }

    /// <summary>True when the requester's own identity is among the accounts deleted with this workspace.</summary>
    public bool RequesterAccountDeleted { get; set; }

    /// <summary>True unless the requester's identity is <c>PasswordlessOnly</c>.</summary>
    public bool RequiresPassword { get; set; }
    public int GraceDays { get; set; }
    public DateTime WouldBeDeletedOn { get; set; }
    public bool IsPaused { get; set; }
}
