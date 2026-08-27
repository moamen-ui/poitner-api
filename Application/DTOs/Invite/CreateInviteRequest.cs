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

    /// <summary>
    /// Required when the caller is a super admin: the existing workspace (tenant OwnerId) this
    /// invite is for — super admins can no longer mint a self-owned workspace or pin any role other
    /// than "Workspace Admin Deputy" via this endpoint. Ignored for a non-super-admin caller, who
    /// always invites into their own tenant.
    /// </summary>
    public Guid? TargetOwnerId { get; set; }
}
