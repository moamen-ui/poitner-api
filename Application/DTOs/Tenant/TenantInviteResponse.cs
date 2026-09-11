namespace Pointer.Application.DTOs.Tenant;

/// <summary>
/// A pending workspace invitation. There is no tenant row yet — the invitation itself *is* the
/// pending workspace, and accepting it is what creates one.
/// </summary>
public class TenantInviteResponse
{
    public int Id { get; set; }

    /// <summary>Always set: workspace invites are email-locked.</summary>
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public int? PlanId { get; set; }

    /// <summary>Resolved for display so the list does not need a second call.</summary>
    public string? PlanName { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The link to send. Returned on create and resend so it can be copied.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Whether the invitation email actually went out. Null on list rows, where it is unknown —
    /// a non-nullable false there would make the dashboard warn on every pending invitation.
    /// </summary>
    public bool? EmailSent { get; set; }
}
