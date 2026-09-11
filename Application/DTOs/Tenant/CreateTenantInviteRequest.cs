namespace Pointer.Application.DTOs.Tenant;

/// <summary>
/// A super admin's invitation for someone to create a workspace. The invitee sets their own
/// password from the emailed link; nobody else ever sees it — which is why this, not
/// <see cref="CreateTenantRequest"/>, is the primary way workspaces are created.
/// </summary>
public class CreateTenantInviteRequest
{
    /// <summary>Required and locked: only this address can accept the link.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Workspace name, pre-filled on the accept form. The invitee may change it.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Plan the workspace starts on, applied at acceptance. Null = Free.</summary>
    public int? PlanId { get; set; }

    /// <summary>Link lifetime, 1–30 days. Null = the 7-day default.</summary>
    public int? ExpiresInDays { get; set; }
}
