using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Effective-plan resolution + the kill-switch + compare-only count/flag checks. Levers wired into
/// each service are covered in <see cref="PlanEnforcementTests"/>.
/// </summary>
public class EntitlementServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public bool Enforcement { get; set; }
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(key == ISettingsService.EnforcementEnabled ? Enforcement : fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options, u);

    // Seeds a Free plan + a Pro plan and returns their ids.
    private static (int freeId, int proId) SeedPlans(string db, PlanEntitlements? proEntitlements = null)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var free = new Plan { Name = "Free", Slug = "free", Entitlements = new PlanEntitlements { MaxProjects = 3 } };
        var pro = new Plan { Name = "Pro", Slug = "pro", Entitlements = proEntitlements ?? new PlanEntitlements { MaxProjects = 20 } };
        seed.Plans.AddRange(free, pro);
        seed.SaveChanges();
        return (free.Id, pro.Id);
    }

    [Fact]
    public async Task MissingSubscription_ResolvesToFree()
    {
        var db = Guid.NewGuid().ToString();
        SeedPlans(db);
        var tenant = Guid.NewGuid();

        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant },
            new FakeSettings());

        var e = await svc.GetForTenantAsync(tenant);
        Assert.Equal(3, EntitlementCatalog.ResolveInt(e, EntitlementCatalog.MaxProjects)); // Free's value
    }

    [Fact]
    public async Task SubscriptionPlan_Wins_OverFree()
    {
        var db = Guid.NewGuid().ToString();
        var (_, proId) = SeedPlans(db);
        var tenant = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Subscriptions.Add(new Subscription { OwnerId = tenant, PlanId = proId, Status = SubscriptionStatus.Active });
            seed.SaveChanges();
        }

        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant }, new FakeSettings());

        var e = await svc.GetForTenantAsync(tenant);
        Assert.Equal(20, EntitlementCatalog.ResolveInt(e, EntitlementCatalog.MaxProjects)); // Pro's value
    }

    [Fact]
    public async Task MissingKey_ResolvesTo_CatalogDefault_NotZero()
    {
        var db = Guid.NewGuid().ToString();
        // A plan with NO MaxSeats set at all.
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Plans.Add(new Plan { Name = "Free", Slug = "free", Entitlements = new PlanEntitlements { MaxProjects = 3 } });
            seed.SaveChanges();
        }
        var tenant = Guid.NewGuid();
        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant },
            new FakeSettings { Enforcement = true });

        // MaxSeats unset ⇒ catalog default (5), so count 4 passes, 5 blocks — never a 0-lockout.
        Assert.True((await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxSeats, 4)).IsSuccess);
        Assert.True((await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxSeats, 5)).IsLimitReached);
    }

    [Fact]
    public async Task KillSwitchOff_AllChecksPass()
    {
        var db = Guid.NewGuid().ToString();
        SeedPlans(db);
        var tenant = Guid.NewGuid();
        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant },
            new FakeSettings { Enforcement = false });

        // Way over the Free limit of 3 — but the kill-switch is OFF, so it passes.
        Assert.True((await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxProjects, 999)).IsSuccess);
        Assert.True((await svc.EnforceFlagAsync(tenant, EntitlementCatalog.ExtensionEnabled)).IsSuccess);
    }

    [Fact]
    public async Task KillSwitchOn_EnforcesLimit_WithPlanLimitPayload()
    {
        var db = Guid.NewGuid().ToString();
        var (freeId, _) = SeedPlans(db);
        var tenant = Guid.NewGuid();
        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant },
            new FakeSettings { Enforcement = true });

        var atLimit = await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxProjects, 3);
        Assert.True(atLimit.IsLimitReached);
        Assert.NotNull(atLimit.Limit);
        Assert.Equal(EntitlementCatalog.MaxProjects, atLimit.Limit!.Lever);
        Assert.Equal(3, atLimit.Limit.Current);
        Assert.Equal(3, atLimit.Limit.Limit);
        Assert.Equal(freeId, atLimit.Limit.PlanId);

        Assert.True((await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxProjects, 2)).IsSuccess);
    }

    [Fact]
    public async Task UnlimitedValue_MinusOne_NeverBlocks()
    {
        var db = Guid.NewGuid().ToString();
        // Legacy-style plan: MaxProjects = -1 (unlimited).
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Plans.Add(new Plan { Name = "Free", Slug = "free", Entitlements = new PlanEntitlements { MaxProjects = 3 } });
            seed.Plans.Add(new Plan { Name = "Legacy", Slug = "legacy", IsActive = false, DisplayState = PlanDisplayState.Hidden, Entitlements = new PlanEntitlements { MaxProjects = -1 } });
            seed.SaveChanges();
        }
        var tenant = Guid.NewGuid();
        int legacyId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            legacyId = seed.Plans.Single(p => p.Slug == "legacy").Id;
            seed.Subscriptions.Add(new Subscription { OwnerId = tenant, PlanId = legacyId, Status = SubscriptionStatus.Active });
            seed.SaveChanges();
        }

        using var ctx = Ctx(new FakeCurrentUser { TenantId = tenant }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { TenantId = tenant },
            new FakeSettings { Enforcement = true });

        Assert.True((await svc.CheckCountAsync(tenant, EntitlementCatalog.MaxProjects, 100000)).IsSuccess);
    }

    [Fact]
    public async Task TenantIsolation_OneTenantAtLimit_DoesNotAffectAnother()
    {
        var db = Guid.NewGuid().ToString();
        SeedPlans(db);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var svc = new EntitlementService(new UnitOfWork(ctx), new FakeCurrentUser { IsSuperAdmin = true },
            new FakeSettings { Enforcement = true });

        // Both resolve to Free (limit 3). The check is compare-only per passed count — independent.
        Assert.True((await svc.CheckCountAsync(tenantA, EntitlementCatalog.MaxProjects, 3)).IsLimitReached);
        Assert.True((await svc.CheckCountAsync(tenantB, EntitlementCatalog.MaxProjects, 0)).IsSuccess);
    }
}
