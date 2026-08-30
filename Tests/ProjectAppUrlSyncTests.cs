using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Extension;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Creating/updating a project with just "AppUrl" (the only concept the browser extension and the
/// old dashboard dialog know about) transparently lands on the "default" AppEnvironment — so
/// ExtensionService.FindProjectForOriginAsync, which now reads ProjectAppUrl, keeps working for
/// every existing caller without them ever knowing environments exist.
/// </summary>
public class ProjectAppUrlSyncTests
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

    private static void SeedGlobalDefaultEnvironment(string dbName)
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        db.AppEnvironments.Add(new AppEnvironment { Name = "default", OwnerId = null });
        db.SaveChanges();
    }

    [Fact]
    public async Task CreateAsync_WithAppUrl_SyncsProjectAppUrlOnDefaultEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());

        var created = await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site", AppUrl = "https://site.example.com" });
        Assert.True(created.IsSuccess);

        using var check = BuildContext(admin, dbName);
        var url = check.ProjectAppUrls.Include(u => u.AppEnvironment).Single();
        Assert.Equal("default", url.AppEnvironment.Name);
        Assert.Equal("https://site.example.com", url.Url);
    }

    [Fact]
    public async Task ExtensionService_FindProjectForOrigin_MatchesViaProjectAppUrl()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var projectSvc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        await projectSvc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site", AppUrl = "https://site.example.com" });

        var extSvc = new ExtensionService(new UnitOfWork(BuildContext(admin, dbName)), projectSvc, new PassThroughEntitlements());
        var result = await extSvc.FindProjectForOriginAsync("https://site.example.com");

        Assert.True(result.IsSuccess);
        Assert.Equal("site", result.Data!.Key);
    }

    [Fact]
    public async Task ExtensionService_FindProjectForOrigin_NoMatch_ReturnsNotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var projectSvc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());

        var extSvc = new ExtensionService(new UnitOfWork(BuildContext(admin, dbName)), projectSvc, new PassThroughEntitlements());
        var result = await extSvc.FindProjectForOriginAsync("https://nowhere.example.com");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task SetAppUrlAsync_OnNonDefaultEnvironment_DoesNotMatchLegacyAppUrl()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalDefaultEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        int stagingEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var staging = new AppEnvironment { Name = "staging", OwnerId = null };
            db.AppEnvironments.Add(staging);
            db.SaveChanges();
            stagingEnvId = staging.Id;
        }

        var setResult = await svc.SetAppUrlAsync(created.Id, stagingEnvId, new SetProjectAppUrlRequest { Url = "https://staging.example.com" });
        Assert.True(setResult.IsSuccess);

        using var check = BuildContext(admin, dbName);
        var project = check.Projects.Single(p => p.Id == created.Id);
        Assert.Null(project.AppUrl); // legacy field only syncs from the "default" environment
    }
}
