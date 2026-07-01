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
        public Guid? TenantId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user);

    private static RoleService BuildService(ICurrentUser user, AppDbContext db) =>
        new(new UnitOfWork(db), user);

    // Seeds: a tenant-owned role to delete (with one assigned user), plus a set of candidate
    // reassignment targets (own non-admin, own admin, global non-admin, other-tenant non-admin).
    private sealed record Seeded(Guid Tenant, int DeleteRoleId, int OwnNonAdmin, int OwnAdmin, int Global, int OtherTenant);

    private static Seeded Seed(string dbName)
    {
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var deleteRole = new Role { Name = "ToDelete", OwnerId = tenant, IsActive = true };
        var ownNonAdmin = new Role { Name = "OwnDev", OwnerId = tenant, IsActive = true, GrantsAdmin = false };
        var ownAdmin = new Role { Name = "OwnAdmin", OwnerId = tenant, IsActive = true, GrantsAdmin = true };
        var global = new Role { Name = "GlobalViewer", OwnerId = null, IsActive = true, GrantsAdmin = false };
        var otherTenantRole = new Role { Name = "OtherDev", OwnerId = otherTenant, IsActive = true, GrantsAdmin = false };
        seed.Roles.AddRange(deleteRole, ownNonAdmin, ownAdmin, global, otherTenantRole);
        seed.SaveChanges();

        // One user assigned to the role being deleted → reassignment is required.
        seed.Users.Add(new User
        {
            Email = "member@a.com",
            PasswordHash = "x",
            DisplayName = "Member",
            RoleId = deleteRole.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = tenant
        });
        seed.SaveChanges();

        return new Seeded(tenant, deleteRole.Id, ownNonAdmin.Id, ownAdmin.Id, global.Id, otherTenantRole.Id);
    }

    [Fact]
    public async Task ScopedAdmin_CannotReassignTo_AdminGrantingRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = s.Tenant, IsAdmin = true };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OwnAdmin);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        // The role was NOT deleted and the user was NOT reassigned.
        Assert.Null(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        Assert.Equal(s.DeleteRoleId, db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId);
    }

    [Fact]
    public async Task ScopedAdmin_CannotReassignTo_GlobalRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = s.Tenant, IsAdmin = true };
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
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = s.Tenant, IsAdmin = true };
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
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = s.Tenant, IsAdmin = true };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.DeleteAsync(s.DeleteRoleId, s.OwnNonAdmin);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Data!.ReassignedUsers);
        Assert.NotNull(db.Roles.IgnoreQueryFilters().Single(r => r.Id == s.DeleteRoleId).DeletedAt);
        Assert.Equal(s.OwnNonAdmin, db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId);
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
        Assert.Equal(s.OwnAdmin, db.Users.IgnoreQueryFilters().Single(u => u.Email == "member@a.com").RoleId);
    }
}
