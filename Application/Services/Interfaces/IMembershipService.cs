using Pointer.Application.Response;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-11a: the shared routine for identity creation and "join". Every path that creates or looks up a
/// <see cref="User"/> for a workspace goes through here instead of constructing rows itself. All
/// methods run <c>IgnoreQueryFilters()</c> internally — they run on anonymous paths (login, invite
/// accept, register) or are scoped by an explicit workspace id, never by the ambient tenant filter.
/// </summary>
public interface IMembershipService
{
    /// <summary>Live identity by e-mail (normalises internally — callers may pass raw input), with Role loaded.</summary>
    Task<User?> FindIdentityByEmailAsync(string? email);

    /// <summary>Live identity by public id, with Role loaded.</summary>
    Task<User?> FindIdentityByPublicIdAsync(Guid publicId);

    /// <summary>The live membership (LeftAt == null, DeletedAt == null) for (userId, workspaceId), with Role loaded.</summary>
    Task<WorkspaceMembership?> GetMembershipAsync(int userId, Guid workspaceId);

    /// <summary>Every live membership for an identity, in workspaces that still exist, with Role loaded.</summary>
    Task<List<WorkspaceMembership>> ListForIdentityAsync(int userId);

    /// <summary>Every non-deleted membership of a workspace (live and ended), with User and Role loaded.</summary>
    IQueryable<WorkspaceMembership> InWorkspace(Guid workspaceId);

    /// <summary>The workspace's current live Workspace Admin membership, if any.</summary>
    Task<WorkspaceMembership?> CurrentAdminAsync(Guid workspaceId);

    /// <summary>
    /// Builds a new membership row (JoinedAt = UtcNow, SecurityStamp = NewGuid) and stages it via
    /// <c>AddAsync</c>. Does NOT call SaveChangesAsync — the caller's existing unit of work does.
    /// </summary>
    Task<WorkspaceMembership> JoinAsync(
        User identity,
        Guid workspaceId,
        Role role,
        ApprovalStatus status,
        bool isActive,
        int? inviteId
    );

    /// <summary>
    /// Builds (does not persist) a brand-new identity. PublicId = NewGuid; Email is normalised. The
    /// legacy OwnerId/RoleId are dual-written (first workspace / first role) and never read after
    /// DB-11a. The caller stages it via its own AddAsync.
    /// </summary>
    User NewIdentity(
        string email,
        string passwordHash,
        string displayName,
        Role firstRole,
        Guid firstWorkspaceId,
        bool passwordlessOnly = false
    );

    /// <summary>
    /// S-13 (DB-11c). Returns the workspaces (id, name) in which any of <paramref name="membershipIds"/>
    /// is the ONLY live Workspace Admin membership. Empty = safe. Applies to every actor, super admins
    /// included; the way out is TransferOwnershipAsync. Tenant suspension by a super admin
    /// (<c>TenantService.SetStatusAsync</c>) is exempt by design (D10): a disabled admin is
    /// recoverable, an admin-less workspace is not.
    /// </summary>
    Task<List<(Guid WorkspaceId, string Name)>> SoleAdminWorkspacesAsync(IEnumerable<int> membershipIds);

    /// <summary>The one Conflict shape for the S-13 guard, everywhere it's enforced.</summary>
    Result SoleAdminConflict(IEnumerable<(Guid WorkspaceId, string Name)> workspaces);

    /// <summary>
    /// Ends a membership (DB-11c §3.3): stamps <c>LeftAt</c>/<c>LeftReason</c>, flips <c>IsActive</c>
    /// false, rotates the membership stamp (kills live JWTs for this workspace), and revokes this
    /// workspace's API keys / quick-access links / pending device-login approvals for the identity.
    /// Does NOT call SaveChangesAsync — the caller's unit of work / transaction does.
    /// </summary>
    Task EndAsync(WorkspaceMembership m, MembershipEndReason reason, Guid actor);
}
