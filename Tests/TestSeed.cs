using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;

namespace Pointer.Tests;

/// <summary>
/// DB-11a: after the User query filter became membership-based, a test that seeds a tenant `User`
/// row (OwnerId = tenant) but calls a service that reads via <c>IMembershipService</c> needs a
/// matching <see cref="WorkspaceMembership"/> row too, or the identity is invisible under the
/// caller's tenant filter and every membership-based lookup (InWorkspace, GetMembershipAsync, …)
/// finds nothing. Use this everywhere a test seeds a tenant user and then calls AuthService,
/// UserService, TenantService, InviteService, RoleService, StatsService, SuggestionService,
/// NotificationService, DeviceLoginService or ApiKeyService.
/// </summary>
public static class TestSeed
{
    /// <summary>
    /// Adds a live membership for <paramref name="user"/> in <paramref name="workspaceId"/> (and a
    /// <see cref="Workspace"/> row for it, if one doesn't already exist) and saves. `role` is set as
    /// the scalar RoleId only (never the navigation) — a role fetched by an earlier, separate query
    /// is very often untracked, and attaching it to a to-be-saved graph makes EF try to re-insert an
    /// already-existing row.
    /// </summary>
    public static WorkspaceMembership Join(
        AppDbContext db,
        User user,
        Guid workspaceId,
        Role role,
        bool isActive = true,
        ApprovalStatus status = ApprovalStatus.Approved
    )
    {
        if (!db.Set<Workspace>().Any(w => w.Id == workspaceId))
        {
            db.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = workspaceId,
                        Name = "Test Workspace",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = workspaceId,
                    }
                );
        }

        var membership = new WorkspaceMembership
        {
            UserId = user.Id,
            OwnerId = workspaceId,
            RoleId = role.Id,
            IsActive = isActive,
            ApprovalStatus = status,
            SecurityStamp = Guid.NewGuid(),
            JoinedAt = DateTime.UtcNow,
        };
        db.Set<WorkspaceMembership>().Add(membership);
        db.SaveChanges();
        return membership;
    }
}
