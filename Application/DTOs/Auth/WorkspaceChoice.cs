namespace Pointer.Application.DTOs.Auth;

/// <summary>
/// One workspace the caller may open a session in (DB-11b login picker / switch-workspace). Also
/// used by <see cref="MeResponse"/> to list every live, approved, active membership.
/// </summary>
public class WorkspaceChoice
{
    public Guid WorkspaceId { get; set; }

    /// <summary>
    /// The workspace's own name (<c>workspaces.name</c>). <see cref="Pointer.Domain.Entity.Workspace.PlaceholderName"/>
    /// is sent as-is — the client labels it "Unnamed workspace".
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string RoleName { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }

    /// <summary>True for the identity's home workspace — its earliest membership of any state
    /// (<c>IMembershipService.HomeWorkspaceIdAsync</c>, DB-11f; formerly users.owner_id).</summary>
    public bool IsHome { get; set; }
}
