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
}
