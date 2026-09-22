using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IMembershipService"/>
public class MembershipService(IUnitOfWork unitOfWork) : IMembershipService
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    public async Task<User?> FindIdentityByEmailAsync(string? email)
    {
        var normalized = EmailNormalizer.Normalize(email);
        if (normalized is null)
            return null;

        return await unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.DeletedAt == null && u.Email.ToLower() == normalized);
    }

    public Task<User?> FindIdentityByPublicIdAsync(Guid publicId) =>
        unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.PublicId == publicId && u.DeletedAt == null);

    public Task<WorkspaceMembership?> GetMembershipAsync(int userId, Guid workspaceId) =>
        unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Include(m => m.Role)
            .FirstOrDefaultAsync(m =>
                m.UserId == userId
                && m.OwnerId == workspaceId
                && m.LeftAt == null
                && m.DeletedAt == null
            );

    public Task<List<WorkspaceMembership>> ListForIdentityAsync(int userId)
    {
        var liveWorkspaceIds = unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w => w.DeletedAt == null)
            .Select(w => w.Id);

        return unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Include(m => m.Role)
            .Where(m =>
                m.UserId == userId
                && m.LeftAt == null
                && m.DeletedAt == null
                && liveWorkspaceIds.Contains(m.OwnerId)
            )
            .ToListAsync();
    }

    public IQueryable<WorkspaceMembership> InWorkspace(Guid workspaceId) =>
        unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Where(m => m.OwnerId == workspaceId && m.DeletedAt == null)
            .Include(m => m.User)
            .Include(m => m.Role);

    public Task<WorkspaceMembership?> CurrentAdminAsync(Guid workspaceId) =>
        InWorkspace(workspaceId)
            .FirstOrDefaultAsync(m => m.LeftAt == null && m.Role.Name == WorkspaceAdminRoleName);

    public async Task<WorkspaceMembership> JoinAsync(
        User identity,
        Guid workspaceId,
        Role role,
        ApprovalStatus status,
        bool isActive,
        int? inviteId
    )
    {
        // Role is intentionally NOT set as a navigation here: `role` frequently comes from an
        // AsNoTracking() lookup upstream, and attaching a detached entity with an already-existing
        // PK to a soon-to-be-saved graph makes EF try to INSERT it again (duplicate-key). RoleId (the
        // scalar FK) is all SaveChanges needs; callers that need `.Role` populated for a return value
        // set it explicitly AFTER their own SaveChangesAsync has run.
        var membership = new WorkspaceMembership
        {
            User = identity,
            UserId = identity.Id,
            OwnerId = workspaceId,
            RoleId = role.Id,
            ApprovalStatus = status,
            IsActive = isActive,
            SecurityStamp = Guid.NewGuid(),
            JoinedAt = DateTime.UtcNow,
            InviteId = inviteId,
        };
        await unitOfWork.Repository<WorkspaceMembership>().AddAsync(membership);
        return membership;
    }

    public User NewIdentity(
        string email,
        string passwordHash,
        string displayName,
        Role firstRole,
        Guid firstWorkspaceId,
        bool passwordlessOnly = false
    ) =>
        new()
        {
            PublicId = Guid.NewGuid(),
            Email = EmailNormalizer.NormalizeRequired(email),
            PasswordHash = passwordHash,
            DisplayName = displayName,
            // Legacy dual-write (RoleId only, no Role navigation — see the JoinAsync comment above).
            RoleId = firstRole.Id,
            OwnerId = firstWorkspaceId,
            PasswordlessOnly = passwordlessOnly,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
            SecurityStamp = Guid.NewGuid(),
        };
}
