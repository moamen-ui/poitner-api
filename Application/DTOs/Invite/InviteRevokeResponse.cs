namespace Pointer.Application.DTOs.Invite;

/// <summary>DB-11c §3.6 — result of revoking an invite. When the invite was already accepted, the
/// dashboard offers "also disable these members" from <see cref="Invitees"/>.</summary>
public class InviteRevokeResponse
{
    public int InviteId { get; set; }

    /// <summary>Live memberships this invite created (workspace_memberships.invite_id). Empty when the
    /// invite was never accepted. The dashboard offers "also disable these members".</summary>
    public List<InviteeMembership> Invitees { get; set; } = new();
}

public class InviteeMembership
{
    public int UserId { get; set; }
    public Guid PublicId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
