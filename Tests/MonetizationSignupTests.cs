using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
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

/// <summary>Signup plan selector, tenant ChangePlanAsync, and the Noop billing seam (zero HTTP).</summary>
public class MonetizationSignupTests
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

    // Signup requires the ScopedAdminSignupEnabled flag on.
    private sealed class SignupEnabledSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(key == ISettingsService.ScopedAdminSignupEnabled ? true : fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string p) => "h:" + p;

        public bool Verify(string p, string h) => h == "h:" + p;
    }

    private sealed class FakeToken : ITokenService
    {
        public string Issue(User u, WorkspaceMembership? membership, int? keyScopes = null) => "t";

        public string IssueSelection(User u) => "sel";

        public string IssueImpersonation(User u, Guid w, long s, DateTime e) =>
            "imp-for-" + u.Email;
    }

    private sealed class FakeReset : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";

        public bool TryValidate(string token, out Guid id, out Guid stamp)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            return false;
        }

        public string CreateScoped(Guid id, Guid stamp, string purpose, string? payload = null) => "r";

        public bool TryValidateScoped(string token, string purpose, out Guid id, out Guid stamp, out string? payload)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            payload = null;
            return false;
        }
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

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse DefaultBranding() =>
            new()
            {
                ProductName = "Pointer",
                Tagline = string.Empty,
                PrimaryColor = "#2563eb",
                Urls = new Pointer.Application.DTOs.Branding.BrandingUrlsResponse
                {
                    App = "https://app.pointer.moamen.work",
                },
                Assets = new Pointer.Application.DTOs.Branding.BrandingAssetsResponse(),
            };

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(DefaultBranding());
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string o, string p, Stream c, string e) =>
            Task.FromResult("");

        public Task DeleteAsync(string x) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string o) => Task.CompletedTask;
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static void SeedRolesAndPlans(string db, out int proId)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        seed.Roles.Add(
            new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = null,
            }
        );
        seed.Plans.Add(
            new Plan
            {
                Name = "Free",
                Slug = "free",
                Entitlements = new PlanEntitlements(),
            }
        );
        var pro = new Plan
        {
            Name = "Pro",
            Slug = "pro",
            IsActive = true,
            DisplayState = PlanDisplayState.Visible,
            Entitlements = new PlanEntitlements(),
        };
        seed.Plans.Add(pro);
        seed.SaveChanges();
        proId = pro.Id;
    }

    private static AuthService Auth(AppDbContext db, ICurrentUser user)
    {
        var uow = new UnitOfWork(db);
        return new AuthService(
            uow,
            new IdentityHasher(),
            new FakeToken(),
            user,
            new SignupEnabledSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );
    }

    [Fact]
    public async Task Signup_WithPaidPlan_CreatesPendingActivationSubscription()
    {
        var db = Guid.NewGuid().ToString();
        SeedRolesAndPlans(db, out var proId);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var auth = Auth(ctx, anon);

        var res = await auth.RegisterAdminAsync(
            new RegisterAdminRequest
            {
                Email = "paid@a.com",
                Password = "password123",
                DisplayName = "Paid",
                PlanId = proId,
            }
        );
        Assert.True(res.IsSuccess);

        var newUser = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "paid@a.com");
        // DB-11a: the subscription is keyed by the WORKSPACE id, which no longer equals the admin's
        // public_id — resolve it via the identity's (freshly minted) membership.
        var membership = ctx.Set<Pointer.Domain.Entity.WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == newUser.Id && m.LeftAt == null);
        var sub = ctx
            .Subscriptions.IgnoreQueryFilters()
            .Single(s => s.OwnerId == membership.OwnerId);
        Assert.Equal(proId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.PendingActivation, sub.Status);
    }

    [Fact]
    public async Task Signup_WithoutPlan_CreatesNoSubscription_FreeFlow()
    {
        var db = Guid.NewGuid().ToString();
        SeedRolesAndPlans(db, out _);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var auth = Auth(ctx, anon);

        var res = await auth.RegisterAdminAsync(
            new RegisterAdminRequest
            {
                Email = "free@a.com",
                Password = "password123",
                DisplayName = "Free",
            }
        );
        Assert.True(res.IsSuccess);

        var newUser = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "free@a.com");
        Assert.Empty(
            ctx.Subscriptions.IgnoreQueryFilters().Where(s => s.OwnerId == newUser.PublicId)
        );
    }

    [Fact]
    public async Task Signup_WithFreePlanId_CreatesNoSubscription()
    {
        var db = Guid.NewGuid().ToString();
        SeedRolesAndPlans(db, out _);
        int freeId = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db)
            .Plans.Single(p => p.Slug == "free")
            .Id;

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var auth = Auth(ctx, anon);

        await auth.RegisterAdminAsync(
            new RegisterAdminRequest
            {
                Email = "freeid@a.com",
                Password = "password123",
                DisplayName = "F",
                PlanId = freeId,
            }
        );

        var newUser = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "freeid@a.com");
        Assert.Empty(
            ctx.Subscriptions.IgnoreQueryFilters().Where(s => s.OwnerId == newUser.PublicId)
        );
    }

    [Fact]
    public async Task NoopBilling_Activate_FlipsPendingToActive_ZeroHttp()
    {
        var provider = new NoopBillingProvider();
        var sub = new Subscription { Status = SubscriptionStatus.PendingActivation };
        await provider.ActivateAsync(sub); // no HttpClient touched anywhere in NoopBillingProvider
        Assert.Equal(SubscriptionStatus.Active, sub.Status);

        await provider.CancelAsync(sub);
        Assert.Equal(SubscriptionStatus.Canceled, sub.Status);
    }

    [Fact]
    public async Task ChangePlan_UpsertsSubscription_ViaBillingSeam()
    {
        var db = Guid.NewGuid().ToString();
        SeedRolesAndPlans(db, out var proId);

        // Seed a tenant (workspace-admin user owning itself).
        Guid tenantPid;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var waRole = seed.Roles.Single(r => r.Name == "Workspace Admin");
            var pid = Guid.NewGuid();
            var tenantUser = new User
            {
                Email = "t@a.com",
                PasswordHash = "x",
                DisplayName = "T",
                RoleId = waRole.Id,
                PublicId = pid,
                IsActive = true,
            };
            seed.Users.Add(tenantUser);
            seed.SaveChanges();
            TestSeed.Join(seed, tenantUser, pid, waRole);
            tenantPid = pid;
        }

        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = Ctx(admin, db);
        var svc = new TenantService(
            new UnitOfWork(ctx),
            new IdentityHasher(),
            new NoopFileStorage(),
            new SignupEnabledSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(ctx))
        );

        // F9 (DB-11a cross-review): ChangePlanAsync now takes the WORKSPACE id (tenantPid, the
        // membership's OwnerId seeded above), never the admin's `users.id`.
        var res = await svc.ChangePlanAsync(tenantPid, proId);
        Assert.True(res.IsSuccess);

        var sub = ctx.Subscriptions.IgnoreQueryFilters().Single(s => s.OwnerId == tenantPid);
        Assert.Equal(proId, sub.PlanId);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
    }
}
