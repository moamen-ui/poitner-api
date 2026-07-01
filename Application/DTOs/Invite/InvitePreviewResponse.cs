namespace Pointer.Application.DTOs.Invite;

/// <summary>
/// SAFE anonymous preview for GET /api/invites/{code}. Exposes ONLY a display name and (optional)
/// role name — NEVER the tenant GUID, the invite id, or any secret. Invalid/expired/revoked/used-up
/// codes return NotFound instead of this, so codes can't be probed for validity.
/// </summary>
public class InvitePreviewResponse
{
    /// <summary>The owning admin's display name (used as the workspace/tenant label).</summary>
    public string WorkspaceName { get; set; } = string.Empty;

    /// <summary>The pinned role's name, if the invite pins one. Null = invitee picks a role.</summary>
    public string? RoleName { get; set; }

    /// <summary>True if the invite is email-locked (the accept form should disable the email field).</summary>
    /// <remarks>
    /// L1 fix: the raw locked email is NOT returned to anonymous callers. The server enforces the
    /// email lock on accept; the client renders a "email is locked" hint from this bool only.
    /// </remarks>
    public bool EmailLocked { get; set; }
}
