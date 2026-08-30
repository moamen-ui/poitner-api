using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Role;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// A scoped tenant can flip a GLOBAL, non-system role's active status for their OWN workspace only
/// (e.g. disabling the seeded "Tester" role) without touching the shared row every other tenant
/// reads — recorded via RoleTenantOverride, never a write to Role itself. Renaming/reconfiguring a
/// global role, or touching a system role at all, stays fully out of reach for a scoped tenant.
/// </summary>
public class RoleServiceGlobalOverrideTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static int SeedGlobalRole(string dbName, string name = "Tester")
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var role = new Role { Name = name, OwnerId = null, IsActive = true, IsSystem = false };
        db.Roles.Add(role);
        db.SaveChanges();
        return role.Id;
    }

    [Fact]
    public async Task ScopedAdmin_CanDisable_GlobalRole_ForOwnTenantOnly()
    {
        var dbName = Guid.NewGuid().ToString();
        var roleId = SeedGlobalRole(dbName);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var adminA = new FakeCurrentUser { IsAdmin = true, TenantId = tenantA };
        var result = await new RoleService(new UnitOfWork(BuildContext(adminA, dbName)), adminA)
            .UpdateAsync(roleId, new UpdateRoleRequest { IsActive = false });

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.IsActive);

        // Tenant A now sees it disabled...
        var adminAView = (await new RoleService(new UnitOfWork(BuildContext(adminA, dbName)), adminA)
            .ListAsync()).Data!.Single(r => r.Id == roleId);
        Assert.False(adminAView.IsActive);

        // ...but tenant B still sees the global default (untouched).
        var adminB = new FakeCurrentUser { IsAdmin = true, TenantId = tenantB };
        var adminBView = (await new RoleService(new UnitOfWork(BuildContext(adminB, dbName)), adminB)
            .ListAsync()).Data!.Single(r => r.Id == roleId);
        Assert.True(adminBView.IsActive);

        // The shared row itself was never touched.
        using var raw = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        Assert.True(raw.Roles.IgnoreQueryFilters().Single(r => r.Id == roleId).IsActive);
    }

    [Fact]
    public async Task ScopedAdmin_ToggleIsIdempotent_UpsertsSameOverrideRow()
    {
        var dbName = Guid.NewGuid().ToString();
        var roleId = SeedGlobalRole(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };

        var off = await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .UpdateAsync(roleId, new UpdateRoleRequest { IsActive = false });
        Assert.True(off.IsSuccess);
        var on = await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .UpdateAsync(roleId, new UpdateRoleRequest { IsActive = true });
        Assert.True(on.IsSuccess);
        Assert.True(on.Data!.IsActive);

        using var raw = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        // Exactly one override row for (role, tenant) — flipped in place, not duplicated.
        Assert.Single(raw.Set<RoleTenantOverride>().IgnoreQueryFilters()
            .Where(o => o.RoleId == roleId && o.OwnerId == tenant));
    }

    [Fact]
    public async Task ScopedAdmin_CannotRename_GlobalRole_ViaToggleEndpoint()
    {
        var dbName = Guid.NewGuid().ToString();
        var roleId = SeedGlobalRole(dbName);
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = Guid.NewGuid() };

        var result = await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .UpdateAsync(roleId, new UpdateRoleRequest { Name = "Renamed" });

        Assert.False(result.IsSuccess);
        using var raw = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        Assert.Equal("Tester", raw.Roles.IgnoreQueryFilters().Single(r => r.Id == roleId).Name);
    }

    [Fact]
    public async Task ScopedAdmin_CannotGrantAdmin_ViaGlobalRoleToggle()
    {
        var dbName = Guid.NewGuid().ToString();
        var roleId = SeedGlobalRole(dbName);
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = Guid.NewGuid() };

        var result = await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .UpdateAsync(roleId, new UpdateRoleRequest { GrantsAdmin = true });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_TogglingGlobalRole_EditsTheRealRow_NoOverrideCreated()
    {
        var dbName = Guid.NewGuid().ToString();
        var roleId = SeedGlobalRole(dbName);
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };

        var result = await new RoleService(new UnitOfWork(BuildContext(superAdmin, dbName)), superAdmin)
            .UpdateAsync(roleId, new UpdateRoleRequest { IsActive = false });

        Assert.True(result.IsSuccess);
        using var raw = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        Assert.False(raw.Roles.IgnoreQueryFilters().Single(r => r.Id == roleId).IsActive);
        Assert.Empty(raw.Set<RoleTenantOverride>().IgnoreQueryFilters().Where(o => o.RoleId == roleId));
    }

    [Fact]
    public async Task GlobalNonSystemRole_CanToggleActive_True_ButCanManage_False_ForScopedAdmin()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalRole(dbName);
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = Guid.NewGuid() };

        var role = (await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .ListAsync()).Data!.Single(r => r.Name == "Tester");

        Assert.False(role.CanManage);
        Assert.True(role.CanToggleActive);
    }

    [Fact]
    public async Task SystemRole_CanToggleActive_False_ForAnyone()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Roles.Add(new Role { Name = "Admin", IsSystem = true, IsSuperAdmin = true, IsActive = true });
            seed.SaveChanges();
        }
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = Guid.NewGuid() };

        var role = (await new RoleService(new UnitOfWork(BuildContext(admin, dbName)), admin)
            .ListAsync()).Data!.Single(r => r.Name == "Admin");

        Assert.False(role.CanManage);
        Assert.False(role.CanToggleActive);
    }
}
