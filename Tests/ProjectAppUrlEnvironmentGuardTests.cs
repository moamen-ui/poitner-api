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
}
