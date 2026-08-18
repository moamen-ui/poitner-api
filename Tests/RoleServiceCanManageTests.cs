using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// RoleResponse.CanManage must mirror the guards in UpdateAsync/DeleteAsync exactly. It exists so a
/// dashboard never offers an action the API would refuse: a workspace admin could see global roles
/// (the Role query filter lets it) with a live actions menu whose Rename then 404'd.
/// </summary>
public class RoleServiceCanManageTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtherTenant = Guid.NewGuid();

    private static void Seed(string dbName)
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        db.AddRange(
            new Role { Name = "Admin", IsSystem = true, IsActive = true },                 // platform, immutable
            new Role { Name = "Developer", OwnerId = null, IsActive = true },              // global
            new Role { Name = "OwnPM", OwnerId = Tenant, IsActive = true },                // this tenant's
            new Role { Name = "TheirPM", OwnerId = OtherTenant, IsActive = true });        // someone else's
        db.SaveChanges();
    }

    private static async Task<Dictionary<string, bool>> ListCanManageAsync(ICurrentUser user, string dbName)
    {
        using var db = BuildContext(user, dbName);
        var result = await new RoleService(new UnitOfWork(db), user).ListAsync();
        Assert.True(result.IsSuccess);
        return result.Data!.ToDictionary(r => r.Name, r => r.CanManage);
    }

    [Fact]
    public async Task ScopedAdmin_canManage_onlyItsOwnNonSystemRoles()
    {
        var dbName = nameof(ScopedAdmin_canManage_onlyItsOwnNonSystemRoles);
        Seed(dbName);

        var canManage = await ListCanManageAsync(
            new FakeCurrentUser { IsAdmin = true, TenantId = Tenant }, dbName);

        Assert.True(canManage["OwnPM"]);
        // Visible but untouchable — these are the rows whose actions menu used to 404.
        Assert.False(canManage["Developer"]);
        Assert.False(canManage["Admin"]);
    }

    [Fact]
    public async Task SuperAdmin_canManage_everythingExceptSystemRoles()
    {
        var dbName = nameof(SuperAdmin_canManage_everythingExceptSystemRoles);
        Seed(dbName);

        var canManage = await ListCanManageAsync(
            new FakeCurrentUser { IsAdmin = true, IsSuperAdmin = true }, dbName);

        Assert.True(canManage["Developer"]);
        Assert.True(canManage["OwnPM"]);
        Assert.True(canManage["TheirPM"]);
        // Immutable for everyone: UpdateAsync/DeleteAsync answer 409 SystemImmutable.
        Assert.False(canManage["Admin"]);
    }

    [Fact]
    public async Task CanManage_agreesWithWhatUpdateActuallyAllows()
    {
        var dbName = nameof(CanManage_agreesWithWhatUpdateActuallyAllows);
        Seed(dbName);
        var user = new FakeCurrentUser { IsAdmin = true, TenantId = Tenant };

        using var db = BuildContext(user, dbName);
        var service = new RoleService(new UnitOfWork(db), user);
        var roles = (await service.ListAsync()).Data!;

        foreach (var role in roles)
        {
            var rename = await service.UpdateAsync(role.Id, new Application.DTOs.Role.UpdateRoleRequest
            {
                Name = role.Name + "-renamed",
            });
            Assert.Equal(role.CanManage, rename.IsSuccess);
        }
    }
}
