using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Per-project origin enforcement (R1-05). This decides whether a signed-in stakeholder's comment is
/// accepted, so the load-bearing cases are the refusals and the deliberate carve-outs: a developer's
/// localhost, the dashboard's own origin, and automation that sends no Origin at all.
/// </summary>
public class ProjectOriginEnforcementTests
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
    }

    private sealed class BrandSettings(string? appUrl) : Pointer.Application.Services.Interfaces.ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(key == Pointer.Application.Services.Interfaces.ISettingsService.BrandUrlApp
                ? appUrl ?? fallback
                : fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext Ctx(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new ConfigurationBuilder().Build());

    /// <summary>Seeds a project with enforcement on and one active app-URL row, returns its id.</summary>
    private static int SeedProject(string dbName, Guid tenant, bool enforce, params string[] urls)
    {
        using var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var env = new AppEnvironment { Name = "prod", OwnerId = null };
        db.Set<AppEnvironment>().Add(env);
        db.SaveChanges();

        var project = new Project
        {
            Key = "site",
            Name = "Site",
            OwnerId = tenant,
            EnforceAllowedOrigins = enforce,
        };
        db.Projects.Add(project);
        db.SaveChanges();

        foreach (var url in urls)
        {
            db.Set<ProjectAppUrl>()
                .Add(new ProjectAppUrl
                {
                    ProjectId = project.Id,
                    AppEnvironmentId = env.Id,
                    Url = url,
                    IsActive = true,
                    OwnerId = tenant,
                });
        }
        db.SaveChanges();

        return project.Id;
    }

    private static ProjectService Service(AppDbContext db, ICurrentUser user, string? brandAppUrl = null, string[]? trusted = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                (trusted ?? [])
                    .Select((o, i) => new KeyValuePair<string, string?>($"Security:TrustedDashboardOrigins:{i}", o))
                    .ToList())
            .Build();

        return new ProjectService(new UnitOfWork(db), user, new PassThroughEntitlements(), new BrandSettings(brandAppUrl), config);
    }

    [Fact]
    public async Task Disabled_By_Default_Allows_Everything()
    {
        var name = nameof(Disabled_By_Default_Allows_Everything);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: false, "https://app.example.com");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);

        Assert.True(await Service(db, new FakeCurrentUser { TenantId = tenant })
            .IsOriginAllowedAsync(id, "https://anywhere.evil", EnvironmentTag.Production, false));
    }

    [Fact]
    public async Task Enforced_Allows_A_Configured_Origin_And_Blocks_Others()
    {
        var name = nameof(Enforced_Allows_A_Configured_Origin_And_Blocks_Others);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://app.example.com");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);
        var svc = Service(db, new FakeCurrentUser { TenantId = tenant });

        Assert.True(await svc.IsOriginAllowedAsync(id, "https://app.example.com", EnvironmentTag.Production, false));
        Assert.False(await svc.IsOriginAllowedAsync(id, "https://evil.example", EnvironmentTag.Production, false));
    }

    [Fact]
    public async Task Enforced_Honours_A_Wildcard_Row()
    {
        var name = nameof(Enforced_Honours_A_Wildcard_Row);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://myapp-*.vercel.app");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);
        var svc = Service(db, new FakeCurrentUser { TenantId = tenant });

        Assert.True(await svc.IsOriginAllowedAsync(id, "https://myapp-pr-12.vercel.app", EnvironmentTag.Staging, false));
        // Another tenant on the same shared host must not inherit the allowance.
        Assert.False(await svc.IsOriginAllowedAsync(id, "https://someoneelse.vercel.app", EnvironmentTag.Staging, false));
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("http://app.localhost:4200")]
    public async Task Local_Environment_Always_Allows_Loopback(string origin)
    {
        // Nobody configures their dev port, and requiring it would make enforcement unusable.
        var name = nameof(Local_Environment_Always_Allows_Loopback) + origin.GetHashCode();
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://app.example.com");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);
        var svc = Service(db, new FakeCurrentUser { TenantId = tenant });

        Assert.True(await svc.IsOriginAllowedAsync(id, origin, EnvironmentTag.Local, false));
        // …but the carve-out is scoped to Local. A production-tagged comment from localhost is not.
        Assert.False(await svc.IsOriginAllowedAsync(id, origin, EnvironmentTag.Production, false));
    }

    [Fact]
    public async Task The_Dashboard_Origin_Is_Always_Allowed()
    {
        // Staff reply and change status from the dashboard; without this, enabling enforcement
        // would 403 every one of those.
        var name = nameof(The_Dashboard_Origin_Is_Always_Allowed);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://app.example.com");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);

        var svc = Service(
            db,
            new FakeCurrentUser { TenantId = tenant },
            brandAppUrl: "https://dash.example.com",
            trusted: ["https://app-angular.example.com"]);

        Assert.True(await svc.IsOriginAllowedAsync(id, "https://dash.example.com", EnvironmentTag.Production, false));
        Assert.True(await svc.IsOriginAllowedAsync(id, "https://app-angular.example.com", EnvironmentTag.Production, false));
    }

    [Fact]
    public async Task No_Origin_Is_Allowed_For_Staff_And_Refused_For_A_Client()
    {
        // The CLI and AI agents send no Origin. A quick-access client only ever arrives via a
        // browser, so a missing Origin from one is not a legitimate shape.
        var name = nameof(No_Origin_Is_Allowed_For_Staff_And_Refused_For_A_Client);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://app.example.com");
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);
        var svc = Service(db, new FakeCurrentUser { TenantId = tenant });

        Assert.True(await svc.IsOriginAllowedAsync(id, null, EnvironmentTag.Production, isQuickAccess: false));
        Assert.True(await svc.IsOriginAllowedAsync(id, "  ", EnvironmentTag.Production, isQuickAccess: false));
        Assert.False(await svc.IsOriginAllowedAsync(id, null, EnvironmentTag.Production, isQuickAccess: true));
    }

    [Fact]
    public async Task An_Inactive_Row_Does_Not_Authorise()
    {
        var name = nameof(An_Inactive_Row_Does_Not_Authorise);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true, "https://app.example.com");

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, name))
        {
            var row = await seed.Set<ProjectAppUrl>().FirstAsync();
            row.IsActive = false;
            await seed.SaveChangesAsync();
        }

        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);

        Assert.False(await Service(db, new FakeCurrentUser { TenantId = tenant })
            .IsOriginAllowedAsync(id, "https://app.example.com", EnvironmentTag.Production, false));
    }

    [Fact]
    public async Task Enforced_With_No_Rows_Blocks_Browser_Traffic()
    {
        // Turning enforcement on with nothing configured is a lockout — deliberately, because the
        // alternative (silently allowing everything) would make the setting a lie.
        var name = nameof(Enforced_With_No_Rows_Blocks_Browser_Traffic);
        var tenant = Guid.NewGuid();
        var id = SeedProject(name, tenant, enforce: true);
        using var db = Ctx(new FakeCurrentUser { TenantId = tenant }, name);

        Assert.False(await Service(db, new FakeCurrentUser { TenantId = tenant })
            .IsOriginAllowedAsync(id, "https://app.example.com", EnvironmentTag.Production, false));
    }
}
