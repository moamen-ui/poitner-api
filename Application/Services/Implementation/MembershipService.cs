using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Resources;
using Pointer.Application.Response;
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
            .Include(m => m.User)
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
            // Legacy dual-write, never read since DB-11f Part A (RoleId only, no Role navigation — see JoinAsync); removed by DB-11f Part B.
            RoleId = firstRole.Id,
            OwnerId = firstWorkspaceId,
            PasswordlessOnly = passwordlessOnly,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
            SecurityStamp = Guid.NewGuid(),
        };

    /// <inheritdoc />
    public Task<Guid?> HomeWorkspaceIdAsync(int userId) =>
        unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Guid?)m.OwnerId)
            .FirstOrDefaultAsync();

    /// <inheritdoc />
    public Task<Role?> PlatformRoleAsync(int userId) =>
        unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r =>
                r.IsSuperAdmin
                && unitOfWork
                    .Repository<User>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Any(u => u.Id == userId && u.RoleId == r.Id)
            )
            .FirstOrDefaultAsync();

    /// <inheritdoc />
    public async Task<int> CountLiveAdminsAsync(Guid workspaceId) =>
        await unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Include(m => m.Role)
            .CountAsync(m =>
                m.OwnerId == workspaceId
                && m.LeftAt == null
                && m.IsActive
                && m.ApprovalStatus == ApprovalStatus.Approved
                && m.Role.Name == WorkspaceAdminRoleName
            );

    public async Task<List<(Guid WorkspaceId, string Name)>> SoleAdminWorkspacesAsync(
        IEnumerable<int> membershipIds
    )
    {
        var ids = membershipIds.Distinct().ToList();
        var result = new List<(Guid WorkspaceId, string Name)>();
        if (ids.Count == 0)
            return result;

        // Only the candidates that are themselves a LIVE, ACTIVE, APPROVED Workspace Admin
        // membership can possibly be "the sole admin" — anything else (a regular member, an
        // already-ended membership, or one disabled/rejected — review finding #4, a disabled or
        // rejected admin cannot act, so it must not count as covering for the last one that can) is
        // safe.
        var candidates = await unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Include(m => m.Role)
            .Where(m =>
                ids.Contains(m.Id)
                && m.LeftAt == null
                && m.IsActive
                && m.ApprovalStatus == ApprovalStatus.Approved
                && m.Role.Name == WorkspaceAdminRoleName
            )
            .ToListAsync();

        foreach (var m in candidates)
        {
            var liveAdminCount = await CountLiveAdminsAsync(m.OwnerId);
            if (liveAdminCount != 1)
                continue;

            var name = await unitOfWork
                .Workspaces.IgnoreQueryFilters()
                .Where(w => w.Id == m.OwnerId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync();

            result.Add((m.OwnerId, name ?? Workspace.PlaceholderName));
        }

        return result;
    }

    public Result SoleAdminConflict(IEnumerable<(Guid WorkspaceId, string Name)> workspaces) =>
        Result.Conflict(
            string.Format(
                MessageKeys.User.SoleAdminBlocked,
                string.Join(", ", workspaces.Select(w => w.Name))
            )
        );

    public async Task EndAsync(WorkspaceMembership m, MembershipEndReason reason, Guid actor)
    {
        m.LeftAt = DateTime.UtcNow;
        m.LeftReason = reason;
        m.IsActive = false;
        m.SecurityStamp = Guid.NewGuid();
        unitOfWork.Repository<WorkspaceMembership>().Update(m);

        var userPublicId =
            m.User?.PublicId
            ?? await unitOfWork
                .Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .Where(u => u.Id == m.UserId)
                .Select(u => u.PublicId)
                .FirstAsync();

        var now = DateTime.UtcNow;

        var apiKeys = await unitOfWork
            .Repository<ApiKey>()
            .Query()
            .IgnoreQueryFilters()
            .Where(k => k.UserId == m.UserId && k.OwnerId == m.OwnerId && k.RevokedAt == null)
            .ToListAsync();
        foreach (var k in apiKeys)
        {
            k.RevokedAt = now;
            unitOfWork.Repository<ApiKey>().Update(k);
        }

        var links = await unitOfWork
            .Repository<QuickAccessLink>()
            .Query()
            .IgnoreQueryFilters()
            .Where(l => l.UserId == userPublicId && l.OwnerId == m.OwnerId && l.RevokedAt == null)
            .ToListAsync();
        foreach (var l in links)
        {
            l.RevokedAt = now;
            unitOfWork.Repository<QuickAccessLink>().Update(l);
        }

        // An approved-not-yet-consumed device code must not hand out a key later for a workspace the
        // identity no longer belongs to (or is disabled in).
        var devices = await unitOfWork
            .Repository<DeviceLogin>()
            .Query()
            .IgnoreQueryFilters()
            .Where(d =>
                d.UserId == userPublicId
                && d.OwnerId == m.OwnerId
                && d.Status == DeviceLoginStatus.Approved
            )
            .ToListAsync();
        foreach (var d in devices)
        {
            d.Status = DeviceLoginStatus.Denied;
            unitOfWork.Repository<DeviceLogin>().Update(d);
        }
    }
}
