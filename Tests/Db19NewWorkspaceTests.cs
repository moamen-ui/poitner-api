using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
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
/// DB-19 (WS-NEW) — signed-in "+ New workspace": the §3.2 owned-count, the §3.3 gate, the §3.4
/// algorithm (levers resolved directly, D19.5), and tenancy isolation of the created workspace.
/// Fixtures copied from <see cref="MonetizationSignupTests"/> (plan + subscription seeding,
/// InMemory context) and <see cref="WorkspaceMembershipTests"/> (two workspaces, one identity).
/// </summary>
public class Db19NewWorkspaceTests
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

    /// <summary>
    /// enforcement_enabled resolves to false (the production default) — DB-19 D19.5: the levers
    /// must still be enforced, which is exactly what every test here asserts through this fake.
    /// </summary>
    private sealed class EnforcementOffSettings : ISettingsService
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static AppDbContext SuperCtx(string db) =>
        Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);

    private sealed record Seeded(
        Guid WorkspaceId,
        Guid CallerPublicId,
        int CallerUserId,
        Role AdminRole
    );

    /// <summary>
    /// Seeds the global Workspace Admin role, one workspace with the caller as its live approved
    /// admin, and (optionally) a plan subscribed to that workspace. The identity is verified,
    /// active and non-demo unless overridden.
    /// </summary>
    private static Seeded SeedCaller(
        string db,
        PlanEntitlements? planEntitlements = null,
        bool verified = true,
        string workspaceName = "Agency HQ"
    )
    {
        using var seed = SuperCtx(db);
        var role = new Role
        {
            Name = "Workspace Admin",
            GrantsAdmin = true,
            IsActive = true,
            OwnerId = null,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();

        var publicId = Guid.NewGuid();
        var identity = new User
        {
            Email = "owner@a.com",
            PasswordHash = "x",
            DisplayName = "Owner",
            PublicId = publicId,
            IsActive = true,
            EmailVerifiedAt = verified ? DateTime.UtcNow : null,
        };
        seed.Users.Add(identity);
        seed.SaveChanges();

        var wsId = Guid.NewGuid();
        seed.Workspaces.Add(
            new Workspace
            {
                Id = wsId,
                Name = workspaceName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = publicId,
            }
        );
        seed.SaveChanges();
        TestSeed.Join(seed, identity, wsId, role);

        if (planEntitlements != null)
        {
            var plan = new Plan
            {
                Name = "Seeded",
                Slug = "seeded-" + Guid.NewGuid().ToString("N")[..8],
                Entitlements = planEntitlements,
            };
            seed.Plans.Add(plan);
            seed.SaveChanges();
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = wsId,
                    PlanId = plan.Id,
                    Status = SubscriptionStatus.Active,
                }
            );
            seed.SaveChanges();
        }

        return new Seeded(wsId, publicId, identity.Id, role);
    }

    private static WorkspaceCreationService Build(
        AppDbContext ctx,
        ICurrentUser user,
        FakeAuditWriter audit
    ) =>
        new(
            new UnitOfWork(ctx),
            user,
            new MembershipService(new UnitOfWork(ctx)),
            new EntitlementService(new UnitOfWork(ctx), user, new EnforcementOffSettings()),
            audit
        );

    private static FakeCurrentUser CallerOn(Seeded s) =>
        new()
        {
            Id = s.CallerPublicId,
            TenantId = s.WorkspaceId,
            IsAdmin = true,
        };

    // ── 1 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_UnderCap_ApprovalFalse_IsActiveImmediately()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(
            db,
            new PlanEntitlements { MaxOwnedWorkspaces = 3, NewWorkspaceRequiresApproval = false }
        );
        var audit = new FakeAuditWriter();

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), audit)
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Acme Client" });

        Assert.True(result.IsSuccess);
        Assert.Equal("active", result.Data!.Status);
        Assert.Equal("Acme Client", result.Data.Name);

        var workspace = ctx
            .Workspaces.IgnoreQueryFilters()
            .Single(w => w.Id == result.Data.WorkspaceId);
        Assert.Equal("Acme Client", workspace.Name);
        Assert.Equal(seeded.CallerPublicId, workspace.CreatedBy);

        var membership = ctx.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.OwnerId == result.Data.WorkspaceId && m.UserId == seeded.CallerUserId);
        Assert.Equal(ApprovalStatus.Approved, membership.ApprovalStatus);
        Assert.True(membership.IsActive);
        Assert.Equal(seeded.AdminRole.Id, membership.RoleId);

        // No subscription row — Free, exactly like register-admin without a plan (§3.4e).
        Assert.Empty(
            ctx.Subscriptions.IgnoreQueryFilters().Where(s => s.OwnerId == result.Data.WorkspaceId)
        );

        // Exactly one workspace.created audit row, source = signed_in, owner = the NEW workspace.
        var row = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.WorkspaceCreated, row.Action);
        Assert.Equal(result.Data.WorkspaceId, row.OwnerId);
        Assert.Equal("signed_in", row.After!["source"]);
        Assert.Equal("Approved", row.After["approval_status"]);
        Assert.Equal("2", row.After["count"]);
    }

    // ── 2 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ApprovalRequired_IsPending()
    {
        var db = Guid.NewGuid().ToString();
        // Keys absent (except the raised cap) ⇒ NewWorkspaceRequiresApproval resolves to true.
        var seeded = SeedCaller(db, new PlanEntitlements { MaxOwnedWorkspaces = 2 });

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Pending Co" });

        Assert.True(result.IsSuccess);
        Assert.Equal("pending_approval", result.Data!.Status);

        var membership = ctx.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.OwnerId == result.Data.WorkspaceId && m.UserId == seeded.CallerUserId);
        Assert.Equal(ApprovalStatus.Pending, membership.ApprovalStatus);
        Assert.False(membership.IsActive);
    }

    // ── 3 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AtCap_ReturnsLimitReached_AndWritesNothing()
    {
        var db = Guid.NewGuid().ToString();
        // Keys absent ⇒ catalog defaults: cap 1, approval required. Caller owns 1 (the seed).
        var seeded = SeedCaller(db);
        var before = SuperCtx(db).Workspaces.IgnoreQueryFilters().Count();

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Over" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsLimitReached);
        Assert.Equal(EntitlementCatalog.MaxOwnedWorkspaces, result.Limit!.Lever);
        Assert.Equal(1, result.Limit.Current);
        Assert.Equal(1, result.Limit.Limit);
        Assert.Equal(before, SuperCtx(db).Workspaces.IgnoreQueryFilters().Count());
    }

    // ── 4 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Owned_Counts_PendingDisabledPausedScheduled_NotRejectedOrEnded()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db); // 1: approved + active admin on a live workspace (counts)

        Guid paused = default,
            scheduled = default,
            pendingWs = default,
            disabledWs = default,
            rejectedWs = default,
            endedWs = default,
            deletedWs = default,
            memberWs = default;
        using (var seed = SuperCtx(db))
        {
            var caller = seed
                .Users.IgnoreQueryFilters()
                .Single(u => u.PublicId == seeded.CallerPublicId);
            var deputy = new Role
            {
                Name = "Deputy",
                GrantsAdmin = false,
                IsActive = true,
                OwnerId = null,
            };
            seed.Roles.Add(deputy);
            seed.SaveChanges();

            Guid Ws(
                string name,
                DateTime? pausedAt = null,
                DateTime? scheduledFor = null,
                DateTime? deletedAt = null
            )
            {
                var id = Guid.NewGuid();
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = id,
                        Name = name,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = seeded.CallerPublicId,
                        PausedAt = pausedAt,
                        DeletionScheduledFor = scheduledFor,
                        DeletedAt = deletedAt,
                    }
                );
                seed.SaveChanges();
                return id;
            }

            pendingWs = Ws("Pending Co");
            disabledWs = Ws("Disabled Co");
            paused = Ws("Paused Co", pausedAt: DateTime.UtcNow);
            scheduled = Ws("Scheduled Co", scheduledFor: DateTime.UtcNow.AddDays(7));
            rejectedWs = Ws("Rejected Co");
            endedWs = Ws("Ended Co");
            deletedWs = Ws("Deleted Ws Co", deletedAt: DateTime.UtcNow);
            memberWs = Ws("Member Co");

            TestSeed.Join(
                seed,
                caller,
                pendingWs,
                seeded.AdminRole,
                isActive: false,
                status: ApprovalStatus.Pending
            );
            TestSeed.Join(seed, caller, disabledWs, seeded.AdminRole, isActive: false); // disabled, approved
            TestSeed.Join(seed, caller, paused, seeded.AdminRole);
            TestSeed.Join(seed, caller, scheduled, seeded.AdminRole);
            TestSeed.Join(
                seed,
                caller,
                rejectedWs,
                seeded.AdminRole,
                status: ApprovalStatus.Rejected
            );
            var ended = TestSeed.Join(seed, caller, endedWs, seeded.AdminRole);
            ended.LeftAt = DateTime.UtcNow;
            seed.SaveChanges();
            var softDeleted = TestSeed.Join(seed, caller, deletedWs, seeded.AdminRole);
            softDeleted.DeletedAt = DateTime.UtcNow;
            seed.SaveChanges();
            TestSeed.Join(seed, caller, memberWs, deputy);
        }

        using var ctx = Ctx(CallerOn(seeded), db);
        var allowance = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .GetAllowanceAsync();

        Assert.True(allowance.IsSuccess);
        // seeded + pending + disabled + paused + scheduled = 5; rejected/ended/soft-deleted/
        // deleted-workspace/deputy excluded (§3.2 / D19.4).
        Assert.Equal(5, allowance.Data!.Owned);
        Assert.Equal(1, allowance.Data.Max); // catalog default
        Assert.True(allowance.Data.RequiresApproval); // catalog default
        Assert.False(allowance.Data.CanCreate);
    }

    // ── 5 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unlimited_MinusOne_NeverBlocks()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(
            db,
            new PlanEntitlements { MaxOwnedWorkspaces = -1, NewWorkspaceRequiresApproval = false }
        );

        using var ctx = Ctx(CallerOn(seeded), db);
        var service = Build(ctx, CallerOn(seeded), new FakeAuditWriter());
        // Owned is already 1; -1 = unlimited ⇒ canCreate true.
        var allowance = await service.GetAllowanceAsync();
        Assert.True(allowance.Data!.CanCreate);

        var result = await service.CreateForCurrentIdentityAsync(
            new CreateWorkspaceRequest { Name = "Third" }
        );
        Assert.True(result.IsSuccess);

        // Still unlimited after the create.
        var after = await service.GetAllowanceAsync();
        Assert.True(after.Data!.CanCreate);
    }

    [Fact]
    public async Task Zero_BlocksEvenFirstExtra()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(
            db,
            new PlanEntitlements { MaxOwnedWorkspaces = 0, NewWorkspaceRequiresApproval = false }
        );

        using var ctx = Ctx(CallerOn(seeded), db);
        var service = Build(ctx, CallerOn(seeded), new FakeAuditWriter());
        var allowance = await service.GetAllowanceAsync();
        Assert.Equal(0, allowance.Data!.Max);
        Assert.False(allowance.Data.CanCreate);

        var result = await service.CreateForCurrentIdentityAsync(
            new CreateWorkspaceRequest { Name = "Nope" }
        );
        Assert.True(result.IsLimitReached);
        Assert.Equal(0, result.Limit!.Limit);
    }

    // ── 6 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GoverningPlan_IsCurrentWorkspacePlan()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db); // workspace A, default plan (cap 1)

        Guid workspaceB;
        using (var seed = SuperCtx(db))
        {
            var caller = seed
                .Users.IgnoreQueryFilters()
                .Single(u => u.PublicId == seeded.CallerPublicId);
            workspaceB = Guid.NewGuid();
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceB,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = seeded.CallerPublicId,
                }
            );
            seed.SaveChanges();
            TestSeed.Join(seed, caller, workspaceB, seeded.AdminRole); // owns 2 now
            var cap5 = new Plan
            {
                Name = "Cap 5",
                Slug = "cap5-" + Guid.NewGuid().ToString("N")[..8],
                Entitlements = new PlanEntitlements
                {
                    MaxOwnedWorkspaces = 5,
                    NewWorkspaceRequiresApproval = false,
                },
            };
            seed.Plans.Add(cap5);
            seed.SaveChanges();
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = workspaceB,
                    PlanId = cap5.Id,
                    Status = SubscriptionStatus.Active,
                }
            );
            seed.SaveChanges();
        }

        // Session on A (cap 1, owns 2) → blocked.
        using (var ctxA = Ctx(CallerOn(seeded), db))
        {
            var blocked = await Build(ctxA, CallerOn(seeded), new FakeAuditWriter())
                .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "From A" });
            Assert.True(blocked.IsLimitReached);
        }

        // Session on B (cap 5, owns 2) → allowed.
        using (
            var ctxB = Ctx(
                new FakeCurrentUser
                {
                    Id = seeded.CallerPublicId,
                    TenantId = workspaceB,
                    IsAdmin = true,
                },
                db
            )
        )
        {
            var allowed = await Build(
                    ctxB,
                    new FakeCurrentUser
                    {
                        Id = seeded.CallerPublicId,
                        TenantId = workspaceB,
                        IsAdmin = true,
                    },
                    new FakeAuditWriter()
                )
                .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "From B" });
            Assert.True(allowed.IsSuccess);
        }
    }

    // ── 7 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refuses_Deputy()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        using var seed = SuperCtx(db);
        var caller = seed
            .Users.IgnoreQueryFilters()
            .Single(u => u.PublicId == seeded.CallerPublicId);
        var deputy = new Role
        {
            Name = "Deputy",
            GrantsAdmin = false,
            IsActive = true,
            OwnerId = null,
        };
        seed.Roles.Add(deputy);
        seed.SaveChanges();
        var membership = seed.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == seeded.CallerUserId && m.OwnerId == seeded.WorkspaceId);
        membership.RoleId = deputy.Id;
        seed.SaveChanges();

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_Member()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        using var seed = SuperCtx(db);
        var dev = new Role
        {
            Name = "Developer",
            GrantsAdmin = false,
            IsActive = true,
            OwnerId = null,
        };
        seed.Roles.Add(dev);
        seed.SaveChanges();
        var caller = seed
            .Users.IgnoreQueryFilters()
            .Single(u => u.PublicId == seeded.CallerPublicId);
        TestSeed.Join(seed, caller, Guid.NewGuid(), dev);
        // The caller's CURRENT membership (on the seeded workspace) is still admin — make it Developer.
        var membership = seed.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == seeded.CallerUserId && m.OwnerId == seeded.WorkspaceId);
        membership.RoleId = dev.Id;
        seed.SaveChanges();

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_SuperAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        var super = new FakeCurrentUser
        {
            Id = seeded.CallerPublicId,
            TenantId = seeded.WorkspaceId,
            IsSuperAdmin = true,
        };

        using var ctx = Ctx(super, db);
        var result = await Build(ctx, super, new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_Impersonation()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        var imp = new FakeCurrentUser
        {
            Id = seeded.CallerPublicId,
            TenantId = seeded.WorkspaceId,
            ImpersonationSessionId = 7,
        };

        using var ctx = Ctx(imp, db);
        var result = await Build(ctx, imp, new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_ApiKey()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        var key = new FakeCurrentUser
        {
            Id = seeded.CallerPublicId,
            TenantId = seeded.WorkspaceId,
            KeyScopes = "comments:write",
        };

        using var ctx = Ctx(key, db);
        var result = await Build(ctx, key, new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_Demo()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db);
        using (var seed = SuperCtx(db))
        {
            var ws = seed.Workspaces.IgnoreQueryFilters().Single(w => w.Id == seeded.WorkspaceId);
            ws.DemoExpiresAt = DateTime.UtcNow.AddHours(20); // live, unconverted demo
            seed.SaveChanges();
        }

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Refuses_Unverified_WithSpecificMessage()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db, verified: false);

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Auth.EmailNotVerified, result.Message);
    }

    // ── 8 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnforcementSwitchOff_StillEnforced()
    {
        // The fake settings resolve enforcement_enabled to false (production default) — D19.5:
        // the cap must still block, because the levers are resolved directly, not via CheckCountAsync.
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db); // cap 1 (catalog default), owns 1

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "X" });
        Assert.True(result.IsLimitReached);
    }

    // ── 9 ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExistingWorkspaces_Untouched_WhenOverCap()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(db); // cap 1; add two more owned workspaces → owns 3

        using (var seed = SuperCtx(db))
        {
            var caller = seed
                .Users.IgnoreQueryFilters()
                .Single(u => u.PublicId == seeded.CallerPublicId);
            foreach (var name in new[] { "Second", "Third" })
            {
                var id = Guid.NewGuid();
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = id,
                        Name = name,
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = seeded.CallerPublicId,
                    }
                );
                seed.SaveChanges();
                TestSeed.Join(seed, caller, id, seeded.AdminRole);
            }
        }

        List<(
            int RoleId,
            ApprovalStatus Approval,
            bool Active,
            Guid Stamp,
            DateTime? LeftAt
        )> before;
        using (var snap = SuperCtx(db))
        {
            before = snap.Set<WorkspaceMembership>()
                .IgnoreQueryFilters()
                .Where(m => m.UserId == seeded.CallerUserId)
                .OrderBy(m => m.Id)
                .Select(m => new
                {
                    m.RoleId,
                    m.ApprovalStatus,
                    m.IsActive,
                    m.SecurityStamp,
                    m.LeftAt,
                })
                .AsEnumerable()
                .Select(m => (m.RoleId, m.ApprovalStatus, m.IsActive, m.SecurityStamp, m.LeftAt))
                .ToList();
            Assert.Equal(3, before.Count);
        }

        using var ctx = Ctx(CallerOn(seeded), db);
        var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
            .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Fourth" });
        Assert.True(result.IsLimitReached);

        using var after = SuperCtx(db);
        var memberships = after
            .Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == seeded.CallerUserId)
            .OrderBy(m => m.Id)
            .Select(m => new
            {
                m.RoleId,
                m.ApprovalStatus,
                m.IsActive,
                m.SecurityStamp,
                m.LeftAt,
            })
            .AsEnumerable()
            .Select(m => (m.RoleId, m.ApprovalStatus, m.IsActive, m.SecurityStamp, m.LeftAt))
            .ToList();
        Assert.Equal(before, memberships);
    }

    // ── 10 ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatedWorkspace_IsTenantIsolated()
    {
        var db = Guid.NewGuid().ToString();
        var seeded = SeedCaller(
            db,
            new PlanEntitlements { MaxOwnedWorkspaces = 3, NewWorkspaceRequiresApproval = false }
        );

        Guid otherMemberWs = default;
        using (var seed = SuperCtx(db))
        {
            // A project + a second member in the caller's current workspace.
            seed.Projects.Add(
                new Project
                {
                    Key = "a1",
                    Name = "A1",
                    OwnerId = seeded.WorkspaceId,
                }
            );
            var colleague = new User
            {
                Email = "colleague@a.com",
                PasswordHash = "x",
                DisplayName = "Colleague",
                PublicId = Guid.NewGuid(),
                IsActive = true,
                EmailVerifiedAt = DateTime.UtcNow,
            };
            seed.Users.Add(colleague);
            seed.SaveChanges();
            TestSeed.Join(seed, colleague, seeded.WorkspaceId, seeded.AdminRole);

            // An unrelated tenant with its own project and member.
            otherMemberWs = Guid.NewGuid();
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = otherMemberWs,
                    Name = "Unrelated",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                }
            );
            seed.Projects.Add(
                new Project
                {
                    Key = "u1",
                    Name = "U1",
                    OwnerId = otherMemberWs,
                }
            );
            seed.SaveChanges();
        }

        Guid newWs;
        using (var ctx = Ctx(CallerOn(seeded), db))
        {
            var result = await Build(ctx, CallerOn(seeded), new FakeAuditWriter())
                .CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Fresh Co" });
            Assert.True(result.IsSuccess);
            newWs = result.Data!.WorkspaceId;
        }

        // A session on the NEW workspace sees no project or membership of the caller's other
        // workspace (R8.5 shape).
        using (
            var newCtx = Ctx(
                new FakeCurrentUser
                {
                    Id = seeded.CallerPublicId,
                    TenantId = newWs,
                    IsAdmin = true,
                },
                db
            )
        )
        {
            Assert.Empty(newCtx.Projects.ToList());
            var memberships = newCtx.Set<WorkspaceMembership>().ToList();
            var owned = memberships.Where(m => m.UserId == seeded.CallerUserId).ToList();
            Assert.Single(owned);
            Assert.Equal(newWs, owned[0].OwnerId);
            Assert.DoesNotContain(memberships, m => m.OwnerId == seeded.WorkspaceId);
        }

        // A session of the unrelated tenant sees neither workspace's rows.
        using (
            var otherCtx = Ctx(
                new FakeCurrentUser
                {
                    Id = Guid.NewGuid(),
                    TenantId = otherMemberWs,
                    IsAdmin = true,
                },
                db
            )
        )
        {
            var projects = otherCtx.Projects.ToList();
            Assert.Single(projects);
            Assert.Equal(otherMemberWs, projects[0].OwnerId);
            Assert.DoesNotContain(
                projects,
                p => p.OwnerId == seeded.WorkspaceId || p.OwnerId == newWs
            );
            var memberships = otherCtx.Set<WorkspaceMembership>().ToList();
            Assert.DoesNotContain(
                memberships,
                m => m.OwnerId == seeded.WorkspaceId || m.OwnerId == newWs
            );
        }
    }
}

/// <summary>
/// DB-19 §6 test 13 — Postgres-gated concurrency proof for the §3.4a users-row lock: two parallel
/// creates by the same identity at owned = max - 1 must produce exactly one workspace. Gated behind
/// POINTER_TEST_PG (same gate as <see cref="UsageRollupPostgresTests"/>):
///
///   docker run -d --name db19 -e POSTGRES_PASSWORD=pw -p 5591:5432 postgres:15
///   POINTER_TEST_PG=1 dotnet test Tests -c Release --filter FullyQualifiedName~Db19NewWorkspacePostgresTests
/// </summary>
[Trait("Category", "Postgres")]
public class Db19NewWorkspacePostgresTests
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

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("POINTER_TEST_PG_CONNSTRING")
        ?? "Host=localhost;Port=5591;Database=postgres;Username=postgres;Password=pw";

    private static AppDbContext MakeContext(ICurrentUser user) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public async Task TwoParallelCreates_AtMaxMinusOne_ExactlyOneSucceeds()
    {
        if (Environment.GetEnvironmentVariable("POINTER_TEST_PG") is null)
            return; // skipped: no throwaway Postgres configured for this run (see class doc)

        var publicId = Guid.NewGuid();
        var wsId = Guid.NewGuid();

        await using (var seed = MakeContext(new FakeCurrentUser()))
        {
            await seed.Database.MigrateAsync();

            // Global Workspace Admin role (fresh migrated DB has none).
            if (
                !await seed
                    .Roles.IgnoreQueryFilters()
                    .AnyAsync(r =>
                        r.Name == "Workspace Admin" && r.OwnerId == null && r.DeletedAt == null
                    )
            )
            {
                seed.Roles.Add(
                    new Role
                    {
                        Name = "Workspace Admin",
                        GrantsAdmin = true,
                        IsActive = true,
                        OwnerId = null,
                    }
                );
                await seed.SaveChangesAsync();
            }
            var role = await seed
                .Roles.IgnoreQueryFilters()
                .SingleAsync(r =>
                    r.Name == "Workspace Admin" && r.OwnerId == null && r.DeletedAt == null
                );

            var identity = new User
            {
                Email = $"db19-{publicId:N}@t.local",
                PasswordHash = "x",
                DisplayName = "DB19",
                PublicId = publicId,
                IsActive = true,
                EmailVerifiedAt = DateTime.UtcNow,
            };
            seed.Users.Add(identity);
            await seed.SaveChangesAsync();

            seed.Workspaces.Add(
                new Workspace
                {
                    Id = wsId,
                    Name = "Seed Co",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = publicId,
                }
            );
            await seed.SaveChangesAsync();
            TestSeed.Join(seed, identity, wsId, role);

            // Root cause of the original failure (not the product locking code): `plans.name` has a
            // live-uniqueness index (`ux_plans_name_live`, PlanMapping.cs:25-28) and this Postgres
            // instance is a long-lived throwaway container, not recreated per run (UsageRollupPostgresTests'
            // doc comment: TRUNCATE can't reach workspaces because of the audit append-only trigger, so the
            // precedent there is fresh random identifiers, not a fixed literal). A fixed `Name = "DB19 cap2"`
            // collided with the row a prior run left behind, so the SEED's SaveChangesAsync threw
            // `23505 duplicate key ux_plans_name_live` before the two parallel creates ever ran. Suffix both
            // Name and Slug with the same per-run GUID so reruns against the same instance never collide.
            var suffix = publicId.ToString("N")[..8];
            var plan = new Plan
            {
                Name = "DB19 cap2 " + suffix,
                Slug = "db19-cap2-" + suffix,
                Entitlements = new PlanEntitlements
                {
                    MaxOwnedWorkspaces = 2,
                    NewWorkspaceRequiresApproval = false,
                },
            };
            seed.Plans.Add(plan);
            await seed.SaveChangesAsync();
            seed.Subscriptions.Add(
                new Subscription
                {
                    OwnerId = wsId,
                    PlanId = plan.Id,
                    Status = SubscriptionStatus.Active,
                }
            );
            await seed.SaveChangesAsync();
        }

        WorkspaceCreationService Svc()
        {
            // IsSuperAdmin defaults to true on this FakeCurrentUser (copied from
            // UsageRollupPostgresTests, whose seeding-only context wants it) — PassesGateAsync's
            // item 1 forbids super-admin sessions, so it must be turned off here for the caller
            // actually exercising the endpoint.
            var user = new FakeCurrentUser
            {
                Id = publicId,
                TenantId = wsId,
                IsAdmin = true,
                IsSuperAdmin = false,
            };
            var ctx = MakeContext(user);
            var uow = new UnitOfWork(ctx);
            return new WorkspaceCreationService(
                uow,
                user,
                new MembershipService(uow),
                new EntitlementService(uow, user, new FakeSettings()),
                new FakeAuditWriter()
            );
        }

        // owned = 1, max = 2 → one create fits, the other must see the committed row and refuse.
        var one = Task.Run(() =>
            Svc().CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Winner" })
        );
        var two = Task.Run(() =>
            Svc().CreateForCurrentIdentityAsync(new CreateWorkspaceRequest { Name = "Loser" })
        );
        var results = await Task.WhenAll(one, two);

        Assert.Single(results.Where(r => r.IsSuccess));
        Assert.Single(results.Where(r => r.IsLimitReached));
        Assert.All(results.Where(r => r.IsSuccess), r => Assert.Equal("active", r.Data!.Status));
    }
}
