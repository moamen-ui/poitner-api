using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class ProjectAppUrlEnvironmentGuardTests
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

    [Fact]
    public async Task CreateAsync_ValidEnvironment_CreatesProjectAndUrl()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int envId;
        using (var db = BuildContext(admin, dbName))
        {
            var env = new AppEnvironment { Name = "test", OwnerId = tenant, IsEnabled = true };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = await svc.CreateAsync(new CreateProjectRequest
        {
            Key = "p",
            Name = "P",
            AppUrl = "https://x",
            AppEnvironmentId = envId
        });

        Assert.True(created.IsSuccess);
        
        using (var db = BuildContext(admin, dbName))
        {
            var url = db.ProjectAppUrls.Single();
            Assert.Equal(envId, url.AppEnvironmentId);
            Assert.Equal("https://x", url.Url);
        }
    }

    [Fact]
    public async Task CreateAsync_MissingEnvironment_ReturnsNotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = await svc.CreateAsync(new CreateProjectRequest
        {
            Key = "p",
            Name = "P",
            AppUrl = "https://x",
            AppEnvironmentId = 999
        });

        Assert.True(created.IsNotFound);
    }

    [Fact]
    public async Task CreateAsync_DisabledEnvironment_ReturnsFailure()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int envId;
        using (var db = BuildContext(admin, dbName))
        {
            var env = new AppEnvironment { Name = "test", OwnerId = tenant, IsEnabled = false };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = await svc.CreateAsync(new CreateProjectRequest
        {
            Key = "p",
            Name = "P",
            AppUrl = "https://x",
            AppEnvironmentId = envId
        });

        Assert.False(created.IsSuccess);
        Assert.False(created.IsNotFound);
    }

    [Fact]
    public async Task CreateAsync_RetiredEnvironment_ReturnsFailure()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int envId;
        using (var db = BuildContext(admin, dbName))
        {
            var env = new AppEnvironment { Name = "test", OwnerId = tenant, IsEnabled = true, IsRetired = true };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = await svc.CreateAsync(new CreateProjectRequest
        {
            Key = "p",
            Name = "P",
            AppUrl = "https://x",
            AppEnvironmentId = envId
        });

        Assert.False(created.IsSuccess);
        Assert.False(created.IsNotFound);
    }

    [Fact]
    public async Task CreateAsync_ForeignEnvironment_ReturnsNotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int envId;
        // Seed foreign env ignoring query filters
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, new FakeCurrentUser { IsSuperAdmin = true }, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()))
        {
            var env = new AppEnvironment { Name = "test", OwnerId = otherTenant, IsEnabled = true };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var created = await svc.CreateAsync(new CreateProjectRequest
        {
            Key = "p",
            Name = "P",
            AppUrl = "https://x",
            AppEnvironmentId = envId
        });

        Assert.True(created.IsNotFound);
    }

    [Fact]
    public async Task SetAppUrlAsync_DisabledEnvironment_ReturnsFailure()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int projectId, envId;
        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "p", Name = "P", OwnerId = tenant };
            db.Projects.Add(project);
            var env = new AppEnvironment { Name = "test", OwnerId = tenant, IsEnabled = false };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            projectId = project.Id;
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var result = await svc.SetAppUrlAsync(projectId, envId, new SetProjectAppUrlRequest { Url = "u" });

        Assert.False(result.IsSuccess);
        Assert.False(result.IsNotFound);
    }

    /// <summary>
    /// Builds a project + enabled environment and tries to save <paramref name="url"/> against it.
    /// </summary>
    private static async Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Project.ProjectAppUrlResponse>> SaveUrlAsync(string url)
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int projectId, envId;
        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "p", Name = "P", OwnerId = tenant };
            db.Projects.Add(project);
            var env = new AppEnvironment { Name = "preview", OwnerId = tenant, IsEnabled = true };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            projectId = project.Id;
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        return await svc.SetAppUrlAsync(projectId, envId, new SetProjectAppUrlRequest { Url = url });
    }

    /// <summary>
    /// BINDING: the origin-enforcement switch must be reachable.
    ///
    /// Project.EnforceAllowedOrigins existed and IsOriginAllowedAsync read it, but no DTO exposed
    /// it and no request could set it — so it was permanently false and the entire allowed-origins
    /// feature (OriginNormalizer, the pattern rules, ProjectAppUrl matching) could never engage.
    /// A setting nothing can turn on is not a setting.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_CanEnableAndDisableOriginEnforcement()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int projectId;
        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "p", Name = "P", OwnerId = tenant, CreatedBy = admin.Id!.Value };
            db.Projects.Add(project);
            db.SaveChanges();
            projectId = project.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());

        // Off by default, and the response must actually carry the value — a write nobody can read
        // back is just as unusable as one nobody can make.
        var before = await svc.UpdateAsync(projectId, new UpdateProjectRequest { });
        Assert.True(before.IsSuccess);
        Assert.False(before.Data!.EnforceAllowedOrigins);

        var enabled = await svc.UpdateAsync(projectId, new UpdateProjectRequest { EnforceAllowedOrigins = true });
        Assert.True(enabled.IsSuccess);
        Assert.True(enabled.Data!.EnforceAllowedOrigins);

        // An omitted field must not silently reset it.
        var untouched = await svc.UpdateAsync(projectId, new UpdateProjectRequest { Name = "Renamed" });
        Assert.True(untouched.Data!.EnforceAllowedOrigins);

        var disabled = await svc.UpdateAsync(projectId, new UpdateProjectRequest { EnforceAllowedOrigins = false });
        Assert.False(disabled.Data!.EnforceAllowedOrigins);
    }

    /// <summary>
    /// BINDING: wildcard validation must run on the WRITE path.
    ///
    /// OriginNormalizer.ValidatePattern refuses a bare `*` on shared hosting because
    /// `https://*.vercel.app` authorises every other tenant on that platform to post comments into
    /// this project. The rule was fully implemented and unit-tested, but nothing called it when
    /// saving an app URL — so the pattern saved without complaint and was honoured at request
    /// time. A guard that only runs in its own tests is not a guard.
    /// </summary>
    [Theory]
    [InlineData("https://*.vercel.app")]      // bare * on a shared host
    [InlineData("https://*.acme.com")]        // bare *, fewer than 3 remaining labels
    [InlineData("https://*.foo.github.io")]   // shared suffix, ends-with semantics
    [InlineData("https://*.*.acme.com")]      // more than one wildcard
    public async Task SetAppUrlAsync_RejectsAnUnsafeWildcardPattern(string url)
    {
        var result = await SaveUrlAsync(url);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Theory]
    [InlineData("https://myapp-*.vercel.app")]     // literal part keeps it scoped to one account
    [InlineData("https://*.staging.acme.com")]     // >= 3 labels, not a shared host
    [InlineData("https://app.example.com")]        // an exact origin is not a pattern at all
    public async Task SetAppUrlAsync_AcceptsASafePattern(string url)
    {
        var result = await SaveUrlAsync(url);

        Assert.True(result.IsSuccess, $"expected {url} to save, got: {result.Message}");
    }
    /// <summary>
    /// BINDING (product decision, reverses execution Decision 7): disabling an environment takes the
    /// widget off the sites that environment describes.
    ///
    /// The original decision ignored rows on disabled environments, so the origin fell through to
    /// "no configured mapping → allowed" and the setting did nothing visible. The owner's call is
    /// that disabling an environment should mean what it says.
    ///
    /// The blast radius is what makes that safe, and is pinned below: only origins the disabled
    /// environment actually describes are affected. An unmapped origin still renders, and another
    /// environment's origin is untouched — so turning off staging cannot take production offline,
    /// which was the original decision's whole concern.
    /// </summary>
    [Fact]
    public async Task WidgetIsInactive_OnAnOriginDescribedByADisabledEnvironment()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int projectId, envId;
        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "wg", Name = "WG", OwnerId = tenant, IsActiveLocal = true, CreatedBy = admin.Id!.Value };
            db.Projects.Add(project);
            var env = new AppEnvironment { Name = "preview", OwnerId = tenant, IsEnabled = true };
            db.AppEnvironments.Add(env);
            db.SaveChanges();
            projectId = project.Id;
            envId = env.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        await svc.SetAppUrlAsync(projectId, envId, new SetProjectAppUrlRequest { Url = "https://preview.example.com" });

        // Enabled: the widget renders there.
        Assert.True((await svc.CheckWidgetActiveAsync("wg", "https://preview.example.com")).Data!.Active);

        using (var db = BuildContext(admin, dbName))
        {
            var env = db.AppEnvironments.IgnoreQueryFilters().First(e => e.Id == envId);
            env.IsEnabled = false;
            db.SaveChanges();
        }

        var after = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());

        // Disabled: it does not.
        Assert.False((await after.CheckWidgetActiveAsync("wg", "https://preview.example.com")).Data!.Active);

        // BLAST RADIUS: an origin this environment never described is unaffected.
        Assert.True((await after.CheckWidgetActiveAsync("wg", "https://unrelated.example.com")).Data!.Active);
    }

    [Fact]
    public async Task DisablingOneEnvironment_DoesNotTakeAnotherEnvironmentsSiteOffline()
    {
        // The exact fear behind the original decision: an admin disables staging and production
        // goes dark. Production's origin matches production's own row, so it cannot.
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int projectId, stagingId, prodId;
        using (var db = BuildContext(admin, dbName))
        {
            var project = new Project { Key = "wg2", Name = "WG2", OwnerId = tenant, IsActiveProduction = true, CreatedBy = admin.Id!.Value };
            db.Projects.Add(project);
            var staging = new AppEnvironment { Name = "staging-x", OwnerId = tenant, IsEnabled = true };
            var prod = new AppEnvironment { Name = "prod-x", OwnerId = tenant, IsEnabled = true };
            db.AppEnvironments.AddRange(staging, prod);
            db.SaveChanges();
            projectId = project.Id; stagingId = staging.Id; prodId = prod.Id;
        }

        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        await svc.SetAppUrlAsync(projectId, stagingId, new SetProjectAppUrlRequest { Url = "https://staging.example.com" });
        await svc.SetAppUrlAsync(projectId, prodId, new SetProjectAppUrlRequest { Url = "https://app.example.com" });

        using (var db = BuildContext(admin, dbName))
        {
            var staging = db.AppEnvironments.IgnoreQueryFilters().First(e => e.Id == stagingId);
            staging.IsEnabled = false;
            db.SaveChanges();
        }

        var after = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());

        Assert.False((await after.CheckWidgetActiveAsync("wg2", "https://staging.example.com")).Data!.Active);
        Assert.True((await after.CheckWidgetActiveAsync("wg2", "https://app.example.com")).Data!.Active);
    }
}
