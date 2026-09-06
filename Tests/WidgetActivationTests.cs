using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// ProjectService.CheckWidgetActiveAsync — the anonymous, pre-auth check the widget calls at boot
/// (before rendering anything) to decide whether it should show up on this page at all. Two
/// independent gates: the project must not be fully disabled, and if the page's origin matches a
/// configured ProjectAppUrl row, that row's IsActive must be true. An origin with no configured
/// row is NOT blocked — most projects never configure "other environments" at all.
/// </summary>
public class WidgetActivationTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static void SeedGlobalDefaultEnvironment(string dbName)
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        db.AppEnvironments.Add(new AppEnvironment { Name = "default", OwnerId = null });
        db.SaveChanges();
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_ProjectFullyInactive_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;
        await svc.UpdateAsync(created.Id, new UpdateProjectRequest
        {
            IsActiveLocal = false,
            IsActiveStaging = false,
            IsActiveProduction = false,
        });

        var result = await svc.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_NoOrigin_ActiveProject_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var result = await svc.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_OriginNotConfigured_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        // No ProjectAppUrl row configured for this origin at all — must not be blocked.
        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000");
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_OriginMatchesDeactivatedRow_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        int localEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var local = new AppEnvironment { Name = "local", OwnerId = null };
            db.AppEnvironments.Add(local);
            db.SaveChanges();
            localEnvId = local.Id;
        }

        await svc.SetAppUrlAsync(created.Id, localEnvId,
            new SetProjectAppUrlRequest { Url = "http://localhost:3000", IsActive = false });

        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000/");
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_OriginMatchesActiveRow_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        int localEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var local = new AppEnvironment { Name = "local", OwnerId = null };
            db.AppEnvironments.Add(local);
            db.SaveChanges();
            localEnvId = local.Id;
        }

        await svc.SetAppUrlAsync(created.Id, localEnvId,
            new SetProjectAppUrlRequest { Url = "http://localhost:3000", IsActive = true });

        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000");
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_UnknownKey_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = Guid.NewGuid() };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());

        var result = await svc.CheckWidgetActiveAsync("does-not-exist", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_AmbiguousKeyAcrossTenants_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenantA = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = Guid.NewGuid() };
        var tenantB = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = Guid.NewGuid() };
        var svcA = new ProjectService(new UnitOfWork(BuildContext(tenantA, dbName)), tenantA, new PassThroughEntitlements());
        var svcB = new ProjectService(new UnitOfWork(BuildContext(tenantB, dbName)), tenantB, new PassThroughEntitlements());
        await svcA.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Tenant A Site" });
        await svcB.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Tenant B Site" });

        // Same key exists under two different tenants — must refuse rather than pick one arbitrarily.
        var result = await svcA.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }
}
