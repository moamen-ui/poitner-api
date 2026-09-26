using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Resources;
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
/// DB-20 §6: BillingMath (test 1), request/quote/code lifecycle (2-3), record payment (4), void (5),
/// append-only (6), complimentary via PATCH plan + invite (7), tenancy (11), workspace payment DTO
/// redaction (14). Fixtures copy MonetizationSignupTests (plans/subscriptions),
/// PlanEnforcementTests.SeedPlanFor, TenantQueryFilterTests (tenant A/B), AuditAppendOnlyGuardTests
/// (SaveChanges guard).
/// </summary>
public class Db20BillingTests
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

    /// <summary>Every BillingService/TenantService write path runs inside
    /// <c>IUnitOfWork.ExecuteInTransactionAsync</c> plus a <c>FOR UPDATE</c> lock marker (the doc's
    /// lock order, §3.6) — both are no-ops under the InMemory provider (<c>Database.IsRelational()
    /// == false</c>, per <c>IUnitOfWork.ExecuteSqlRawAsync</c>'s own doc comment), same as every
    /// other transaction-wrapped service in this suite (Db19NewWorkspaceTests precedent) — Sqlite
    /// would reject `FOR UPDATE` as a syntax error (it is genuinely Postgres-only, proven instead by
    /// the R11 rehearsal / Db20BillingPostgresTests). InMemory's own transaction warning is ignored.</summary>
    private static AppDbContext Ctx(ICurrentUser u, string db) =>
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
            u,
            new ConfigurationBuilder().Build()
        );

    private static BillingService Billing(
        AppDbContext db,
        ICurrentUser user,
        FakeSettings? settings = null,
        IAuditWriter? audit = null
    )
    {
        var uow = new UnitOfWork(db);
        return new BillingService(
            uow,
            user,
            new MembershipService(uow),
            new EntitlementService(uow, user, settings ?? new FakeSettings()),
            new NoopBillingProvider(),
            settings ?? new FakeSettings(),
            audit
        );
    }

    private static TenantService Tenants(
        AppDbContext db,
        ICurrentUser user,
        IAuditWriter? audit = null
    ) =>
        new(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(db)),
            audit,
            user
        );

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;

        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string o, string p, Stream c, string e) =>
            Task.FromResult("");

        public Task DeleteAsync(string x) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string o) => Task.CompletedTask;
    }

    /// <summary>Seeds the global "Workspace Admin" role, a Free plan and a Pro plan (price/currency
    /// configurable), a workspace, and a live Approved Workspace Admin membership for a fresh
    /// identity. Returns everything a test needs to act as that admin or as a super admin.</summary>
    private static (Guid WorkspaceId, Guid AdminPublicId, int FreeId, int ProId) SeedWorkspace(
        string db,
        decimal proPrice = 19.99m,
        string proCurrency = "USD"
    )
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
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
            DisplayState = PlanDisplayState.Visible,
            Entitlements = new PlanEntitlements(),
        };
        var pro = new Plan
        {
            Name = "Pro",
            Slug = "pro",
            IsActive = true,
            DisplayState = PlanDisplayState.Visible,
            PriceMonthly = proPrice,
            Currency = proCurrency,
            Entitlements = new PlanEntitlements(),
        };
        seed.Plans.AddRange(free, pro);
        seed.SaveChanges();

        var workspaceId = Guid.NewGuid();
        var adminPid = Guid.NewGuid();
        var admin = new User
        {
            Email = "admin@" + Guid.NewGuid().ToString("N") + ".com",
            PasswordHash = "h",
            DisplayName = "Admin",
            RoleId = role.Id,
            PublicId = adminPid,
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        seed.Users.Add(admin);
        seed.SaveChanges();
        TestSeed.Join(seed, admin, workspaceId, role);

        return (workspaceId, adminPid, free.Id, pro.Id);
    }

    private static FakeCurrentUser AsAdmin(Guid workspaceId, Guid adminPid) =>
        new() { Id = adminPid, TenantId = workspaceId };

    private static FakeCurrentUser AsSuperAdmin(Guid? operatorId = null) =>
        new() { IsSuperAdmin = true, Id = operatorId ?? Guid.NewGuid() };

    // ── 1. BillingMath ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Discount_Percent_RoundsAwayFromZero()
    {
        var (discount, applicable) = BillingMath.Discount(
            19.99m,
            DiscountKind.Percent,
            15m,
            null,
            "USD"
        );
        Assert.True(applicable);
        Assert.Equal(3.00m, discount);
        Assert.Equal(16.99m, BillingMath.FinalPrice(19.99m, discount));
    }

    [Fact]
    public void Discount_Percent100_FinalIsZero()
    {
        var (discount, applicable) = BillingMath.Discount(
            50m,
            DiscountKind.Percent,
            100m,
            null,
            "USD"
        );
        Assert.True(applicable);
        Assert.Equal(0m, BillingMath.FinalPrice(50m, discount));
    }

    [Fact]
    public void Discount_FixedGreaterThanPrice_FinalIsZero()
    {
        var (discount, applicable) = BillingMath.Discount(
            10m,
            DiscountKind.FixedAmount,
            25m,
            "USD",
            "USD"
        );
        Assert.True(applicable);
        Assert.Equal(10m, discount);
        Assert.Equal(0m, BillingMath.FinalPrice(10m, discount));
    }

    [Fact]
    public void Discount_FixedCurrencyMismatch_NotApplicable()
    {
        var (_, applicable) = BillingMath.Discount(10m, DiscountKind.FixedAmount, 5m, "EUR", "USD");
        Assert.False(applicable);
    }

    [Fact]
    public void PeriodEnd_Monthly_JanuaryThirtyFirst_ClampsToFebruary()
    {
        var start = new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var end = BillingMath.PeriodEnd(start, BillingInterval.Monthly);
        Assert.Equal(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc), end);

        var leapStart = new DateTime(2028, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var leapEnd = BillingMath.PeriodEnd(leapStart, BillingInterval.Monthly);
        Assert.Equal(new DateTime(2028, 2, 29, 0, 0, 0, DateTimeKind.Utc), leapEnd);
    }

    [Fact]
    public void PeriodEnd_Yearly_AddsOneYear()
    {
        var start = new DateTime(2027, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var end = BillingMath.PeriodEnd(start, BillingInterval.Yearly);
        Assert.Equal(new DateTime(2028, 3, 15, 0, 0, 0, DateTimeKind.Utc), end);
    }

    // Review finding #2: the shared UTC-conversion helper every request-DateTime entry point now
    // goes through, mirroring RecordPaymentAsync's own hand-rolled paidAt conversion.
    [Fact]
    public void ToUtc_UnspecifiedKind_ConvertedToUtc()
    {
        var unspecified = DateTime.SpecifyKind(
            new DateTime(2027, 6, 1, 12, 0, 0),
            DateTimeKind.Unspecified
        );
        var converted = BillingMath.ToUtc(unspecified);
        Assert.Equal(DateTimeKind.Utc, converted.Kind);
    }

    [Fact]
    public void ToUtc_LocalKind_ConvertedToUtcEquivalentInstant()
    {
        var local = DateTime.SpecifyKind(new DateTime(2027, 6, 1, 12, 0, 0), DateTimeKind.Local);
        var converted = BillingMath.ToUtc(local);
        Assert.Equal(DateTimeKind.Utc, converted.Kind);
        Assert.Equal(local.ToUniversalTime(), converted);
    }

    [Fact]
    public void ToUtc_Null_ReturnsNull()
    {
        Assert.Null(BillingMath.ToUtc((DateTime?)null));
    }

    // ── 2. Request from Free ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestPlan_FromFree_ParksRequest_EntitlementsStayFree()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, freeId, proId) = SeedWorkspace(db);

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));

        var result = await svc.RequestPlanAsync(proId, null);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(freeId, result.Data!.PlanId);
        Assert.Equal("PendingActivation", result.Data.Status);
        Assert.Equal(proId, result.Data.RequestedPlanId);
        Assert.Equal(19.99m, result.Data.QuotedPrice);
        Assert.Equal("USD", result.Data.QuotedCurrency);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(freeId, sub.PlanId); // entitlements still resolve to Free
    }

    // ── 3. Request with code ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestPlan_WithCode_CreatesPendingRedemptionWithSnapshots()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.DiscountCodes.Add(
                new DiscountCode
                {
                    Code = "SAVE10",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    Duration = DiscountDuration.Once,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));

        var result = await svc.RequestPlanAsync(proId, "save10"); // lower-case matches
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(17.99m, result.Data!.QuotedPrice); // 19.99 - 10% (2.00 rounded)

        var redemption = ctx
            .DiscountRedemptions.IgnoreQueryFilters()
            .Single(r => r.OwnerId == workspaceId);
        Assert.Equal(DiscountRedemptionStatus.Pending, redemption.Status);
        Assert.Equal("SAVE10", redemption.CodeSnapshot);
        Assert.Equal(DiscountKind.Percent, redemption.KindSnapshot);
        Assert.Equal(10m, redemption.ValueSnapshot);
        Assert.Equal(19.99m, redemption.OriginalPrice);
        Assert.Equal(17.99m, redemption.FinalPrice);
    }

    [Fact]
    public async Task RequestPlan_SecondRequest_ReplacesPendingRedemption()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        int codeId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var code = new DiscountCode
            {
                Code = "SAVE10",
                Kind = DiscountKind.Percent,
                Value = 10m,
                Duration = DiscountDuration.Once,
                IsActive = true,
            };
            seed.DiscountCodes.Add(code);
            seed.SaveChanges();
            codeId = code.Id;
        }

        var user = AsAdmin(workspaceId, adminPid);
        using (var ctx1 = Ctx(user, db))
        {
            var svc1 = Billing(ctx1, user);
            var r1 = await svc1.RequestPlanAsync(proId, "SAVE10");
            Assert.True(r1.IsSuccess, r1.Message);
        }

        using (var ctx2 = Ctx(user, db))
        {
            var svc2 = Billing(ctx2, user);
            var r2 = await svc2.RequestPlanAsync(proId, "SAVE10");
            Assert.True(r2.IsSuccess, r2.Message);
        }

        using var verify = Ctx(user, db);
        var rows = verify
            .DiscountRedemptions.IgnoreQueryFilters()
            .Where(r => r.OwnerId == workspaceId)
            .OrderBy(r => r.Id)
            .ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(DiscountRedemptionStatus.Released, rows[0].Status);
        Assert.Equal(RedemptionReleaseReason.Replaced, rows[0].ReleaseReason);
        Assert.Equal(DiscountRedemptionStatus.Pending, rows[1].Status);
    }

    [Fact]
    public async Task RequestPlan_CodeAlreadyApplied_Refused()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var code = new DiscountCode
            {
                Code = "ONCE1",
                Kind = DiscountKind.Percent,
                Value = 10m,
                IsActive = true,
            };
            seed.DiscountCodes.Add(code);
            seed.SaveChanges();
            seed.DiscountRedemptions.Add(
                new DiscountRedemption
                {
                    OwnerId = workspaceId,
                    DiscountCodeId = code.Id,
                    PlanId = proId,
                    Status = DiscountRedemptionStatus.Applied,
                    CodeSnapshot = "ONCE1",
                    KindSnapshot = DiscountKind.Percent,
                    DurationSnapshot = DiscountDuration.Once,
                    ValueSnapshot = 10m,
                    OriginalPrice = 19.99m,
                    DiscountAmount = 2.00m,
                    FinalPrice = 17.99m,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = adminPid,
                    AppliedAt = DateTime.UtcNow,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.RequestPlanAsync(proId, "ONCE1");
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CodeAlreadyUsed, result.Message);
    }

    [Fact]
    public async Task RequestPlan_MaxRedemptionsReached_CodeInvalid()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var code = new DiscountCode
            {
                Code = "MAXED",
                Kind = DiscountKind.Percent,
                Value = 10m,
                IsActive = true,
                MaxRedemptions = 1,
            };
            seed.DiscountCodes.Add(code);
            seed.SaveChanges();
            seed.DiscountRedemptions.Add(
                new DiscountRedemption
                {
                    OwnerId = Guid.NewGuid(),
                    DiscountCodeId = code.Id,
                    PlanId = proId,
                    Status = DiscountRedemptionStatus.Applied,
                    CodeSnapshot = "MAXED",
                    KindSnapshot = DiscountKind.Percent,
                    DurationSnapshot = DiscountDuration.Once,
                    ValueSnapshot = 10m,
                    OriginalPrice = 19.99m,
                    DiscountAmount = 2.00m,
                    FinalPrice = 17.99m,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    AppliedAt = DateTime.UtcNow,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.RequestPlanAsync(proId, "MAXED");
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CodeInvalid, result.Message);
    }

    [Fact]
    public async Task RequestPlan_CodeOutsideWindow_CodeInvalid()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.DiscountCodes.Add(
                new DiscountCode
                {
                    Code = "EXPIRED",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    IsActive = true,
                    ValidUntil = DateTime.UtcNow.AddDays(-1),
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.RequestPlanAsync(proId, "EXPIRED");
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CodeInvalid, result.Message);
    }

    [Fact]
    public async Task RequestPlan_CodeWrongPlanScope_CodeNotForPlan()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, freeId, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var code = new DiscountCode
            {
                Code = "PLANONLY",
                Kind = DiscountKind.Percent,
                Value = 10m,
                IsActive = true,
            };
            seed.DiscountCodes.Add(code);
            seed.SaveChanges();
            seed.Set<DiscountCodePlan>()
                .Add(new DiscountCodePlan { DiscountCodeId = code.Id, PlanId = freeId });
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.RequestPlanAsync(proId, "PLANONLY");
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CodeNotForPlan, result.Message);
    }

    // ── 3b. Quote (review finding #7: QuoteAsync itself was never exercised) ────────────────

    [Fact]
    public async Task QuoteAsync_WithCode_ReturnsDiscountedPrice()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.DiscountCodes.Add(
                new DiscountCode
                {
                    Code = "SAVE10",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    Duration = DiscountDuration.Once,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.QuoteAsync(proId, "save10"); // lower-case matches, same as /request

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(proId, result.Data!.PlanId);
        Assert.Equal(19.99m, result.Data.Price);
        Assert.Equal(2.00m, result.Data.Discount);
        Assert.Equal(17.99m, result.Data.Final);
        Assert.NotNull(result.Data.DiscountCodeId);

        // A pure preview — QuoteAsync must not create/touch any redemption row.
        Assert.Empty(ctx.DiscountRedemptions.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task QuoteAsync_NoCode_ReturnsListPrice()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));

        var result = await svc.QuoteAsync(proId, null);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(19.99m, result.Data!.Price);
        Assert.Equal(0m, result.Data.Discount);
        Assert.Equal(19.99m, result.Data.Final);
        Assert.Null(result.Data.DiscountCodeId);
    }

    [Fact]
    public async Task QuoteAsync_OwnPendingRedemptionOfSameCode_StillSucceeds()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.DiscountCodes.Add(
                new DiscountCode
                {
                    Code = "SAVE10",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    Duration = DiscountDuration.Once,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        var user = AsAdmin(workspaceId, adminPid);
        // Creates a Pending redemption of SAVE10 for this workspace (§3.6a).
        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, "SAVE10")).IsSuccess);

        // Review finding #3 regression: previewing the SAME code again via /quote must not be
        // rejected as CodeAlreadyUsed just because this workspace's own Pending redemption of it
        // already exists — only an Applied redemption is a genuine reuse (consistent with /request,
        // which releases-then-requotes the same Pending row).
        using var ctx = Ctx(user, db);
        var quote = await Billing(ctx, user).QuoteAsync(proId, "SAVE10");
        Assert.True(quote.IsSuccess, quote.Message);
        Assert.Equal(17.99m, quote.Data!.Final);
    }

    [Fact]
    public async Task QuoteAsync_OtherWorkspaceAppliedRedemption_StillBlocksThisWorkspacesOwnApplied()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        int codeId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var code = new DiscountCode
            {
                Code = "ONCE1",
                Kind = DiscountKind.Percent,
                Value = 10m,
                IsActive = true,
            };
            seed.DiscountCodes.Add(code);
            seed.SaveChanges();
            codeId = code.Id;
            seed.DiscountRedemptions.Add(
                new DiscountRedemption
                {
                    OwnerId = workspaceId,
                    DiscountCodeId = code.Id,
                    PlanId = proId,
                    Status = DiscountRedemptionStatus.Applied,
                    CodeSnapshot = "ONCE1",
                    KindSnapshot = DiscountKind.Percent,
                    DurationSnapshot = DiscountDuration.Once,
                    ValueSnapshot = 10m,
                    OriginalPrice = 19.99m,
                    DiscountAmount = 2.00m,
                    FinalPrice = 17.99m,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = adminPid,
                    AppliedAt = DateTime.UtcNow,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.QuoteAsync(proId, "ONCE1");
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CodeAlreadyUsed, result.Message);
    }

    // ── 3c. GET /api/admin/billing/plans (dashboard gap fix) ────────────────────────────────

    [Fact]
    public async Task ListRequestablePlans_FiltersHiddenInactiveFreeAndComingSoonExcludedByPrice()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, freeId, proId) = SeedWorkspace(db);
        int hiddenId,
            inactiveId,
            enterpriseId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var hidden = new Plan
            {
                Name = "Internal",
                Slug = "internal",
                IsActive = true,
                DisplayState = PlanDisplayState.Hidden,
                PriceMonthly = 999m,
                Currency = "USD",
                SortOrder = 1,
                Entitlements = new PlanEntitlements(),
            };
            var inactive = new Plan
            {
                Name = "Retired",
                Slug = "retired",
                IsActive = false,
                DisplayState = PlanDisplayState.Visible,
                PriceMonthly = 29.99m,
                Currency = "USD",
                SortOrder = 2,
                Entitlements = new PlanEntitlements(),
            };
            var enterprise = new Plan
            {
                Name = "Enterprise",
                Slug = "enterprise",
                IsActive = true,
                DisplayState = PlanDisplayState.Visible,
                PriceMonthly = 499m,
                Currency = "USD",
                SortOrder = 3,
                FeatureBullets = new List<string> { "SSO", "Priority support" },
                Entitlements = new PlanEntitlements(),
            };
            seed.Plans.AddRange(hidden, inactive, enterprise);
            seed.SaveChanges();
            hiddenId = hidden.Id;
            inactiveId = inactive.Id;
            enterpriseId = enterprise.Id;
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.ListRequestablePlansAsync();

        Assert.True(result.IsSuccess, result.Message);
        var ids = result.Data!.Select(p => p.Id).ToList();
        // Free (price 0) is excluded — same PriceMonthly > 0 rule QuoteAsync/RequestPlanAsync apply.
        Assert.DoesNotContain(freeId, ids);
        Assert.DoesNotContain(hiddenId, ids);
        Assert.DoesNotContain(inactiveId, ids);
        Assert.Contains(proId, ids);
        Assert.Contains(enterpriseId, ids);

        // Ordered by SortOrder — Pro (seeded first, SortOrder default 0) before Enterprise (3).
        Assert.True(ids.IndexOf(proId) < ids.IndexOf(enterpriseId));

        var enterprisePlan = result.Data.Single(p => p.Id == enterpriseId);
        Assert.Equal("enterprise", enterprisePlan.Slug);
        Assert.Equal(499m, enterprisePlan.Price);
        Assert.Equal("USD", enterprisePlan.Currency);
        Assert.Equal("Monthly", enterprisePlan.Interval);
        Assert.Equal(new List<string> { "SSO", "Priority support" }, enterprisePlan.FeatureBullets);
    }

    [Fact]
    public async Task ListRequestablePlans_MarksTheWorkspacesCurrentPlan()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var ctx1 = Ctx(AsAdmin(workspaceId, adminPid), db))
            Assert.True(
                (
                    await Billing(ctx1, AsAdmin(workspaceId, adminPid))
                        .RequestPlanAsync(proId, null)
                ).IsSuccess
            );
        using (var ctx2 = Ctx(AsSuperAdmin(), db))
        {
            var pay = await Billing(ctx2, AsSuperAdmin())
                .RecordPaymentAsync(
                    workspaceId,
                    19.99m,
                    null,
                    DateTime.UtcNow,
                    PaymentMethod.Cash,
                    null,
                    null
                );
            Assert.True(pay.IsSuccess, pay.Message);
        }

        using var ctx = Ctx(AsAdmin(workspaceId, adminPid), db);
        var svc = Billing(ctx, AsAdmin(workspaceId, adminPid));
        var result = await svc.ListRequestablePlansAsync();

        Assert.True(result.IsSuccess, result.Message);
        var pro = result.Data!.Single(p => p.Id == proId);
        Assert.True(pro.IsCurrent);
    }

    [Fact]
    public async Task ListRequestablePlans_NotAWorkspaceAdmin_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        SeedWorkspace(db);
        using var ctx = Ctx(new FakeCurrentUser(), db); // no TenantId/Id resolved at all
        var svc = Billing(ctx, new FakeCurrentUser());
        var result = await svc.ListRequestablePlansAsync();
        Assert.True(result.IsForbidden, result.Message);
    }

    // ── 4. Record payment ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordPayment_FirstPayment_GrantsPlan_AppliesRedemption()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var user = AsAdmin(workspaceId, adminPid);

        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, null)).IsSuccess);

        var operatorId = Guid.NewGuid();
        // F-B4: a FIRST payment starts the period at recording time (real UtcNow), not at the
        // operator-entered paidAt — bracket the service's own "now" between two real timestamps
        // taken immediately around the call rather than asserting an exact, unreproducible instant.
        var before = DateTime.UtcNow;
        using var ctx = Ctx(AsSuperAdmin(operatorId), db);
        var svc = Billing(ctx, AsSuperAdmin(operatorId));

        var result = await svc.RecordPaymentAsync(
            workspaceId,
            19.99m,
            null,
            DateTime.UtcNow,
            PaymentMethod.Cash,
            "REF1",
            "paid by wire"
        );
        var after = DateTime.UtcNow;
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(BillingPaymentKind.Payment.ToString(), result.Data!.Kind);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(proId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Null(sub.RequestedPlanId);
        Assert.Equal("manual", sub.BillingProvider);

        Assert.InRange(
            sub.CurrentPeriodEnd!.Value,
            BillingMath.PeriodEnd(before, BillingInterval.Monthly),
            BillingMath.PeriodEnd(after, BillingInterval.Monthly)
        );
    }

    [Fact]
    public async Task RecordPayment_RenewalOnActive_StartsAtOldPeriodEnd()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var oldEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    CurrentPeriodEnd = oldEnd,
                    BillingProvider = "manual",
                }
            );
            seed.SaveChanges();
        }

        var operatorId = Guid.NewGuid();
        var now = DateTime.UtcNow; // renews early — stacks (paidAt only needs to pass the window check)
        using var ctx = Ctx(AsSuperAdmin(operatorId), db);
        var svc = Billing(ctx, AsSuperAdmin(operatorId));
        var result = await svc.RecordPaymentAsync(
            workspaceId,
            19.99m,
            null,
            now,
            PaymentMethod.BankTransfer,
            null,
            null
        );
        Assert.True(result.IsSuccess, result.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(BillingMath.PeriodEnd(oldEnd, BillingInterval.Monthly), sub.CurrentPeriodEnd);
    }

    [Fact]
    public async Task RecordPayment_RenewalOnPastDue_ContiguousFromOldEnd()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var oldEnd = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.PastDue,
                    CurrentPeriodEnd = oldEnd,
                    BillingProvider = "manual",
                }
            );
            seed.SaveChanges();
        }

        var operatorId = Guid.NewGuid();
        var now = DateTime.UtcNow; // late payer (paidAt only needs to pass the window check)
        using var ctx = Ctx(AsSuperAdmin(operatorId), db);
        var svc = Billing(ctx, AsSuperAdmin(operatorId));
        var result = await svc.RecordPaymentAsync(
            workspaceId,
            19.99m,
            null,
            now,
            PaymentMethod.Cash,
            null,
            null
        );
        Assert.True(result.IsSuccess, result.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(BillingMath.PeriodEnd(oldEnd, BillingInterval.Monthly), sub.CurrentPeriodEnd);
    }

    [Fact]
    public async Task RecordPayment_Complimentary_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    IsComplimentary = true,
                    CompedAt = DateTime.UtcNow,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Billing(ctx, AsSuperAdmin());
        var result = await svc.RecordPaymentAsync(
            workspaceId,
            10m,
            null,
            DateTime.UtcNow,
            PaymentMethod.Cash,
            null,
            null
        );
        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.Billing.Complimentary, result.Message);
    }

    [Fact]
    public async Task RecordPayment_NoSubscription_NothingToPay()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, _, _) = SeedWorkspace(db);
        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Billing(ctx, AsSuperAdmin());
        var result = await svc.RecordPaymentAsync(
            workspaceId,
            10m,
            null,
            DateTime.UtcNow,
            PaymentMethod.Cash,
            null,
            null
        );
        Assert.True(result.IsNotFound);
        Assert.Equal(MessageKeys.Billing.NothingToPay, result.Message);
    }

    // ── 5. Void ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task VoidPayment_RestoresPreviousState_ReleasesRedemption()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, freeId, proId) = SeedWorkspace(db);
        var user = AsAdmin(workspaceId, adminPid);

        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, null)).IsSuccess);

        long paymentId;
        using (var ctx2 = Ctx(AsSuperAdmin(), db))
        {
            var svc2 = Billing(ctx2, AsSuperAdmin());
            var pay = await svc2.RecordPaymentAsync(
                workspaceId,
                19.99m,
                null,
                DateTime.UtcNow,
                PaymentMethod.Cash,
                null,
                null
            );
            Assert.True(pay.IsSuccess, pay.Message);
            paymentId = pay.Data!.Id;
        }

        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Billing(ctx, AsSuperAdmin());
        var voidResult = await svc.VoidPaymentAsync(workspaceId, paymentId, "refund requested");
        Assert.True(voidResult.IsSuccess, voidResult.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        // PendingActivation on Free before the payment → restores to None (not PendingActivation).
        Assert.Equal(freeId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.None, sub.Status);

        var voidRow = ctx
            .BillingPayments.IgnoreQueryFilters()
            .Single(p => p.Kind == BillingPaymentKind.Void);
        Assert.Equal(paymentId, voidRow.VoidsPaymentId);
    }

    [Fact]
    public async Task VoidPayment_SecondVoidOfSamePayment_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var user = AsAdmin(workspaceId, adminPid);
        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, null)).IsSuccess);

        long paymentId;
        using (var ctx2 = Ctx(AsSuperAdmin(), db))
        {
            var pay = await Billing(ctx2, AsSuperAdmin())
                .RecordPaymentAsync(
                    workspaceId,
                    19.99m,
                    null,
                    DateTime.UtcNow,
                    PaymentMethod.Cash,
                    null,
                    null
                );
            paymentId = pay.Data!.Id;
        }

        using (var ctx3 = Ctx(AsSuperAdmin(), db))
            Assert.True(
                (
                    await Billing(ctx3, AsSuperAdmin())
                        .VoidPaymentAsync(workspaceId, paymentId, "r1")
                ).IsSuccess
            );

        using var ctx = Ctx(AsSuperAdmin(), db);
        var second = await Billing(ctx, AsSuperAdmin())
            .VoidPaymentAsync(workspaceId, paymentId, "r2");
        Assert.True(second.IsConflict);
        Assert.Equal(MessageKeys.Billing.VoidOnlyLatest, second.Message);
    }

    // Review finding #1 regression: a voided Payment row keeps Kind == Payment forever (append-only
    // ledger), so once the NEWEST payment (P2) is voided, the next-newest (P1) — now the latest
    // NON-voided payment — must itself become voidable, not permanently shadowed by P2.
    [Fact]
    public async Task VoidPayment_P1ThenP2_VoidP2ThenP1_BothSucceed()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var user = AsAdmin(workspaceId, adminPid);
        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, null)).IsSuccess);

        long p1Id;
        using (var ctx2 = Ctx(AsSuperAdmin(), db))
        {
            var pay1 = await Billing(ctx2, AsSuperAdmin())
                .RecordPaymentAsync(
                    workspaceId,
                    19.99m,
                    null,
                    DateTime.UtcNow,
                    PaymentMethod.Cash,
                    null,
                    null
                );
            Assert.True(pay1.IsSuccess, pay1.Message);
            p1Id = pay1.Data!.Id;
        }

        long p2Id;
        using (var ctx3 = Ctx(AsSuperAdmin(), db))
        {
            // A renewal payment for the same plan — sub is already Active on `proId` with a
            // CurrentPeriodEnd, so this is accepted as a contiguous renewal, not a new request.
            var pay2 = await Billing(ctx3, AsSuperAdmin())
                .RecordPaymentAsync(
                    workspaceId,
                    19.99m,
                    null,
                    DateTime.UtcNow,
                    PaymentMethod.Cash,
                    null,
                    null
                );
            Assert.True(pay2.IsSuccess, pay2.Message);
            p2Id = pay2.Data!.Id;
        }

        using (var ctx4 = Ctx(AsSuperAdmin(), db))
        {
            var voidP2 = await Billing(ctx4, AsSuperAdmin())
                .VoidPaymentAsync(workspaceId, p2Id, "void p2");
            Assert.True(voidP2.IsSuccess, voidP2.Message);
        }

        using var ctx = Ctx(AsSuperAdmin(), db);
        var voidP1 = await Billing(ctx, AsSuperAdmin())
            .VoidPaymentAsync(workspaceId, p1Id, "void p1");
        Assert.True(voidP1.IsSuccess, voidP1.Message);
    }

    // ── 6. Append-only ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ModifiedBillingPayment_SaveThrows()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var payment = new BillingPayment
        {
            PlanId = 1,
            Kind = BillingPaymentKind.Void,
            Amount = 10m,
            Currency = "USD",
            VoidsPaymentId = 999,
            Note = "x",
            RecordedAt = DateTime.UtcNow,
            RecordedBy = Guid.NewGuid(),
        };
        ctx.BillingPayments.Add(payment);
        await ctx.SaveChangesAsync();

        ctx.Entry(payment).State = EntityState.Modified;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task RemovedBillingPayment_SaveThrows()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var payment = new BillingPayment
        {
            PlanId = 1,
            Kind = BillingPaymentKind.Void,
            Amount = 10m,
            Currency = "USD",
            VoidsPaymentId = 999,
            RecordedAt = DateTime.UtcNow,
            RecordedBy = Guid.NewGuid(),
        };
        ctx.BillingPayments.Add(payment);
        await ctx.SaveChangesAsync();

        ctx.BillingPayments.Remove(payment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
    }

    // ── 7. Complimentary ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePlan_PaidPlan_GrantsComplimentary_ClearsPendingRequest()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, proId) = SeedWorkspace(db);
        var user = AsAdmin(workspaceId, adminPid);
        using (var ctx1 = Ctx(user, db))
            Assert.True((await Billing(ctx1, user).RequestPlanAsync(proId, null)).IsSuccess);

        var operatorId = Guid.NewGuid();
        using var ctx = Ctx(AsSuperAdmin(operatorId), db);
        var svc = Tenants(ctx, AsSuperAdmin(operatorId));
        var result = await svc.ChangePlanAsync(workspaceId, proId, "VIP", null);
        Assert.True(result.IsSuccess, result.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.True(sub.IsComplimentary);
        Assert.Equal(operatorId, sub.CompedBy);
        Assert.Equal("VIP", sub.CompReason);
        Assert.Null(sub.CurrentPeriodEnd);
        Assert.Null(sub.RequestedPlanId);

        var released = ctx
            .DiscountRedemptions.IgnoreQueryFilters()
            .Where(r => r.OwnerId == workspaceId)
            .ToList();
        // No code was used in this test — nothing to release, but the request itself is cleared.
        Assert.Empty(released);
    }

    [Fact]
    public async Task ChangePlan_HiddenPlan_Allowed()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, _, _) = SeedWorkspace(db);
        int hiddenPlanId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var hidden = new Plan
            {
                Name = "Enterprise",
                Slug = "enterprise",
                IsActive = false,
                DisplayState = PlanDisplayState.Hidden,
                PriceMonthly = 500m,
                Currency = "USD",
                Entitlements = new PlanEntitlements(),
            };
            seed.Plans.Add(hidden);
            seed.SaveChanges();
            hiddenPlanId = hidden.Id;
        }

        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Tenants(ctx, AsSuperAdmin());
        var result = await svc.ChangePlanAsync(workspaceId, hiddenPlanId);
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task ChangePlan_FreePlan_ClearsComp()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, adminPid, freeId, proId) = SeedWorkspace(db);
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceId,
                    PlanId = proId,
                    Status = SubscriptionStatus.Active,
                    IsComplimentary = true,
                    CompedAt = DateTime.UtcNow,
                    CompedBy = Guid.NewGuid(),
                    CompReason = "VIP",
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Tenants(ctx, AsSuperAdmin());
        var result = await svc.ChangePlanAsync(workspaceId, freeId);
        Assert.True(result.IsSuccess, result.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.False(sub.IsComplimentary);
        Assert.Null(sub.CompedBy);
        Assert.Null(sub.CompReason);
    }

    // ── Review findings #2, #4, #5: ChangePlanAsync comp-field validation/UTC/operator id ───

    [Fact]
    public async Task ChangePlan_CompReasonTooLong_Rejected()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, _, proId) = SeedWorkspace(db);
        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Tenants(ctx, AsSuperAdmin());

        var result = await svc.ChangePlanAsync(workspaceId, proId, new string('x', 201), null);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CompReasonTooLong, result.Message);

        // Nothing was persisted — no half-applied plan change.
        Assert.Empty(ctx.Subscriptions.IgnoreQueryFilters().Where(s => s.OwnerId == workspaceId));
    }

    [Fact]
    public async Task ChangePlan_CompEndsAtInPast_Rejected()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, _, proId) = SeedWorkspace(db);
        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Tenants(ctx, AsSuperAdmin());

        var result = await svc.ChangePlanAsync(
            workspaceId,
            proId,
            null,
            DateTime.UtcNow.AddDays(-1)
        );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Billing.CompEndsAtMustBeFuture, result.Message);
    }

    [Fact]
    public async Task ChangePlan_PaidPlan_NoResolvedOperatorId_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, _, proId) = SeedWorkspace(db);
        // Same "nullable-with-default" ICurrentUser seam TenantService's constructor comment
        // describes — but a paid plan now REQUIRES a resolved operator id (ck_subscriptions_comp_
        // consistent needs comped_by whenever is_complimentary is true), so this must be Forbidden,
        // never a DbUpdateException from the check constraint.
        var noOperator = new FakeCurrentUser { IsSuperAdmin = true }; // Id left null
        using var ctx = Ctx(noOperator, db);
        var svc = Tenants(ctx, noOperator);

        var result = await svc.ChangePlanAsync(workspaceId, proId, "VIP", null);
        Assert.True(result.IsForbidden, result.Message);
        Assert.Equal(MessageKeys.Common.Forbidden, result.Message);
        Assert.Empty(ctx.Subscriptions.IgnoreQueryFilters().Where(s => s.OwnerId == workspaceId));
    }

    [Fact]
    public async Task ChangePlan_PaidPlan_CompEndsAtConvertedToUtc()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceId, _, _, proId) = SeedWorkspace(db);
        using var ctx = Ctx(AsSuperAdmin(), db);
        var svc = Tenants(ctx, AsSuperAdmin());

        // Review finding #2: simulates the Unspecified kind System.Text.Json produces for an
        // offset-less ISO timestamp — Npgsql would otherwise throw InvalidCastException persisting
        // this straight through to subscriptions.comp_ends_at (timestamptz).
        var unspecified = DateTime.SpecifyKind(
            DateTime.UtcNow.AddDays(30),
            DateTimeKind.Unspecified
        );

        var result = await svc.ChangePlanAsync(workspaceId, proId, "VIP", unspecified);
        Assert.True(result.IsSuccess, result.Message);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == workspaceId);
        Assert.Equal(DateTimeKind.Utc, sub.CompEndsAt!.Value.Kind);
    }

    // ── 11. Tenancy (R8.5) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void TenantB_ReadsNoBillingPaymentOrRedemptionOfTenantA()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.AddRange(
                new Workspace
                {
                    Id = tenantA,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantA,
                },
                new Workspace
                {
                    Id = tenantB,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantB,
                }
            );
            seed.Plans.Add(
                new Plan
                {
                    Name = "Pro",
                    Slug = "pro",
                    PriceMonthly = 10,
                    Currency = "USD",
                    Entitlements = new PlanEntitlements(),
                }
            );
            seed.SaveChanges();
            var planId = seed.Plans.Single().Id;

            seed.BillingPayments.Add(
                new BillingPayment
                {
                    OwnerId = tenantA,
                    PlanId = planId,
                    Kind = BillingPaymentKind.Payment,
                    Amount = 10,
                    Currency = "USD",
                    Method = PaymentMethod.Cash,
                    PaidAt = DateTime.UtcNow,
                    PeriodStart = DateTime.UtcNow,
                    PeriodEnd = DateTime.UtcNow.AddMonths(1),
                    PreviousPlanId = planId,
                    PreviousStatus = SubscriptionStatus.None,
                    RecordedAt = DateTime.UtcNow,
                    RecordedBy = Guid.NewGuid(),
                }
            );
            seed.DiscountRedemptions.Add(
                new DiscountRedemption
                {
                    OwnerId = tenantA,
                    DiscountCodeId = 1,
                    PlanId = planId,
                    Status = DiscountRedemptionStatus.Pending,
                    CodeSnapshot = "X",
                    OriginalPrice = 10,
                    FinalPrice = 10,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                }
            );
            seed.SaveChanges();
        }

        using var asB = Ctx(new FakeCurrentUser { TenantId = tenantB }, db);
        Assert.Empty(asB.BillingPayments);
        Assert.Empty(asB.DiscountRedemptions);

        using var asSuper = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        Assert.Single(asSuper.BillingPayments.IgnoreQueryFilters());
        Assert.Single(asSuper.DiscountRedemptions.IgnoreQueryFilters());
    }

    // ── 14. Workspace payment DTO redaction ─────────────────────────────────────────────────

    [Fact]
    public async Task WorkspacePaymentResponse_OmitsNoteAndRecordedBy()
    {
        var props =
            typeof(Pointer.Application.DTOs.Billing.WorkspacePaymentResponse).GetProperties();
        Assert.DoesNotContain(props, p => p.Name == "Note");
        Assert.DoesNotContain(props, p => p.Name == "RecordedBy");
        await Task.CompletedTask;
    }
}
