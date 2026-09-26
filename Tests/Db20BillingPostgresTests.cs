using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-20 §6 tests 10 and 13 — real Postgres only (append-only trigger, existing-data-survives
/// migration, concurrent races the FOR UPDATE lock order closes). Gated behind POINTER_TEST_PG
/// (UsageRollupPostgresTests precedent): skipped unless set, so `dotnet test` / CI never require a
/// live Postgres. Throwaway instance for this run:
///
///   docker run -d --name db20-pg -e POSTGRES_PASSWORD=pw -p 5592:5432 postgres:15
///   POINTER_TEST_PG=1 dotnet test Tests --filter FullyQualifiedName~Db20BillingPostgresTests
///
/// (connection defaults to Host=localhost;Port=5592;Database=postgres;Username=postgres;Password=pw
/// — override with POINTER_TEST_PG_CONNSTRING for a different throwaway instance/port).
/// </summary>
[Trait("Category", "Postgres")]
public class Db20BillingPostgresTests
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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string html,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class NoopBranding : IBrandingService
    {
        private static BrandingResponse Default() =>
            new()
            {
                ProductName = "Pointer",
                Tagline = string.Empty,
                PrimaryColor = "#2563eb",
                Urls = new BrandingUrlsResponse { App = "https://app.example.com" },
                Assets = new BrandingAssetsResponse(),
            };

        public Task<Pointer.Application.Response.Result<BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<BrandingResponse>.Success(Default())
            );

        public Task<Pointer.Application.Response.Result<BrandingResponse>> UpdateAsync(
            BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<BrandingResponse>.Success(Default())
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Default());
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("POINTER_TEST_PG") != null;

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("POINTER_TEST_PG_CONNSTRING")
        ?? "Host=localhost;Port=5592;Database=postgres;Username=postgres;Password=pw";

    private static AppDbContext MakeContext(ICurrentUser? user = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
            user ?? new FakeCurrentUser(),
            new ConfigurationBuilder().Build()
        );

    private static BillingService BillingSvc(AppDbContext db, ICurrentUser user)
    {
        var uow = new UnitOfWork(db);
        return new BillingService(
            uow,
            user,
            new MembershipService(uow),
            new EntitlementService(uow, user, new FakeSettings()),
            new NoopBillingProvider(),
            new FakeSettings()
        );
    }

    [Fact]
    public async Task AppendOnlyTrigger_RefusesUpdateAndDelete_AdmitsSetNullDetach()
    {
        if (!Enabled)
            return;

        await using var db = MakeContext();
        await db.Database.MigrateAsync();
        // No TRUNCATE here: billing_payments is append-only (refuses TRUNCATE too), proven by
        // this very test below. A fresh random workspace id keeps this run isolated.

        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "PG-Trigger",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        var pgSuffix = Guid.NewGuid().ToString("N")[..8];
        db.Plans.Add(
            new Plan
            {
                Name = "Pro " + pgSuffix,
                Slug = "pro-pg-" + pgSuffix,
                PriceMonthly = 10,
                Currency = "USD",
                Entitlements = new PlanEntitlements(),
            }
        );
        await db.SaveChangesAsync();
        var planId = db.Plans.Local.Single().Id;

        await db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO billing_payments (owner_id, plan_id, kind, amount, currency, method, paid_at, period_start, period_end, previous_plan_id, previous_status, recorded_at, recorded_by)
              VALUES ({0}, {1}, 1, 10, 'USD', 1, now(), now(), now() + interval '1 month', {1}, 0, now(), {2})",
            workspaceId,
            planId,
            Guid.NewGuid()
        );

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE billing_payments SET amount = 1")
        );
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM billing_payments")
        );

        // The one permitted UPDATE: the FK's ON DELETE SET NULL detaching the row.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM workspaces WHERE id = {0}", workspaceId);
        var row = await db.BillingPayments.IgnoreQueryFilters().SingleAsync();
        Assert.Null(row.OwnerId);
    }

    [Fact]
    public async Task ExistingData_SurvivesTheThreeMigrations_JobLeavesItUntouched()
    {
        if (!Enabled)
            return;

        await using var db = MakeContext();
        // Roll back to just before DB-20 so the "existing data" this asserts on predates it.
        await db.GetService<IMigrator>().MigrateAsync("20260924051230_ClearUsersRoleIdForMembers");

        var workspaceLegacy = Guid.NewGuid();
        var workspacePaidPending = Guid.NewGuid();
        var legacySlug = "legacy-pg-" + Guid.NewGuid().ToString("N")[..8];
        var paidSlug = "paid-pg-" + Guid.NewGuid().ToString("N")[..8];

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO workspaces (id, name, created_at, created_by) VALUES ({0}, 'Legacy WS', now(), {0}), ({1}, 'Paid WS', now(), {1})",
            workspaceLegacy,
            workspacePaidPending
        );
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO plans (name, slug, price_monthly, currency, interval, sort_order, is_active, display_state, feature_bullets, entitlements, created_at, created_by) "
                + "VALUES ({1}, {1}, 0, 'USD', 0, 0, false, 2, '[]', '{{}}', now(), {0})",
            workspaceLegacy,
            legacySlug
        );
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO plans (name, slug, price_monthly, currency, interval, sort_order, is_active, display_state, feature_bullets, entitlements, created_at, created_by) "
                + "VALUES ({1}, {1}, 15, 'USD', 0, 0, true, 0, '[]', '{{}}', now(), {0})",
            workspacePaidPending,
            paidSlug
        );
        var legacyPlanId = await db
            .Database.SqlQueryRaw<int>(
                "SELECT id AS \"Value\" FROM plans WHERE slug = {0}",
                legacySlug
            )
            .SingleAsync();
        var paidPlanId = await db
            .Database.SqlQueryRaw<int>(
                "SELECT id AS \"Value\" FROM plans WHERE slug = {0}",
                paidSlug
            )
            .SingleAsync();

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO subscriptions (owner_id, plan_id, status, created_at, created_by) VALUES ({0}, {1}, 3, now(), {0})",
            workspaceLegacy,
            legacyPlanId
        );
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO subscriptions (owner_id, plan_id, status, created_at, created_by) VALUES ({0}, {1}, 1, now(), {0})",
            workspacePaidPending,
            paidPlanId
        );
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO invites (owner_id, code, plan_id, expires_at, max_uses, uses, created_at, created_by) VALUES (NULL, {0}, {1}, now() + interval '7 days', 1, 0, now(), {2})",
            "pginv-" + Guid.NewGuid().ToString("N"),
            paidPlanId,
            Guid.NewGuid()
        );

        // Apply the three DB-20 migrations.
        await db.GetService<IMigrator>().MigrateAsync();

        var legacySub = await db
            .Subscriptions.IgnoreQueryFilters()
            .SingleAsync(s => s.OwnerId == workspaceLegacy);
        Assert.Equal(legacyPlanId, legacySub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, legacySub.Status);
        Assert.Null(legacySub.CurrentPeriodEnd);
        Assert.Null(legacySub.RequestedPlanId);
        Assert.False(legacySub.IsComplimentary);

        var paidSub = await db
            .Subscriptions.IgnoreQueryFilters()
            .SingleAsync(s => s.OwnerId == workspacePaidPending);
        Assert.Equal(paidPlanId, paidSub.PlanId);
        Assert.Equal(SubscriptionStatus.PendingActivation, paidSub.Status);
        Assert.Null(paidSub.CurrentPeriodEnd);
        Assert.Null(paidSub.RequestedPlanId); // old-shape row — no NEW request, still payable via §3.1

        var invite = await db.Invites.IgnoreQueryFilters().SingleAsync(i => i.PlanId == paidPlanId);
        Assert.False(invite.IsComplimentary);
        Assert.Null(invite.CompReason);
        Assert.Null(invite.CompEndsAt);

        // The job pass must leave both rows untouched (NULL current_period_end is never selected).
        var period = new BillingPeriodService(
            new UnitOfWork(db),
            new EntitlementService(new UnitOfWork(db), new FakeCurrentUser(), new FakeSettings()),
            new NoopBillingProvider(),
            new FakeSettings(),
            new MembershipService(new UnitOfWork(db)),
            new NoopBranding(),
            new NoopEmail()
        );
        await period.RunOnceAsync(DateTime.UtcNow);

        var changedCount = await db
            .Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM subscriptions WHERE owner_id IN ({0}, {1}) AND updated_at IS NOT NULL",
                workspaceLegacy,
                workspacePaidPending
            )
            .FirstAsync();
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task TwoParallelRequests_LastCodeSlot_ExactlyOnePendingRedemption()
    {
        if (!Enabled)
            return;

        await using var setup = MakeContext();
        await setup.Database.MigrateAsync();
        // No TRUNCATE here: billing_payments is append-only (refuses TRUNCATE too) and
        // CASCADE would try to reach it via discount_redemptions' FK. Fresh random identifiers
        // per run (workspace/plan/code) keep this test isolated from prior runs' leftover rows.

        var suffix = Guid.NewGuid().ToString("N")[..8];
        // WorkspaceLifecycleGuard.CanManageAsync hardcodes the exact name "Workspace Admin" — must
        // NOT be suffixed. ux_roles_name_owner_live is unique on (name, owner_id) for live rows, so
        // reuse the global row a prior run of this test left behind instead of inserting a duplicate.
        var role =
            await setup
                .Roles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(r =>
                    r.Name == "Workspace Admin" && r.OwnerId == null && r.DeletedAt == null
                )
            ?? new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = null,
            };
        // A missing subscription resolves to Free (EntitlementService.GetFreePlanIdAsync looks up
        // slug "free") — RequestPlanAsync needs a real row to satisfy subscriptions.plan_id's FK.
        var freePlan =
            await setup.Plans.FirstOrDefaultAsync(p => p.Slug == "free" && p.DeletedAt == null)
            ?? new Plan
            {
                Name = "Free",
                Slug = "free",
                IsActive = true,
                Entitlements = new PlanEntitlements(),
            };
        var plan = new Plan
        {
            Name = "Pro " + suffix,
            Slug = "pro-race-" + suffix,
            PriceMonthly = 10,
            Currency = "USD",
            Entitlements = new PlanEntitlements(),
        };
        var code = new DiscountCode
        {
            Code = "RACE" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Kind = DiscountKind.Percent,
            Value = 10,
            IsActive = true,
            MaxRedemptions = 1,
        };
        if (role.Id == 0)
            setup.Roles.Add(role);
        if (freePlan.Id == 0)
            setup.Plans.Add(freePlan);
        setup.Plans.Add(plan);
        setup.DiscountCodes.Add(code);
        await setup.SaveChangesAsync();

        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var adminA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        foreach (var (ws, adminPid) in new[] { (workspaceA, adminA), (workspaceB, adminB) })
        {
            setup.Workspaces.Add(
                new Workspace
                {
                    Id = ws,
                    Name = "Race-" + ws,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = ws,
                }
            );
            // DB-11f: users.role_id is the PLATFORM role now — null for every non-super-admin
            // identity (the tenant role lives on the membership row, set by TestSeed.Join below).
            var user = new User
            {
                Email = $"race-{adminPid:N}@example.com",
                PasswordHash = "h",
                DisplayName = "Admin",
                PublicId = adminPid,
                IsActive = true,
                EmailVerifiedAt = DateTime.UtcNow,
            };
            setup.Users.Add(user);
            await setup.SaveChangesAsync();
            TestSeed.Join(setup, user, ws, role);
        }

        async Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Billing.BillingSummaryResponse>> RequestAsync(
            Guid workspaceId,
            Guid adminPid
        )
        {
            await using var db = MakeContext(
                new FakeCurrentUser
                {
                    Id = adminPid,
                    TenantId = workspaceId,
                    IsSuperAdmin = false,
                }
            );
            return await BillingSvc(
                    db,
                    new FakeCurrentUser
                    {
                        Id = adminPid,
                        TenantId = workspaceId,
                        IsSuperAdmin = false,
                    }
                )
                .RequestPlanAsync(plan.Id, code.Code);
        }

        var results = await Task.WhenAll(
            RequestAsync(workspaceA, adminA),
            RequestAsync(workspaceB, adminB)
        );

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        Assert.Equal(1, results.Count(r => !r.IsSuccess));

        await using var verify = MakeContext();
        var pendingCount = await verify
            .DiscountRedemptions.IgnoreQueryFilters()
            .CountAsync(r =>
                r.DiscountCodeId == code.Id && r.Status == DiscountRedemptionStatus.Pending
            );
        Assert.Equal(1, pendingCount);
    }

    [Fact]
    public async Task TwoParallelPayments_SameWorkspace_ContiguousPeriods_NoOverlap()
    {
        if (!Enabled)
            return;

        await using var setup = MakeContext();
        await setup.Database.MigrateAsync();
        // No TRUNCATE here: billing_payments is append-only. Fresh random workspace/plan
        // identifiers per run keep this test isolated from prior runs' leftover rows.

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var plan = new Plan
        {
            Name = "Pro " + suffix,
            Slug = "pro-race2-" + suffix,
            PriceMonthly = 10,
            Currency = "USD",
            Entitlements = new PlanEntitlements(),
        };
        setup.Plans.Add(plan);
        var workspaceId = Guid.NewGuid();
        setup.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Race2-" + suffix,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        await setup.SaveChangesAsync(); // plan.Id must be assigned before the Subscription below

        setup.Subscriptions.Add(
            new Subscription
            {
                OwnerId = workspaceId,
                PlanId = plan.Id,
                Status = SubscriptionStatus.None,
            }
        );
        await setup.SaveChangesAsync();

        var operatorId = Guid.NewGuid();

        async Task<long> PayAsync()
        {
            await using var db = MakeContext(
                new FakeCurrentUser { IsSuperAdmin = true, Id = operatorId }
            );
            var result = await BillingSvc(
                    db,
                    new FakeCurrentUser { IsSuperAdmin = true, Id = operatorId }
                )
                .RecordPaymentAsync(
                    workspaceId,
                    10m,
                    "USD",
                    DateTime.UtcNow,
                    PaymentMethod.Cash,
                    null,
                    null
                );
            Assert.True(result.IsSuccess, result.Message);
            return result.Data!.Id;
        }

        await Task.WhenAll(PayAsync(), PayAsync());

        await using var verify = MakeContext();
        var payments = await verify
            .BillingPayments.IgnoreQueryFilters()
            .Where(p => p.OwnerId == workspaceId && p.Kind == BillingPaymentKind.Payment)
            .OrderBy(p => p.Id)
            .ToListAsync();
        Assert.Equal(2, payments.Count);
        // Contiguous, no overlap: the second payment's period starts exactly where the first ends.
        Assert.Equal(payments[0].PeriodEnd, payments[1].PeriodStart);
    }
}
