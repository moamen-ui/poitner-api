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
    /// Required when the caller is a super admin AND <see cref="CreateNewWorkspace"/> is false: the
    /// existing workspace (tenant OwnerId) this invite is for — super admins can only pin
    /// "Workspace Admin Deputy" onto an existing workspace via this field, never any other role.
    /// Ignored for a non-super-admin caller, who always invites into their own tenant.
    /// </summary>
    public Guid? TargetOwnerId { get; set; }

    /// <summary>
    /// Super-admin only. When true, this invite mints a brand-new, self-owned workspace on accept
    /// (the accepter becomes its "Workspace Admin", approved and active immediately) instead of
    /// joining an existing one — <see cref="TargetOwnerId"/> and <see cref="RoleId"/> are ignored.
    /// Mirrors TenantService.CreateAsync's direct-create path, deferred to the invitee via a link.
    /// </summary>
    public bool CreateNewWorkspace { get; set; }
}
