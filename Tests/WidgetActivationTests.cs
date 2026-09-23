using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
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
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static AppDbContext BuildContext(
        ICurrentUser user,
        string dbName,
        IInterceptor? extraInterceptor = null
    )
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName);
        if (extraInterceptor is not null)
            builder.AddInterceptors(extraInterceptor);
        return new(
            builder.Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );
    }

    /// <summary>Review finding #3 (MEDIUM): deterministically fails any SaveChanges(Async) with a
    /// pending Added widget_installed UsageEvent, with an exception that is NOT the 23505/19
    /// duplicate-key shape — proving the general catch added after that case still swallows it
    /// (never 500s this anonymous, public path).</summary>
    private sealed class FailOnWidgetInstalledInsertInterceptor : SaveChangesInterceptor
    {
        private static void ThrowIfPending(DbContext? context)
        {
            if (
                context?.ChangeTracker.Entries<UsageEvent>()
                    .Any(e =>
                        e.State == EntityState.Added
                        && e.Entity.Type == UsageEventTypes.WidgetInstalled
                    ) == true
            )
                throw new InvalidOperationException(
                    "simulated non-23505 widget_installed insert failure (test)"
                );
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result
        )
        {
            ThrowIfPending(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            ThrowIfPending(eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    private static void SeedGlobalLocalEnvironment(string dbName)
    {
        using var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        db.AppEnvironments.Add(
            new AppEnvironment
            {
                Name = "local",
                OwnerId = null,
                IsEnabled = true,
            }
        );
        db.SaveChanges();
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_ProjectFullyInactive_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var created = (
            await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })
        ).Data!;
        await svc.UpdateAsync(
            created.Id,
            new UpdateProjectRequest
            {
                IsActiveLocal = false,
                IsActiveStaging = false,
                IsActiveProduction = false,
            }
        );

        var result = await svc.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_NoOrigin_ActiveProject_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var result = await svc.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_OriginNotConfigured_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
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
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var created = (
            await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })
        ).Data!;

        int localEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var local = new AppEnvironment
            {
                Name = "local",
                OwnerId = null,
                IsEnabled = true,
            };
            db.AppEnvironments.Add(local);
            db.SaveChanges();
            localEnvId = local.Id;
        }

        await svc.SetAppUrlAsync(
            created.Id,
            localEnvId,
            new SetProjectAppUrlRequest { Url = "http://localhost:3000", IsActive = false }
        );

        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000/");
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_OriginMatchesActiveRow_ReturnsActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var created = (
            await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })
        ).Data!;

        int localEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var local = new AppEnvironment
            {
                Name = "local",
                OwnerId = null,
                IsEnabled = true,
            };
            db.AppEnvironments.Add(local);
            db.SaveChanges();
            localEnvId = local.Id;
        }

        await svc.SetAppUrlAsync(
            created.Id,
            localEnvId,
            new SetProjectAppUrlRequest { Url = "http://localhost:3000", IsActive = true }
        );

        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000");
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_EnvironmentDisabled_BlocksThatOrigin()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var created = (
            await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })
        ).Data!;

        int localEnvId;
        using (var db = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var local = new AppEnvironment
            {
                Name = "local",
                OwnerId = null,
                IsEnabled = false,
            }; // disabled environment
            db.AppEnvironments.Add(local);
            db.SaveChanges();
            localEnvId = local.Id;
        }

        // A mapping that describes this origin, on an environment that has been disabled.
        using (var db = BuildContext(admin, dbName))
        {
            db.ProjectAppUrls.Add(
                new ProjectAppUrl
                {
                    ProjectId = created.Id,
                    AppEnvironmentId = localEnvId,
                    Url = "http://localhost:3000",
                    IsActive = false,
                    OwnerId = tenant,
                }
            );
            db.SaveChanges();
        }

        var result = await svc.CheckWidgetActiveAsync("site", "http://localhost:3000");
        Assert.True(result.IsSuccess);

        // REVERSED 2026-09-12 by the product owner. This test previously asserted Active == true —
        // a disabled environment's row was ignored, so the origin fell through to "no mapping →
        // allowed" and disabling an environment changed nothing a user could see.
        //
        // The rule now: disabling an environment takes the widget off the sites that environment
        // describes. An origin with NO mapping is still allowed, and each environment's origins
        // match their own rows, so this cannot reach another environment's site —
        // ProjectAppUrlEnvironmentGuardTests pins both of those bounds.
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_UnknownKey_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        var svc = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );

        var result = await svc.CheckWidgetActiveAsync("does-not-exist", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    [Fact]
    public async Task CheckWidgetActiveAsync_AmbiguousKeyAcrossTenants_ReturnsNotActive()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedGlobalLocalEnvironment(dbName);
        var tenantA = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        var tenantB = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        var svcA = new ProjectService(
            new UnitOfWork(BuildContext(tenantA, dbName)),
            tenantA,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var svcB = new ProjectService(
            new UnitOfWork(BuildContext(tenantB, dbName)),
            tenantB,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        await svcA.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Tenant A Site" });
        await svcB.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Tenant B Site" });

        // Same key exists under two different tenants — must refuse rather than pick one arbitrarily.
        var result = await svcA.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);
    }

    // ── DB-15: the widget_installed one-shot fact (first non-localhost widget-status hit) ──

    private static async Task<ProjectService> ActiveSiteServiceAsync(
        string dbName,
        ICurrentUser user,
        IMemoryCache cache,
        string key = "site"
    )
    {
        SeedGlobalLocalEnvironment(dbName);
        var svc = new ProjectService(
            new UnitOfWork(BuildContext((FakeCurrentUser)user, dbName)),
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter(),
            cache: cache
        );
        await svc.CreateAsync(new CreateProjectRequest { Key = key, Name = "Site" });
        return svc;
    }

    [Fact]
    public async Task WidgetStatus_NonLocalhostOrigin_EmitsWidgetInstalledOnce()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        using var cache = TestProjectServiceDeps.Cache();
        var svc = await ActiveSiteServiceAsync(dbName, admin, cache);

        var first = await svc.CheckWidgetActiveAsync("site", "https://app.example.com");
        var second = await svc.CheckWidgetActiveAsync("site", "https://app.example.com");
        Assert.True(first.IsSuccess && second.IsSuccess);

        var row = Assert.Single(
            BuildContext(admin, dbName).UsageEvents.IgnoreQueryFilters().ToList()
        );
        Assert.Equal(UsageEventTypes.WidgetInstalled, row.Type);
        Assert.Equal(tenant, row.OwnerId);
        Assert.Equal("api", row.Source);
        Assert.Contains("app.example.com", row.Meta);
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://127.0.0.1:3000")]
    [InlineData("http://app.localhost:8080")]
    public async Task WidgetStatus_LocalhostOrigin_EmitsNothing(string origin)
    {
        var dbName = Guid.NewGuid().ToString();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        using var cache = TestProjectServiceDeps.Cache();
        var svc = await ActiveSiteServiceAsync(dbName, admin, cache);

        var result = await svc.CheckWidgetActiveAsync("site", origin);
        Assert.True(result.IsSuccess);

        Assert.Empty(BuildContext(admin, dbName).UsageEvents.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task WidgetStatus_NoOrigin_EmitsNothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        using var cache = TestProjectServiceDeps.Cache();
        var svc = await ActiveSiteServiceAsync(dbName, admin, cache);

        var result = await svc.CheckWidgetActiveAsync("site", null);
        Assert.True(result.IsSuccess);

        Assert.Empty(BuildContext(admin, dbName).UsageEvents.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task WidgetStatus_AmbiguousKey_EmitsNothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        var tenantB = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        using var cacheA = TestProjectServiceDeps.Cache();
        using var cacheB = TestProjectServiceDeps.Cache();
        var svcA = await ActiveSiteServiceAsync(dbName, tenantA, cacheA);
        var svcB = await ActiveSiteServiceAsync(dbName, tenantB, cacheB);

        // Same key under two tenants — the check refuses rather than picking one; no fact is minted.
        var result = await svcA.CheckWidgetActiveAsync("site", "https://app.example.com");
        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Active);

        Assert.Empty(BuildContext(tenantA, dbName).UsageEvents.IgnoreQueryFilters().ToList());
    }

    /// <summary>
    /// The guarded insert's duplicate path: the row already exists and the cache holds nothing
    /// (e.g. a restart) — the AnyAsync pre-check sees it, no second row is written, no exception.
    /// The unique index itself is exercised on Sqlite by UsageEventFirstCommentTests' shape; the
    /// InMemory provider does not model partial index filters, so the catch's 23505/19 path is
    /// only reachable in a true concurrent race.
    /// </summary>
    [Fact]
    public async Task WidgetInstalled_RaceOnUniqueIndex_Swallowed()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };

        using (var cache = TestProjectServiceDeps.Cache())
        {
            var svc = await ActiveSiteServiceAsync(dbName, admin, cache);
            await svc.CheckWidgetActiveAsync("site", "https://app.example.com");
        }

        // Cache dropped (process restart shape) — call again: pre-check sees the row, no throw.
        using var freshCache = TestProjectServiceDeps.Cache();
        var svcAfterRestart = new ProjectService(
            new UnitOfWork(BuildContext(admin, dbName)),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter(),
            cache: freshCache
        );
        var again = await svcAfterRestart.CheckWidgetActiveAsync("site", "https://app.example.com");
        Assert.True(again.IsSuccess);

        var rows = BuildContext(admin, dbName).UsageEvents.IgnoreQueryFilters().ToList();
        var row = Assert.Single(rows);
        Assert.Equal(UsageEventTypes.WidgetInstalled, row.Type);
    }

    /// <summary>
    /// Review finding #3 (MEDIUM): a non-23505 failure writing the widget_installed fact (e.g. a
    /// transient DB error) must never 500 this anonymous, public widget-status path — the general
    /// catch after the 23505 case must swallow it too, clearing the tracker and still caching so the
    /// insert isn't retried on every hit.
    /// </summary>
    [Fact]
    public async Task WidgetInstalled_NonDuplicateFailure_NeverThrows_AndCaches()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = tenant,
        };
        SeedGlobalLocalEnvironment(dbName);

        using var cache = TestProjectServiceDeps.Cache();
        var failingContext = BuildContext(
            admin,
            dbName,
            extraInterceptor: new FailOnWidgetInstalledInsertInterceptor()
        );
        var svc = new ProjectService(
            new UnitOfWork(failingContext),
            admin,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter(),
            cache: cache
        );
        var created = (
            await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })
        ).Data!;

        // The interceptor throws InvalidOperationException (not DbUpdateException/23505) — the
        // general catch must swallow it: the call still succeeds, no exception escapes.
        var result = await svc.CheckWidgetActiveAsync("site", "https://app.example.com");
        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.Active);

        // No row was actually persisted (the insert failed)…
        Assert.Empty(BuildContext(admin, dbName).UsageEvents.IgnoreQueryFilters().ToList());

        // …but the cache was still set on the failure path, so a second hit on this (or a fresh,
        // same-process) service instance doesn't retry the write on every request.
        Assert.True(cache.TryGetValue($"widget_installed:{created.Id}", out _));
    }
}
