namespace Pointer.Application.DTOs.Invite;

/// <summary>Body for POST /api/admin/invites. All fields optional.</summary>
public class CreateInviteRequest
{
    /// <summary>Optional pinned non-admin role. Null = invitee picks a tenant/global role on accept.</summary>
    public int? RoleId { get; set; }

    /// <summary>Optional email lock. Null = anyone with the link may accept.</summary>
    public string? Email { get; set; }

    /// <summary>TTL in days. Null/&lt;=0 = default (7 days).</summary>
    public int? ExpiresInDays { get; set; }

    /// <summary>Accept cap. Null = unlimited within the TTL.</summary>
    public int? MaxUses { get; set; }
}
