namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-18 §3.4/§3.8. Body of the anonymous POST /api/auth/workspace-deletion/confirm.</summary>
public class ConfirmWorkspaceDeletionRequest
{
    public string Token { get; set; } = string.Empty;

    /// <summary>Null/empty for a passwordless identity (name-only confirmation).</summary>
    public string? Password { get; set; }

    /// <summary>Must equal the workspace's own name (normalised comparison — DB-18 §3.4).</summary>
    public string WorkspaceName { get; set; } = string.Empty;
}
