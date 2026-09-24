using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// #1 — Role-delete reassignment guard. Deleting a role that still has users requires a reassignment
/// target; the target must be resolved with an ownership/escalation guard so a scoped admin cannot
/// use the delete flow to move users onto a GrantsAdmin / IsSuperAdmin / global / cross-tenant role.
/// </summary>
public class RoleServiceDeleteTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static RoleService BuildService(ICurrentUser user, AppDbContext db)
    {
        var uow = new UnitOfWork(db);
        return new(uow, user, new MembershipService(uow));
    }

    // Seeds: a tenant-owned role to delete (with one assigned user), plus a set of candidate
    // reassignment targets (own non-admin, own admin, global non-admin, other-tenant non-admin).
    private sealed record Seeded(
        Guid Tenant,
        int DeleteRoleId,
        int OwnNonAdmin,
        int OwnAdmin,
        int Global,
        int OtherTenant,
        int SuperAdminRole
    );

    private static Seeded Seed(string dbName)
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var deleteRole = new Role
        {
            Name = "ToDelete",
            OwnerId = tenant,
            IsActive = true,
        };
        var ownNonAdmin = new Role
        {
            Name = "OwnDev",
            OwnerId = tenant,
            IsActive = true,
            GrantsAdmin = false,
        };
        var ownAdmin = new Role
        {
            Name = "OwnAdmin",
            OwnerId = tenant,
            IsActive = true,
            GrantsAdmin = true,
        };
        var global = new Role
        {
            Name = "GlobalViewer",
            OwnerId = null,
            IsActive = true,
            GrantsAdmin = false,
        };
        var otherTenantRole = new Role
        {
            Name = "OtherDev",
            OwnerId = otherTenant,
            IsActive = true,
            GrantsAdmin = false,
        };
        var superAdminRole = new Role
        {
            Name = "SA",
            OwnerId = null,
            IsActive = true,
            IsSuperAdmin = true,
        };
        seed.Roles.AddRange(
            deleteRole,
            ownNonAdmin,
            ownAdmin,
            global,
            otherTenantRole,
            superAdminRole
        );
        seed.SaveChanges();

        // One user assigned to the role being deleted → reassignment is required.
        var member = new User
        {
            Email = "member@a.com",
            PasswordHash = "x",
            DisplayName = "Member",
            RoleId = deleteRole.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = tenant,
        };
        seed.Users.Add(member);
        seed.SaveChanges();
        TestSeed.Join(seed, member, tenant, deleteRole);

        return new Seeded(
            tenant,
            deleteRole.Id,
            ownNonAdmin.Id,
            ownAdmin.Id,
            global.Id,
            otherTenantRole.Id,
            superAdminRole.Id
        );
    }

    [Fact]
    public async Task ScopedAdmin_CannotReassignTo_AdminGrantingRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = s.Tenant,
            IsAdmin = true,
        };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OwnAdmin);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        // The role was NOT deleted and the user was NOT reassigned.
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        Assert.Equal(
            s.DeleteRoleId,
            db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId
        );
    }

    [Fact]
    public async Task ScopedAdmin_CannotReassignTo_GlobalRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = s.Tenant,
            IsAdmin = true,
        };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.Global);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsNotFound); // null-owner role is not reachable to a scoped admin
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
    }

    [Fact]
    public async Task ScopedAdmin_CannotReassignTo_OtherTenantRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = s.Tenant,
            IsAdmin = true,
        };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OtherTenant);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsNotFound);
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
    }

    [Fact]
    public async Task ScopedAdmin_CanReassignTo_OwnNonAdminRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = s.Tenant,
            IsAdmin = true,
        };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OwnNonAdmin);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data!.ReassignedUsers);
        Assert.NotNull(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        // DB-11a: reassignment happens on the MEMBERSHIP, not users.role_id.
        var memberUserId = db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").Id;
        Assert.Equal(
            s.OwnNonAdmin,
            db.Set<Pointer.Domain.Entity.WorkspaceMembership>()
                .IgnoreQueryFilters()
                .Single(m => m.UserId == memberUserId && m.LeftAt == null)
                .RoleId
        );
    }

    [Fact]
    public async Task SuperAdmin_CanReassignTo_AdminGrantingRole()
    {
        // A super admin is exempt from the escalation guard (mirrors the UpdateAsync bypass).
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = BuildContext(superAdmin, dbName);
        var svc = BuildService(superAdmin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OwnAdmin);

        Assert.True(result.IsSuccess);
        var memberUserId = db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").Id;
        Assert.Equal(
            s.OwnAdmin,
            db.Set<Pointer.Domain.Entity.WorkspaceMembership>()
                .IgnoreQueryFilters()
                .Single(m => m.UserId == memberUserId && m.LeftAt == null)
                .RoleId
        );
    }

    /// <summary>DB-11f F1: not even a super admin may reassign a membership onto another
    /// workspace's role — the F1 guard is checked before the caller-scoped escalation guard, which
    /// exempts super admins entirely.</summary>
    [Fact]
    public async Task SuperAdmin_CannotReassignTo_OtherTenantRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = BuildContext(superAdmin, dbName);
        var svc = BuildService(superAdmin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OtherTenant);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        Assert.Equal(
            s.DeleteRoleId,
            db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId
        );
    }

    /// <summary>DB-11f F1: not even a super admin may reassign a membership onto the platform
    /// (super-admin) role — that would make the member's session in that workspace carry
    /// is_super_admin=true (cross-review Opus MEDIUM, the P11 finding).</summary>
    [Fact]
    public async Task SuperAdmin_CannotReassignTo_SuperAdminRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = BuildContext(superAdmin, dbName);
        var svc = BuildService(superAdmin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.SuperAdminRole);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        Assert.Equal(
            s.DeleteRoleId,
            db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId
        );
    }
}
