using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
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
/// Each enforced lever through its real service: limit → LimitReached, soft-delete frees a slot,
/// downgrade grandfathers existing rows. Uses a real EntitlementService with the kill-switch ON.
/// </summary>
public class PlanEnforcementTests
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
        public bool Enforcement { get; set; } = true;
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

    // Seeds a Free plan carrying the given entitlements and a Subscription linking the tenant to it.
    private static void SeedPlanFor(string db, Guid tenant, PlanEntitlements ent)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var plan = new Plan { Name = "Free", Slug = "free", Entitlements = ent };
        seed.Plans.Add(plan);
        seed.SaveChanges();
        seed.Subscriptions.Add(new Subscription { OwnerId = tenant, PlanId = plan.Id, Status = SubscriptionStatus.Active });
        seed.SaveChanges();
    }

    private static ProjectService Projects(AppDbContext db, ICurrentUser user, FakeSettings settings)
    {
        var uow = new UnitOfWork(db);
        return new ProjectService(uow, user, new EntitlementService(uow, user, settings));
    }

    // ── MaxProjects ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MaxProjects_BlocksAtLimit_And_SoftDeleteFreesSlot()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxProjects = 2 });

        // Non-admin owner: DeleteAsync takes the owner path (no transaction — the in-memory provider
        // doesn't support the admin cascade transaction). CreatedBy is stamped to this user's Id.
        var userId = Guid.NewGuid();
        var user = new FakeCurrentUser { Id = userId, TenantId = tenant, IsAdmin = false };
        var ctx = Ctx(user, db);
        var svc = Projects(ctx, user, new FakeSettings());

        Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = "p1", Name = "P1" })).IsSuccess);
        Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = "p2", Name = "P2" })).IsSuccess);

        var blocked = await svc.CreateAsync(new CreateProjectRequest { Key = "p3", Name = "P3" });
        Assert.True(blocked.IsLimitReached);
        Assert.Equal(EntitlementCatalog.MaxProjects, blocked.Limit!.Lever);
        Assert.Equal(2, blocked.Limit.Current);
        Assert.Equal(2, blocked.Limit.Limit);

        // Soft-delete one → a slot frees → next create succeeds.
        var p1 = ctx.Projects.IgnoreQueryFilters().First(p => p.Key == "p1");
        var del = await svc.DeleteAsync(p1.Id);
        Assert.True(del.IsSuccess);

        Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = "p3", Name = "P3" })).IsSuccess);
    }

    [Fact]
    public async Task MaxProjects_Downgrade_Grandfathers_ExistingRows()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        // Start generous: limit 5, create 4.
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxProjects = 5 });
        var user = new FakeCurrentUser { Id = tenant, TenantId = tenant, IsAdmin = true };

        using (var ctx = Ctx(user, db))
        {
            var svc = Projects(ctx, user, new FakeSettings());
            for (var i = 0; i < 4; i++)
                Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = $"p{i}", Name = $"P{i}" })).IsSuccess);
        }

        // Downgrade the plan to MaxProjects = 2 (existing 4 are untouched).
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var plan = seed.Plans.Single(p => p.Slug == "free");
            plan.Entitlements = new PlanEntitlements { MaxProjects = 2 };
            seed.Plans.Update(plan);
            seed.SaveChanges();
        }

        using (var ctx = Ctx(user, db))
        {
            // Existing rows survive.
            Assert.Equal(4, ctx.Projects.IgnoreQueryFilters().Count(p => p.DeletedAt == null));
            // Next create is blocked (4 >= 2).
            var svc = Projects(ctx, user, new FakeSettings());
            var blocked = await svc.CreateAsync(new CreateProjectRequest { Key = "p9", Name = "P9" });
            Assert.True(blocked.IsLimitReached);
        }
    }

    // ── MaxSeats (UserService direct-add) ───────────────────────────────────

    [Fact]
    public async Task MaxSeats_BlocksAtLimit_OnDirectUserAdd()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxSeats = 1 });

        int roleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Dev", GrantsAdmin = false, IsActive = true, OwnerId = tenant };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;
            // The workspace-admin user already occupies one seat.
            var adminRole = new Role { Name = "WA", GrantsAdmin = true, IsActive = true, OwnerId = tenant };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();
            seed.Users.Add(new User { Email = "wa@a.com", PasswordHash = "x", DisplayName = "WA", RoleId = adminRole.Id, PublicId = tenant, OwnerId = tenant, IsActive = true, ApprovalStatus = ApprovalStatus.Approved });
            seed.SaveChanges();
        }

        var user = new FakeCurrentUser { Id = tenant, TenantId = tenant, IsAdmin = true };
        var ctx = Ctx(user, db);
        var uow = new UnitOfWork(ctx);
        var svc = new UserService(uow, new IdentityHasher(), user, new NoopEmail(), new EntitlementService(uow, user, new FakeSettings()), new NoopBrandingService());

        // Seat 1 already used by the WA user → limit 1 reached → next add blocked.
        var res = await svc.CreateAsync(new Application.DTOs.User.CreateUserRequest
        { Email = "new@a.com", Password = "password123", DisplayName = "New", RoleId = roleId });
        Assert.True(res.IsLimitReached);
    }

    // ── MaxTenantWidePredefinedActions ───────────────────────────────────────

    [Fact]
    public async Task MaxTenantWidePredefinedActions_BlocksAtLimit()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxTenantWidePredefinedActions = 1 });

        var user = new FakeCurrentUser { Id = tenant, TenantId = tenant, IsAdmin = true };
        var ctx = Ctx(user, db);
        var uow = new UnitOfWork(ctx);
        var ent = new EntitlementService(uow, user, new FakeSettings());
        var svc = new PredefinedActionService(uow, new ProjectService(uow, user, ent), user, ent);

        Assert.True((await svc.CreateTenantAsync(new CreatePredefinedActionRequest { Text = "A", Prompt = "p", IsActive = true })).IsSuccess);
        var blocked = await svc.CreateTenantAsync(new CreatePredefinedActionRequest { Text = "B", Prompt = "p", IsActive = true });
        Assert.True(blocked.IsLimitReached);
        Assert.Equal(EntitlementCatalog.MaxTenantWidePredefinedActions, blocked.Limit!.Lever);
    }

    // ── MaxPredefinedActionsPerProject (project create-loop) ─────────────────

    [Fact]
    public async Task MaxPredefinedActionsPerProject_BlocksInCreateLoop()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxProjects = -1, MaxPredefinedActionsPerProject = 1 });

        var user = new FakeCurrentUser { Id = tenant, TenantId = tenant, IsAdmin = true };
        var ctx = Ctx(user, db);
        var svc = Projects(ctx, user, new FakeSettings());

        var req = new CreateProjectRequest
        {
            Key = "p1",
            Name = "P1",
            PredefinedActions = new List<PredefinedActionInput>
            {
                new() { Text = "A", Prompt = "p", IsActive = true },
                new() { Text = "B", Prompt = "p", IsActive = true }, // 2nd exceeds limit of 1
            }
        };
        var res = await svc.CreateAsync(req);
        Assert.True(res.IsLimitReached);
    }

    // ── Kill-switch off ⇒ no enforcement anywhere ────────────────────────────

    [Fact]
    public async Task KillSwitchOff_NoEnforcement_OnProjectCreate()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedPlanFor(db, tenant, new PlanEntitlements { MaxProjects = 1 });

        var user = new FakeCurrentUser { Id = tenant, TenantId = tenant, IsAdmin = true };
        var ctx = Ctx(user, db);
        var svc = Projects(ctx, user, new FakeSettings { Enforcement = false });

        Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = "p1", Name = "P1" })).IsSuccess);
        Assert.True((await svc.CreateAsync(new CreateProjectRequest { Key = "p2", Name = "P2" })).IsSuccess); // over limit but OFF
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse DefaultBranding() => new()
        {
            ProductName = "Pointer",
            Tagline = string.Empty,
            PrimaryColor = "#2563eb",
            Urls = new Pointer.Application.DTOs.Branding.BrandingUrlsResponse { App = "https://app.pointer.moamen.work" },
            Assets = new Pointer.Application.DTOs.Branding.BrandingAssetsResponse(),
        };
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(Pointer.Application.DTOs.Branding.BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(DefaultBranding());
    }
}
