namespace Pointer.Domain.Entity;

/// <summary>
/// A tenant invite link/code. An admin generates one; whoever opens the link
/// (<c>{app}/join?code=…</c>) can create an account that is <b>pre-authorized</b> (skips the
/// approval queue) and <b>pre-scoped</b> to this tenant (and optionally a role).
///
/// <see cref="OwnerId"/> is the tenant isolation boundary and is non-null for every invite that
/// joins an EXISTING tenant (strict-own query filter). The one exception: a super admin may create
/// a null-owner invite whose accept mints a brand-new self-owned tenant instead of joining one —
/// see InviteService.CreateAsync's CreateNewWorkspace branch and AcceptAsync's null-owner branch.
/// The <see cref="Code"/> is an unguessable, URL-safe random token looked up as a DB row (not signed).
/// A revoked (<see cref="RevokedAt"/>), expired (<see cref="ExpiresAt"/>), or used-up
/// (<see cref="Uses"/> ≥ <see cref="MaxUses"/>) invite can no longer be accepted.
/// </summary>
public class Invite : BaseEntity
{
    /// <summary>
    /// Tenant this invite joins. Null only for a super-admin-created "new workspace" invite, whose
    /// accept mints a brand-new self-owned tenant rather than joining an existing one.
    /// </summary>
    public Guid? OwnerId { get; set; }

    /// <summary>Unguessable, URL-safe token; unique index. Looked up as a DB row (not signed).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Optional pinned non-admin role; null = invitee picks a tenant/global role on accept.</summary>
    public int? RoleId { get; set; }

    /// <summary>
    /// Target project for a quick-access invite (see Role.QuickAccess) — used only to know which
    /// project's AppUrl to send the invitee to. Not an access grant: tenant membership already
    /// grants access to every project in the tenant.
    /// </summary>
    public int? ProjectId { get; set; }

    /// <summary>Optional email lock: only this (normalized) email may accept. Null = anyone with the link.</summary>
    public string? Email { get; set; }

    /// <summary>Required TTL. A past value means the invite can no longer be accepted.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>null = unlimited within the TTL; else the accept cap.</summary>
    public int? MaxUses { get; set; }

    /// <summary>Incremented per successful accept.</summary>
    public int Uses { get; set; }

    /// <summary>Soft-revoke timestamp. Non-null = revoked; the invite can no longer be accepted.</summary>
    public DateTime? RevokedAt { get; set; }
}
