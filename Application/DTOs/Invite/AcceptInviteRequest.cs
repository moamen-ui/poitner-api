namespace Pointer.Application.DTOs.Invite;

/// <summary>Body for POST /api/auth/register-invite (anonymous). Reuses LoginResponse on success.</summary>
public class AcceptInviteRequest
{
    public string Code { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Required only when the invite does NOT pin a role; must be a non-admin tenant/global role.</summary>
    public int? RoleId { get; set; }
}
