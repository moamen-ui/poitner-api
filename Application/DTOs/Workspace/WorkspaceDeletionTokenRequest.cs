namespace Pointer.Application.DTOs.Workspace;

/// <summary>DB-18 §3.4/§3.8. Body of the anonymous POST /api/auth/workspace-deletion/preview and
/// /pause-instead — the token alone is the credential.</summary>
public class WorkspaceDeletionTokenRequest
{
    public string Token { get; set; } = string.Empty;
}
