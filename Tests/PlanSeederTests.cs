using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pointer.API.Seed;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>AdminSeeder plan step: Free seed (idempotent), hidden Legacy, and existing-tenant backfill.</summary>
public class PlanSeederTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string p) => "h:" + p;

        public bool Verify(string p, string h) => h == "h:" + p;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    /// <summary>
    /// DB-19: the Legacy workspace-lever backfill step is guarded by an AppSetting — only a
    /// stateful fake can prove the guard (run once) and the null-key idempotence (never overwrite
    /// a non-null value).
    /// </summary>
    private sealed class StatefulSettings : ISettingsService
    {
        public Dictionary<string, string> Store { get; } = new();

        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(
                Store.TryGetValue(key, out var v) && bool.TryParse(v, out var parsed)
                    ? parsed
                    : fallback
            );

        public Task SetBoolAsync(string key, bool value)
        {
            Store[key] = value ? "true" : "false";
            return Task.CompletedTask;
        }

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(Store.TryGetValue(key, out var v) ? v : fallback);

        public Task SetStringAsync(string key, string value)
        {
            Store[key] = value;
            return Task.CompletedTask;
        }

        public Task<int> GetIntAsync(string key, int fallback = 0) =>
            Task.FromResult(
                Store.TryGetValue(key, out var v) && int.TryParse(v, out var parsed)
                    ? parsed
                    : fallback
            );

        public Task SetIntAsync(string key, int value)
        {
            Store[key] = value.ToString();
            return Task.CompletedTask;
        }
    }

    // Builds a service provider whose AppDbContext uses a shared in-memory DB name.
    private static ServiceProvider BuildProvider(string dbName, ISettingsService? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser());
        services.AddScoped(sp => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            sp.GetRequiredService<ICurrentUser>(),
            new ConfigurationBuilder().Build()
        ));
        services.AddScoped<IPasswordHasher>(_ => new IdentityHasher());
        services.AddScoped<ISettingsService>(_ => settings ?? new FakeSettings());
        // Provide operator creds so the seeder proceeds past the super-admin reconcile to the plan step.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADMIN:EMAIL"] = "op@a.com",
                    ["ADMIN:PASSWORD"] = "operator-password",
                }
            )
            .Build();
        services.AddSingleton<IConfiguration>(config);
        return services.BuildServiceProvider();
    }

    private static AppDbContext Raw(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static Guid SeedExistingTenant(string dbName)
    {
        using var seed = Raw(dbName);
        var role = new Role
        {
            Name = "Workspace Admin",
            GrantsAdmin = true,
            IsSuperAdmin = false,
            IsActive = true,
            OwnerId = null,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();
        var pid = Guid.NewGuid();
        seed.Users.Add(
            new User
            {
                Email = "old@a.com",
                PasswordHash = "x",
                DisplayName = "Old",
                RoleId = role.Id,
                PublicId = pid,
                IsActive = true,
            }
        );
        // DB-11a: a tenant is a workspaces row (AdminSeeder's legacy backfill now enumerates
        // db.Workspaces, not self-owned admin users).
        seed.Workspaces.Add(
            new Workspace
            {
                Id = pid,
                Name = "Old Co",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = pid,
            }
        );
        seed.SaveChanges();
        return pid;
    }

    [Fact]
    public async Task Seeds_Free_And_Legacy_And_Backfills_ExistingTenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantPid = SeedExistingTenant(dbName);

        await AdminSeeder.SeedAsync(BuildProvider(dbName));

        using var db = Raw(dbName);
        var free = db.Plans.Single(p => p.Slug == "free");
        Assert.True(free.IsActive);
        Assert.Equal(PlanDisplayState.Visible, free.DisplayState);
        Assert.Equal(0m, free.PriceMonthly);
        // Free entitlements come from catalog defaults (maxProjects = 3).
        Assert.Equal(3, free.Entitlements.MaxProjects);

        var legacy = db.Plans.Single(p => p.Slug == "legacy");
        Assert.False(legacy.IsActive);
        Assert.Equal(PlanDisplayState.Hidden, legacy.DisplayState);
        Assert.Equal(-1, legacy.Entitlements.MaxProjects); // all unlimited

        // Existing tenant backfilled onto Legacy (Active) so it is never retroactively limited.
        var sub = db.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == tenantPid);
        Assert.Equal(legacy.Id, sub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }

    [Fact]
    public async Task Seeder_IsIdempotent_NoDuplicates_And_DoesNotOverwriteEditedFree()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedExistingTenant(dbName);

        await AdminSeeder.SeedAsync(BuildProvider(dbName));

        // Simulate an admin editing Free's entitlements.
        using (var db = Raw(dbName))
        {
            var free = db.Plans.Single(p => p.Slug == "free");
            free.Entitlements = new PlanEntitlements { MaxProjects = 99 };
            db.Plans.Update(free);
            db.SaveChanges();
        }

        // Re-run the seeder — must not duplicate plans nor reset the edited Free entitlements.
        await AdminSeeder.SeedAsync(BuildProvider(dbName));

        using (var db = Raw(dbName))
        {
            Assert.Single(db.Plans.Where(p => p.Slug == "free"));
            Assert.Single(db.Plans.Where(p => p.Slug == "legacy"));
            Assert.Equal(99, db.Plans.Single(p => p.Slug == "free").Entitlements.MaxProjects); // preserved
            // Backfill is idempotent: exactly one subscription per tenant.
            Assert.Single(db.Subscriptions.IgnoreQueryFilters());
        }
    }

    /// <summary>DB-19 task 7: a freshly seeded Legacy plan is unlimited for the workspace levers too
    /// (-1 / false — the special case for the restrictive-polarity bool).</summary>
    [Fact]
    public async Task Legacy_GetsUnlimitedWorkspaceLevers_OnFirstSeed()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedExistingTenant(dbName);

        await AdminSeeder.SeedAsync(BuildProvider(dbName));

        using var db = Raw(dbName);
        var legacy = db.Plans.Single(p => p.Slug == "legacy");
        Assert.Equal(-1, legacy.Entitlements.MaxOwnedWorkspaces);
        Assert.False(legacy.Entitlements.NewWorkspaceRequiresApproval);
    }

    /// <summary>
    /// DB-19 task 7b: the guarded backfill step fills ABSENT keys on a pre-DB-19 Legacy row, sets
    /// the flag, and never overwrites an operator-edited value even when it re-runs.
    /// </summary>
    [Fact]
    public async Task LegacyWorkspaceLevers_BackfillsOnlyAbsentKeys_NeverOperatorEdits()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedExistingTenant(dbName);

        // A pre-DB-19 Legacy row: old-shape entitlements, both new keys absent.
        using (var db = Raw(dbName))
        {
            db.Plans.Add(
                new Plan
                {
                    Name = "Legacy",
                    Slug = "legacy",
                    Entitlements = new PlanEntitlements { MaxProjects = -1, MaxSeats = -1 },
                }
            );
            db.SaveChanges();
        }

        var settings = new StatefulSettings();
        await AdminSeeder.SeedAsync(BuildProvider(dbName, settings));

        using (var db = Raw(dbName))
        {
            var legacy = db.Plans.Single(p => p.Slug == "legacy");
            Assert.Equal(-1, legacy.Entitlements.MaxOwnedWorkspaces);
            Assert.False(legacy.Entitlements.NewWorkspaceRequiresApproval);
        }
        Assert.True(settings.Store[ISettingsService.LegacyWorkspaceLeversBackfilled] == "true");

        // Operator edits both levers; a fresh settings store (flag lost) means the step WOULD
        // re-run — the non-null keys must be preserved.
        using (var db = Raw(dbName))
        {
            var legacy = db.Plans.Single(p => p.Slug == "legacy");
            legacy.Entitlements.MaxOwnedWorkspaces = 5;
            legacy.Entitlements.NewWorkspaceRequiresApproval = true;
            db.Plans.Update(legacy);
            db.SaveChanges();
        }
        var secondRun = new StatefulSettings();
        await AdminSeeder.SeedAsync(BuildProvider(dbName, secondRun));

        using (var db = Raw(dbName))
        {
            var legacy = db.Plans.Single(p => p.Slug == "legacy");
            Assert.Equal(5, legacy.Entitlements.MaxOwnedWorkspaces);
            Assert.True(legacy.Entitlements.NewWorkspaceRequiresApproval);
        }
    }
}
