using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.AppEnvironment;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// AppEnvironment mirrors Role's own-plus-global pattern: a super admin manages the global catalog
/// ("default", "prod", "staging", "testing", seeded by AdminSeeder), and a tenant can layer its own
/// custom environments on top without ever touching another tenant's or the global rows.
/// </summary>
public class AppEnvironmentServiceTests
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

    private static int SeedGlobalEnvironment(string dbName, string name = "prod")
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var env = new AppEnvironment { Name = name, OwnerId = null };
        db.AppEnvironments.Add(env);
        db.SaveChanges();
        return env.Id;
    }

    [Fact]
    public async Task ScopedAdmin_SeesGlobalAndOwnEnvironments()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalEnvironment(dbName, "prod");
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "my-custom-env" });
        var list = (await svc.ListAsync()).Data!;

        Assert.Contains(list, e => e.Name == "prod" && e.IsGlobal);
        Assert.Contains(list, e => e.Name == "my-custom-env" && !e.IsGlobal);
    }

    [Fact]
    public async Task ScopedAdmin_CannotSeeAnotherTenantsCustomEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var adminA = new FakeCurrentUser { IsAdmin = true, TenantId = tenantA };
        await new AppEnvironmentService(new UnitOfWork(BuildContext(adminA, dbName)), adminA)
            .CreateAsync(new CreateAppEnvironmentRequest { Name = "tenant-a-only" });

        var adminB = new FakeCurrentUser { IsAdmin = true, TenantId = tenantB };
        var listB = (await new AppEnvironmentService(new UnitOfWork(BuildContext(adminB, dbName)), adminB)
            .ListAsync()).Data!;

        Assert.DoesNotContain(listB, e => e.Name == "tenant-a-only");
    }

    [Fact]
    public async Task ScopedAdmin_CannotRenameGlobalEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var envId = SeedGlobalEnvironment(dbName);
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = Guid.NewGuid() };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);

        var result = await svc.UpdateAsync(envId, new UpdateAppEnvironmentRequest { Name = "renamed" });

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task ScopedAdmin_CanRenameOwnEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        var created = (await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "old-name" })).Data!;

        var result = await svc.UpdateAsync(created.Id, new UpdateAppEnvironmentRequest { Name = "new-name" });

        Assert.True(result.IsSuccess);
        Assert.Equal("new-name", result.Data!.Name);
    }

    [Fact]
    public async Task SuperAdmin_CanRenameGlobalEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var envId = SeedGlobalEnvironment(dbName);
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(superAdmin, dbName)), superAdmin);

        var result = await svc.UpdateAsync(envId, new UpdateAppEnvironmentRequest { Name = "renamed" });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameForSameOwner_Conflicts()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "qa" });

        var result = await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "qa" });

        Assert.True(result.IsConflict);
    }

    [Fact]
    public async Task DeleteAsync_InUse_Conflicts()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new AppEnvironmentService(new UnitOfWork(BuildContext(admin, dbName)), admin);
        var env = (await svc.CreateAsync(new CreateAppEnvironmentRequest { Name = "used-env" })).Data!;

        using (var seed = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "p", Name = "P", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
            seed.Projects.Add(project);
            seed.SaveChanges();
            seed.ProjectAppUrls.Add(new ProjectAppUrl
            {
                ProjectId = project.Id, AppEnvironmentId = env.Id, Url = "https://x.test", OwnerId = tenant
            });
            seed.SaveChanges();
        }

        var result = await svc.DeleteAsync(env.Id);

        Assert.True(result.IsConflict);
    }
}
