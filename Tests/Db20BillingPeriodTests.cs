using Microsoft.EntityFrameworkCore;
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

/// <summary>DB-20 §3.6f / §6 test 9: the period job's four passes, each in its own transaction
/// (lock, reload, re-check, act). Uses the InMemory provider with
/// <c>InMemoryEventId.TransactionIgnoredWarning</c> suppressed — same as Db20BillingTests — since
/// <c>ExecuteInTransactionAsync</c> + its <c>FOR UPDATE</c> lock marker are Postgres-only and no-op
/// under InMemory.</summary>
public class Db20BillingPeriodTests
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
        public int GraceDays { get; set; } = 7;
        public int ReminderDays { get; set; } = 3;

        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) =>
            Task.FromResult(
                key == ISettingsService.BillingGraceDays ? GraceDays
                : key == ISettingsService.BillingReminderDays ? ReminderDays
                : fallback
            );

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private sealed class NoopEmail : IEmailService
    {
        public int SentCount;

        public Task<bool> SendAsync(
            string to,
            string subject,
            string html,
            CancellationToken ct = default
        )
        {
            SentCount++;
            return Task.FromResult(true);
        }
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

    private static AppDbContext Ctx(string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .InMemoryEventId
                            .TransactionIgnoredWarning
                    )
                )
                .Options,
            new FakeCurrentUser(),
            new ConfigurationBuilder().Build()
        );

    private static BillingPeriodService Period(
        AppDbContext db,
        FakeSettings settings,
        NoopEmail email,
        IAuditWriter? audit = null
    )
    {
        var uow = new UnitOfWork(db);
        var user = new FakeCurrentUser();
        return new BillingPeriodService(
            uow,
            new EntitlementService(uow, user, settings),
            new NoopBillingProvider(),
            settings,
            new MembershipService(uow),
            new NoopBranding(),
            email,
            null,
            audit
        );
    }

    private static (Guid WorkspaceId, int FreeId, int ProId, int RoleId) Seed(string db)
    {
        using var seed = Ctx(db);
        var role = new Role
        {
            Name = "Workspace Admin",
            GrantsAdmin = true,
            IsActive = true,
            OwnerId = null,
        };
        seed.Roles.Add(role);
        var free = new Plan
        {
            Name = "Free",
            Slug = "free",
            IsActive = true,
            Entitlements = new PlanEntitlements(),
        };
        var pro = new Plan
        {
            Name = "Pro",
            Slug = "pro",
            IsActive = true,
            PriceMonthly = 20m,
            Currency = "USD",
            Entitlements = new PlanEntitlements(),
        };
        seed.Plans.AddRange(free, pro);
        seed.SaveChanges();

        var workspaceId = Guid.NewGuid();
        seed.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "W",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        seed.SaveChanges();

        var admin = new User
        {
            Email = "admin@" + Guid.NewGuid().ToString("N") + ".com",
            PasswordHash = "h",
            DisplayName = "Admin",
            RoleId = role.Id,
            PublicId = Guid.NewGuid(),
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        seed.Users.Add(admin);
        seed.SaveChanges();
        TestSeed.Join(seed, admin, workspaceId, role);

        return (workspaceId, free.Id, pro.Id, role.Id);
    }

    // ── h1: comp end ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CompEnd_PastDeadline_ClearsComp_SetsPastDue()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, proId, _) = Seed(db);
        var compEndsAt = DateTime.UtcNow.AddDays(-1);
        using (var seed = Ctx(db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    IsComplimentary = true,
                    CompedAt = DateTime.UtcNow.AddDays(-30),
                    CompEndsAt = compEndsAt,
                }
            );
            seed.SaveChanges();
        }

        var settings = new FakeSettings();
        var email = new NoopEmail();
        using var ctx = Ctx(db);
        await Period(ctx, settings, email).RunOnceAsync(DateTime.UtcNow);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.False(sub.IsComplimentary);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.Equal(compEndsAt, sub.CurrentPeriodEnd);
    }

    // ── h2: reminder ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reminder_SentOnce()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, proId, _) = Seed(db);
        using (var seed = Ctx(db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    CurrentPeriodEnd = DateTime.UtcNow.AddDays(2), // inside the 3-day reminder window
                }
            );
            seed.SaveChanges();
        }

        var settings = new FakeSettings();
        var email = new NoopEmail();
        using (var ctx1 = Ctx(db))
            await Period(ctx1, settings, email).RunOnceAsync(DateTime.UtcNow);

        Assert.Equal(1, email.SentCount);
        using (var verify1 = Ctx(db))
        {
            var sub = verify1
                .Subscriptions.IgnoreQueryFilters()
                .Single(s => s.OwnerId == workspaceId);
            Assert.NotNull(sub.RenewalReminderSentAt);
        }

        // Second pass, same "now" — idempotent (already sent, no second e-mail).
        using (var ctx2 = Ctx(db))
            await Period(ctx2, settings, email).RunOnceAsync(DateTime.UtcNow);
        Assert.Equal(1, email.SentCount);
    }

    // ── h3: past due ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PastDue_PeriodEndPassed_FlipsStatus()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, proId, _) = Seed(db);
        using (var seed = Ctx(db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    CurrentPeriodEnd = DateTime.UtcNow.AddDays(-1),
                }
            );
            seed.SaveChanges();
        }

        var settings = new FakeSettings();
        using var ctx = Ctx(db);
        await Period(ctx, settings, new NoopEmail()).RunOnceAsync(DateTime.UtcNow);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
    }

    // ── h4: downgrade ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Downgrade_GraceElapsed_MovesToFree_KeepsData()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, freeId, proId, _) = Seed(db);
        using (var seed = Ctx(db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.PastDue,
                    CurrentPeriodEnd = DateTime.UtcNow.AddDays(-8), // 7-day grace elapsed
                }
            );
            seed.Projects.Add(new Project { OwnerId = workspaceId, Name = "Kept project" });
            seed.SaveChanges();
        }

        var settings = new FakeSettings();
        using var ctx = Ctx(db);
        await Period(ctx, settings, new NoopEmail()).RunOnceAsync(DateTime.UtcNow);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(freeId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.None, sub.Status);
        Assert.Null(sub.CurrentPeriodEnd);

        // Data above the Free cap is kept (grandfather-safe creation checks, not a data sweep).
        Assert.Single(ctx.Projects.IgnoreQueryFilters().Where(p => p.OwnerId == workspaceId));
    }

    // ── NULL current_period_end rows are never selected ─────────────────────────────────────

    [Fact]
    public async Task LegacyRow_NullPeriodEnd_UntouchedAfter100Days()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, proId, _) = Seed(db);
        using (var seed = Ctx(db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    CurrentPeriodEnd = null, // Legacy / pre-BILL-1 shape
                }
            );
            seed.SaveChanges();
        }

        var settings = new FakeSettings();
        using var ctx = Ctx(db);
        // Simulate the job running "100 days" later — a NULL CurrentPeriodEnd is never selected by
        // any of the four passes' predicates, so nothing changes regardless of `now`.
        await Period(ctx, settings, new NoopEmail()).RunOnceAsync(DateTime.UtcNow.AddDays(100));

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(proId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Null(sub.CurrentPeriodEnd);
    }
}
