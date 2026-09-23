namespace Pointer.Application.DTOs.Auth;

public class LoginResponse
{
    /// <summary>
    /// Always present so the web component can branch on it for ALL outcomes:
    /// "ok" | "choose-workspace" | "mfa_required" | "pending" | "rejected" | "disabled" |
    /// "no-workspace" | "locked". On non-"ok" results, Token and User are null — except
    /// "choose-workspace", whose Token is a short-lived selection token (see
    /// <see cref="Workspaces"/>) usable only against POST /api/auth/switch-workspace, and
    /// "mfa_required" (R5-61), whose Token is a short-lived scoped token usable only against
    /// POST /api/auth/mfa/verify.
    /// </summary>
    public string Status { get; set; } = string.Empty;
    public string? Token { get; set; }
    public MeResponse? User { get; set; }

    /// <summary>Present only when Status == "choose-workspace": the caller's approved, active memberships.</summary>
    public List<WorkspaceChoice>? Workspaces { get; set; }
}
