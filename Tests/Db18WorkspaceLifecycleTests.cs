using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-18 — workspace self-service pause and delete. Fixture mirrors <see cref="ChangeEmailTests"/>
/// (InMemory + <c>TestSeed.Join</c> + a real <c>ResetTokenService</c> + a capturing e-mail double).
/// </summary>
public class Db18WorkspaceLifecycleTests
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";

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

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;

        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class NoopSettings : ISettingsService
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

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    /// <summary>Records every workspace segment passed to DeleteOwnerFilesAsync — same shape as
    /// Db17HardDeleteAndCleanupTests.RecordingFileStorage — used by the §9a HardDelete/job tests
    /// below to prove a refused/raced delete never touches the filesystem, and a real one does.</summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        public List<string> DeletedOwners { get; } = [];

        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment)
        {
            DeletedOwners.Add(ownerSegment);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");

        public string IssueSelection(User user) => "selection-for-" + user.PublicId.ToString("N");

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "imp-for-" + user.PublicId.ToString("N");
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
                    App = "https://app.pointer.test",
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

    /// <summary>Records every send; extracts the token= query param from the first link in the body.</summary>
    private sealed class CapturingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
        {
            Sent.Add((to, subject, htmlBody));
            return Task.FromResult(true);
        }

        public static string ExtractToken(string html)
        {
            var marker = "token=";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "no token= found in email body");
            start += marker.Length;
            var end = start;
            while (end < html.Length && html[end] != '"' && html[end] != '&' && html[end] != ' ')
                end++;
            return Uri.UnescapeDataString(html[start..end]);
        }
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
            new ConfigurationBuilder().Build()
        );

    private static IResetTokenService RealResetTokens() =>
        new ResetTokenService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );

    private static WorkspaceLifecycleService BuildService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail? email = null,
        FakeAuditWriter? audit = null,
        ILoginAttemptLimiter? lockout = null,
        IConfiguration? config = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? linkAttemptCache = null,
        IFileStorage? fileStorage = null
    ) =>
        BuildServiceWithUow(
            new UnitOfWork(ctx),
            user,
            email,
            audit,
            lockout,
            config,
            linkAttemptCache,
            fileStorage
        );

    private static WorkspaceLifecycleService BuildServiceWithUow(
        IUnitOfWork uow,
        ICurrentUser user,
        CapturingEmail? email = null,
        FakeAuditWriter? audit = null,
        ILoginAttemptLimiter? lockout = null,
        IConfiguration? config = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? linkAttemptCache = null,
        IFileStorage? fileStorage = null
    )
    {
        var memberships = new MembershipService(uow);
        var state = new WorkspaceStateService(uow);
        var workspaces = new WorkspaceService(uow, user, audit, memberships, config);
        var tenants = new TenantService(
            uow,
            new IdentityHasher(),
            fileStorage ?? new NoopFileStorage(),
            new NoopSettings(),
            new NoopBillingProvider(),
            memberships,
            audit
        );
        return new WorkspaceLifecycleService(
            uow,
            user,
            memberships,
            new IdentityHasher(),
            RealResetTokens(),
            email ?? new CapturingEmail(),
            new NoopBrandingService(),
            state,
            workspaces,
            tenants,
            lockout ?? new FakeLoginAttemptLimiter(),
            config,
            audit,
            linkAttemptCache
        );
    }

    /// <summary>AuthService fixture for RegisterAsync's frozen-workspace guard (D18.11) — mirrors
    /// ApiKeyAuthTests.BuildAuthService, reusing this file's IdentityHasher/CapturingEmail/
    /// NoopBrandingService/FakeLoginAttemptLimiter/RealResetTokens doubles.</summary>
    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        IWorkspaceStateService? workspaceState = null
    )
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new IdentityHasher(),
            new FakeTokenService(),
            user,
            new NoopSettings(),
            RealResetTokens(),
            new CapturingEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow),
            workspaceState: workspaceState
        );
    }

    /// <summary>
    /// Models a genuinely separate concurrent transaction committing in the gap between a locked
    /// method's pre-lock read and its own <c>FOR UPDATE</c> lock — copies
    /// <c>Db17HardDeleteAndCleanupTests.ExtendingOnLockUnitOfWork</c>'s idea: decorate a real
    /// <see cref="UnitOfWork"/> (InMemory-backed) so the moment <see cref="IUnitOfWork.ExecuteSqlRawAsync"/>
    /// (the lock marker — a no-op under InMemory, same as production's real <c>FOR UPDATE</c> would
    /// be a real lock under Postgres) is called for the FIRST time, a supplied callback runs — a
    /// second, fully independent call through a SEPARATE <see cref="AppDbContext"/> against the SAME
    /// named InMemory database, exactly as a second HTTP request would.
    /// </summary>
    private sealed class RacingUnitOfWork(UnitOfWork inner, Func<Task> onLock) : IUnitOfWork
    {
        private bool _fired;

        public IRepository<T> Repository<T>()
            where T : BaseEntity => inner.Repository<T>();

        public DbSet<UsageEvent> UsageEvents => inner.UsageEvents;
        public DbSet<UsageDaily> UsageDaily => inner.UsageDaily;
        public DbSet<Workspace> Workspaces => inner.Workspaces;
        public DbSet<UserAlias> UserAliases => inner.UserAliases;
        public DbSet<AuditEvent> AuditEvents => inner.AuditEvents;
        public DbSet<ImpersonationSession> ImpersonationSessions => inner.ImpersonationSessions;
        public DbSet<BillingPayment> BillingPayments => inner.BillingPayments;
        public DbSet<DiscountRedemption> DiscountRedemptions => inner.DiscountRedemptions;

        public Task<int> SaveChangesAsync() => inner.SaveChangesAsync();

        public Task ExecuteInTransactionAsync(Func<Task> action) =>
            inner.ExecuteInTransactionAsync(action);

        public void PreserveCreatedAtOnInsert(BaseEntity entity) =>
            inner.PreserveCreatedAtOnInsert(entity);

        public void ClearChangeTracker() => inner.ClearChangeTracker();

        public Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now) =>
            inner.AtomicClaimInviteSlotAsync(inviteId, now);

        public async Task ExecuteSqlRawAsync(string sql, params object[] parameters)
        {
            if (!_fired)
            {
                _fired = true;
                await onLock();
            }
            await inner.ExecuteSqlRawAsync(sql, parameters);
        }
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    private sealed class SeededWorkspace
    {
        public Guid WorkspaceId;
        public Role AdminRole = null!;
        public User Admin = null!;
        public User SecondAdmin = null!;
        public User Deputy = null!;
    }

    private static SeededWorkspace SeedWorkspace(AppDbContext seed, string name = "Acme")
    {
        var adminRole = new Role
        {
            Name = WorkspaceAdminRoleName,
            GrantsAdmin = true,
            IsSystem = true,
            IsActive = true,
        };
        var deputyRole = new Role
        {
            Name = "Workspace Admin Deputy",
            GrantsAdmin = true,
            IsSystem = true,
            IsActive = true,
        };
        seed.Roles.AddRange(adminRole, deputyRole);
        seed.SaveChanges();

        var workspaceId = Guid.NewGuid();
        seed.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = name,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        seed.SaveChanges();

        var admin = new User
        {
            Email = "admin@t.com",
            PasswordHash = "h:pw-admin",
            DisplayName = "Admin",
            PublicId = Guid.NewGuid(),
            RoleId = adminRole.Id,
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        var secondAdmin = new User
        {
            Email = "second@t.com",
            PasswordHash = "h:pw-second",
            DisplayName = "Second Admin",
            PublicId = Guid.NewGuid(),
            RoleId = adminRole.Id,
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        var deputy = new User
        {
            Email = "deputy@t.com",
            PasswordHash = "h:pw-deputy",
            DisplayName = "Deputy",
            PublicId = Guid.NewGuid(),
            RoleId = deputyRole.Id,
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        seed.Users.AddRange(admin, secondAdmin, deputy);
        seed.SaveChanges();

        TestSeed.Join(seed, admin, workspaceId, adminRole);
        TestSeed.Join(seed, secondAdmin, workspaceId, adminRole);
        TestSeed.Join(seed, deputy, workspaceId, deputyRole);

        return new SeededWorkspace
        {
            WorkspaceId = workspaceId,
            AdminRole = adminRole,
            Admin = admin,
            SecondAdmin = secondAdmin,
            Deputy = deputy,
        };
    }

    private static FakeCurrentUser AdminCaller(SeededWorkspace ws) =>
        new() { Id = ws.Admin.PublicId, TenantId = ws.WorkspaceId };

    // ── 1. Pause / resume ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pause_ByWorkspaceAdmin_SetsColumns_AuditsWorkspacePaused()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var audit = new FakeAuditWriter();
        using (var ctx = Ctx(caller, db))
        {
            var svc = BuildService(caller, ctx, audit: audit);
            var result = await svc.PauseAsync();
            Assert.True(result.IsSuccess, result.Message);
        }

        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var row = await ctx
                .Workspaces.IgnoreQueryFilters()
                .FirstAsync(w => w.Id == ws.WorkspaceId);
            Assert.NotNull(row.PausedAt);
            Assert.Equal(ws.Admin.PublicId, row.PausedBy);
            Assert.False(row.PausedByOperator);
        }

        Assert.Contains(audit.Entries, e => e.Action == AuditActions.WorkspacePaused);
    }

    [Fact]
    public async Task Pause_ByDeputy_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Deputy.PublicId, TenantId = ws.WorkspaceId };
        using var ctx = Ctx(caller, db);
        var svc = BuildService(caller, ctx);
        var result = await svc.PauseAsync();
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Pause_KeySession_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        caller.KeyScopes = "read";
        using var ctx = Ctx(caller, db);
        var svc = BuildService(caller, ctx);
        var result = await svc.PauseAsync();
        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Workspace.KeySessionCannotManage, result.Message);
    }

    [Fact]
    public async Task Pause_QuickAccess_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        caller.IsQuickAccess = true;
        using var ctx = Ctx(caller, db);
        var svc = BuildService(caller, ctx);
        var result = await svc.PauseAsync();
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Pause_LiveDemo_Refused()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            var row = await seed.Workspaces.FirstAsync(w => w.Id == ws.WorkspaceId);
            row.DemoExpiresAt = DateTime.UtcNow.AddHours(2);
            await seed.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        using var ctx = Ctx(caller, db);
        var svc = BuildService(caller, ctx);
        var result = await svc.PauseAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DemoCannotPauseOrDelete, result.Message);
    }

    [Fact]
    public async Task Pause_Twice_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        using (var ctx = Ctx(caller, db))
        {
            var svc = BuildService(caller, ctx);
            Assert.True((await svc.PauseAsync()).IsSuccess);
        }

        using (var ctx = Ctx(caller, db))
        {
            var svc = BuildService(caller, ctx);
            var result = await svc.PauseAsync();
            Assert.True(result.IsConflict);
            Assert.Equal(MessageKeys.Workspace.AlreadyPaused, result.Message);
        }
    }

    [Fact]
    public async Task Resume_AfterSelfPause_Clears()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx).PauseAsync()).IsSuccess);

        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildService(caller, ctx).ResumeAsync();
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.PausedAt);
        Assert.Null(row.PausedBy);
        Assert.False(row.PausedByOperator);
    }

    [Fact]
    public async Task Resume_OperatorPaused_ForbiddenForAdmin()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        var op = new FakeCurrentUser { IsSuperAdmin = true, Id = Guid.NewGuid() };
        using (var seed = Ctx(op, db))
            ws = SeedWorkspace(seed);

        using (var ctx = Ctx(op, db))
            Assert.True((await BuildService(op, ctx).OperatorPauseAsync(ws.WorkspaceId)).IsSuccess);

        var caller = AdminCaller(ws);
        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2).ResumeAsync();
        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Workspace.PausedByOperator, result.Message);
    }

    [Fact]
    public async Task OperatorResume_ClearsOperatorPause()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        var op = new FakeCurrentUser { IsSuperAdmin = true, Id = Guid.NewGuid() };
        using (var seed = Ctx(op, db))
            ws = SeedWorkspace(seed);

        using (var ctx = Ctx(op, db))
            Assert.True((await BuildService(op, ctx).OperatorPauseAsync(ws.WorkspaceId)).IsSuccess);

        using (var ctx = Ctx(op, db))
        {
            var result = await BuildService(op, ctx).OperatorResumeAsync(ws.WorkspaceId);
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(op, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.PausedAt);
        Assert.False(row.PausedByOperator);
    }

    [Fact]
    public async Task Resume_WhileScheduled_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            var svc = BuildService(caller, ctx, email);
            Assert.True((await svc.PauseAsync()).IsSuccess);
            Assert.True((await svc.RequestDeletionAsync()).IsSuccess);
        }

        string token;
        using (var confirmCtx = Ctx(caller, db))
        {
            var svc = BuildService(caller, confirmCtx, email);
            token = CapturingEmail.ExtractToken(email.Sent.Last().Html);
            var confirm = await svc.ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
            Assert.True(confirm.IsSuccess, confirm.Message);
        }

        using var resumeCtx = Ctx(caller, db);
        var resumeResult = await BuildService(caller, resumeCtx).ResumeAsync();
        Assert.True(resumeResult.IsConflict);
        Assert.Equal(MessageKeys.Workspace.CancelDeletionFirst, resumeResult.Message);
    }

    // ── 2. Request deletion ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestDeletion_OneMail_ToRequesterOnly_LinkHasPurposeDeleteWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        using var ctx = Ctx(caller, db);
        var svc = BuildService(caller, ctx, email);
        var result = await svc.RequestDeletionAsync();
        Assert.True(result.IsSuccess, result.Message);

        Assert.Single(email.Sent);
        Assert.Equal(ws.Admin.Email, email.Sent[0].To);

        var token = CapturingEmail.ExtractToken(email.Sent[0].Html);
        var resetTokens = RealResetTokens();
        Assert.True(
            resetTokens.TryValidateScoped(
                token,
                TokenPurposes.DeleteWorkspace,
                out var pid,
                out _,
                out var payload
            )
        );
        Assert.Equal(ws.Admin.PublicId, pid);
        Assert.NotNull(payload);
        Assert.StartsWith(ws.WorkspaceId.ToString("N"), payload);
    }

    [Fact]
    public async Task RequestDeletion_Cooldown5Min()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx).RequestDeletionAsync()).IsSuccess);

        using var ctx2 = Ctx(caller, db);
        var second = await BuildService(caller, ctx2).RequestDeletionAsync();
        Assert.False(second.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionEmailJustSent, second.Message);
    }

    [Fact]
    public async Task RequestDeletion_SixthIn24h_DailyLimit()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var audit = new FakeAuditWriter();

        // Seed 5 prior "workspace.deletion_requested" audit rows within the last 24h directly.
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            for (var i = 0; i < 5; i++)
            {
                seedCtx.AuditEvents.Add(
                    new AuditEvent
                    {
                        OccurredAt = DateTime.UtcNow.AddMinutes(-i),
                        OwnerId = ws.WorkspaceId,
                        Action = AuditActions.WorkspaceDeletionRequested,
                        TargetType = AuditTargets.Workspace,
                        ActorKind = AuditActorKind.User,
                    }
                );
            }
            await seedCtx.SaveChangesAsync();
        }

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, audit: audit).RequestDeletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionDailyLimit, result.Message);
    }

    [Fact]
    public async Task RequestDeletion_ArabicUser_GetsArabicMail()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            var admin = await seed.Users.FirstAsync(u => u.Id == ws.Admin.Id);
            admin.Language = "ar";
            await seed.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email).RequestDeletionAsync();
        Assert.True(result.IsSuccess, result.Message);

        Assert.Single(email.Sent);
        Assert.Contains("تأكيد", email.Sent[0].Subject);
        Assert.Contains("dir=\"rtl\"", email.Sent[0].Html);
    }

    // ── 3. Confirm ────────────────────────────────────────────────────────────────────────────

    private static async Task<string> RequestAndExtractTokenAsync(
        Db18WorkspaceLifecycleTests _,
        FakeCurrentUser caller,
        string db,
        CapturingEmail email
    )
    {
        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email).RequestDeletionAsync();
        Assert.True(result.IsSuccess, result.Message);
        return CapturingEmail.ExtractToken(email.Sent.Last().Html);
    }

    [Fact]
    public async Task Confirm_ValidTokenPasswordName_SchedulesGraceDays_MailsEveryLiveAdmin()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        var audit = new FakeAuditWriter();
        using (var ctx = Ctx(caller, db))
        {
            var svc = BuildService(caller, ctx, email, audit);
            var result = await svc.ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Data!.DeletionScheduledFor > DateTime.UtcNow.AddDays(6));
        }

        using (var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var row = await check
                .Workspaces.IgnoreQueryFilters()
                .FirstAsync(w => w.Id == ws.WorkspaceId);
            Assert.NotNull(row.DeletionConfirmedAt);
            Assert.NotNull(row.DeletionScheduledFor);
        }

        Assert.Contains(audit.Entries, e => e.Action == AuditActions.WorkspaceDeletionConfirmed);
        // E1 (request) + E2 (scheduled) to admin + secondAdmin = 3 total sends; deputy gets none.
        Assert.Equal(3, email.Sent.Count);
        Assert.DoesNotContain(email.Sent, s => s.To == ws.Deputy.Email);
        Assert.Contains(email.Sent, s => s.To == ws.SecondAdmin.Email);
    }

    [Fact]
    public async Task Confirm_WrongPassword_NoStateChange()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildService(caller, ctx, email)
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "wrong",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.False(result.IsSuccess);
            Assert.Equal(MessageKeys.User.CurrentPasswordIncorrect, result.Message);
        }

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionScheduledFor);
    }

    [Fact]
    public async Task Confirm_NameMismatch()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Wrong Name",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionNameMismatch, result.Message);
    }

    [Fact]
    public async Task Confirm_Passwordless_NameOnly_Ok()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            var admin = await seed.Users.FirstAsync(u => u.Id == ws.Admin.Id);
            admin.PasswordlessOnly = true;
            await seed.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest { Token = token, WorkspaceName = "Acme" }
            );
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task Confirm_Twice_SecondIsLinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        var req = new ConfirmWorkspaceDeletionRequest
        {
            Token = token,
            Password = "pw-admin",
            WorkspaceName = "Acme",
        };
        using (var ctx = Ctx(caller, db))
            Assert.True(
                (await BuildService(caller, ctx, email).ConfirmDeletionAsync(req)).IsSuccess
            );

        using var ctx2 = Ctx(caller, db);
        var second = await BuildService(caller, ctx2, email).ConfirmDeletionAsync(req);
        Assert.False(second.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, second.Message);
    }

    [Fact]
    public async Task Confirm_AfterPasswordChange_LinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var admin = await ctx.Users.FirstAsync(u => u.Id == ws.Admin.Id);
            admin.SecurityStamp = Guid.NewGuid();
            await ctx.SaveChangesAsync();
        }

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_AfterDemotion_LinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var membership = await ctx.Set<WorkspaceMembership>()
                .IgnoreQueryFilters()
                .FirstAsync(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.WorkspaceId);
            membership.RoleId = ws.Deputy.RoleId!.Value; // demote to Deputy
            membership.SecurityStamp = Guid.NewGuid();
            await ctx.SaveChangesAsync();
        }

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_AfterNewerRequest_OldLinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var oldToken = await RequestAndExtractTokenAsync(this, caller, db, email);

        // Force the cooldown to have elapsed, then request again — a newer request voids the old link.
        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var row = await ctx
                .Workspaces.IgnoreQueryFilters()
                .FirstAsync(w => w.Id == ws.WorkspaceId);
            row.DeletionRequestedAt = DateTime.UtcNow.AddMinutes(-10);
            await ctx.SaveChangesAsync();
        }
        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx, email).RequestDeletionAsync()).IsSuccess);

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = oldToken,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_AfterCancel_OldLinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx, email).CancelDeletionAsync()).IsSuccess);

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_EraseTokenOfSameIdentity_Invalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        // Mint an `erase` purpose token for the same identity — must not validate for delete-workspace.
        var resetTokens = RealResetTokens();
        var eraseToken = resetTokens.CreateScoped(
            ws.Admin.PublicId,
            ws.Admin.SecurityStamp,
            TokenPurposes.Erase
        );

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = eraseToken,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_NullPassword_400NotServerError()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = null,
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.CurrentPasswordIncorrect, result.Message);
    }

    [Fact]
    public async Task Confirm_ArabicNameWithRlmMarks_Matches()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed, name: "مساحة العمل");

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        // A copy-paste from a bidi UI can carry an invisible RLM (U+200F) around the text.
        var typed = "‏مساحة العمل‏";

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = typed,
                }
            );
        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public async Task Confirm_FiveWrongPasswords_LockoutAndLinkSpent()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        // Opus MEDIUM (code review fix pass): the link-spend decision now comes from a DEDICATED
        // per-link counter (`delws:{ws}:{requestedAtMs}`), independent of the shared account-level
        // login lockout — so this test shares ONE `IMemoryCache` across every ConfirmDeletionAsync
        // call below (each call builds a fresh WorkspaceLifecycleService, as a fresh HTTP request
        // would; only DI's singleton IMemoryCache — modelled here by this shared instance — carries
        // the count between them). The lockout limiter is still passed (and still trips at its own
        // threshold), but is no longer what spends the link.
        var lockout = new LoginAttemptLimiter(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
            ),
            Microsoft.Extensions.Options.Options.Create(
                new LoginLockoutOptions { Threshold = 5, WindowMinutes = 15 }
            )
        );
        var linkAttemptCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
        );

        for (var i = 0; i < 5; i++)
        {
            using var ctx = Ctx(caller, db);
            var result = await BuildService(
                    caller,
                    ctx,
                    email,
                    lockout: lockout,
                    linkAttemptCache: linkAttemptCache
                )
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "wrong",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.False(result.IsSuccess);
        }

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionRequestedAt);
        Assert.Null(row.DeletionRequestedBy);
        Assert.True(await lockout.IsLockedAsync(ws.Admin.Email));
    }

    /// <summary>Gemini HIGH (code review): before the fix, the link was spent when the SHARED
    /// account-level login lockout tripped (production threshold 10) — coupling a workspace-deletion
    /// link's lifetime to unrelated dashboard login failures. With a realistic prod-like threshold
    /// (10) the account is NOT locked after 5 wrong passwords, yet the link must already be dead —
    /// proving the per-link counter, not the lockout, is what spends it.</summary>
    [Fact]
    public async Task Confirm_LinkSpentOnOwnFifthFailure_EvenWhenAccountLockoutThresholdIsHigher()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        // Production-shaped: threshold 10, well above the link's own 5-failure budget.
        var lockout = new LoginAttemptLimiter(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
            ),
            Microsoft.Extensions.Options.Options.Create(
                new LoginLockoutOptions { Threshold = 10, WindowMinutes = 15 }
            )
        );
        var linkAttemptCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
        );

        for (var i = 0; i < 5; i++)
        {
            using var ctx = Ctx(caller, db);
            var result = await BuildService(
                    caller,
                    ctx,
                    email,
                    lockout: lockout,
                    linkAttemptCache: linkAttemptCache
                )
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "wrong",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.False(result.IsSuccess);
        }

        // The account itself is NOT locked (5 < 10) — proves the spend did not ride on the lockout.
        Assert.False(await lockout.IsLockedAsync(ws.Admin.Email));

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionRequestedAt);
        Assert.Null(row.DeletionRequestedBy);

        // The link itself is dead: even the CORRECT password now fails as DeletionLinkInvalid.
        using var retryCtx = Ctx(caller, db);
        var retry = await BuildService(
                caller,
                retryCtx,
                email,
                lockout: lockout,
                linkAttemptCache: linkAttemptCache
            )
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(retry.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, retry.Message);
    }

    // ── 4. Pause-instead ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PauseInstead_PausesAndSpendsLink()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildService(caller, ctx, email).PauseInsteadAsync(token);
            Assert.True(result.IsSuccess, result.Message);
        }

        using (var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var row = await check
                .Workspaces.IgnoreQueryFilters()
                .FirstAsync(w => w.Id == ws.WorkspaceId);
            Assert.NotNull(row.PausedAt);
            Assert.Null(row.DeletionRequestedAt);
        }

        using var ctx2 = Ctx(caller, db);
        var second = await BuildService(caller, ctx2, email).PauseInsteadAsync(token);
        Assert.False(second.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, second.Message);
    }

    [Fact]
    public async Task PauseInstead_AlreadyPaused_OnlySpendsLink()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx, email).PauseAsync()).IsSuccess);

        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        var audit = new FakeAuditWriter();
        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email, audit).PauseInsteadAsync(token);
        Assert.True(result.IsSuccess, result.Message);
        // Exactly one audit row (Opus LOW — AuditCoverageFilter's StrictCoverage 500).
        Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.WorkspaceDeletionCancelled, audit.Entries[0].Action);
    }

    // ── 5. Cancel ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_ByAnotherAdmin_ClearsSchedule_KeepsPriorPause_MailsAdmins()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();

        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx, email).PauseAsync()).IsSuccess);

        var token = await RequestAndExtractTokenAsync(this, caller, db, email);
        using (var ctx = Ctx(caller, db))
        {
            var confirm = await BuildService(caller, ctx, email)
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "pw-admin",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.True(confirm.IsSuccess, confirm.Message);
        }

        var second = new FakeCurrentUser
        {
            Id = ws.SecondAdmin.PublicId,
            TenantId = ws.WorkspaceId,
        };
        email.Sent.Clear();
        using (var ctx = Ctx(second, db))
        {
            var result = await BuildService(second, ctx, email).CancelDeletionAsync();
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionScheduledFor);
        Assert.NotNull(row.PausedAt); // prior pause survives the cancel
        Assert.Equal(2, email.Sent.Count); // E5 to both live admins
    }

    [Fact]
    public async Task CancelPending_VoidsLink()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildService(caller, ctx, email).CancelDeletionAsync();
            Assert.True(result.IsSuccess, result.Message);
        }

        using var ctx2 = Ctx(caller, db);
        var confirm = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(confirm.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, confirm.Message);
    }

    [Fact]
    public async Task Cancel_AfterRowDeleted_ConflictAlreadyDeleted()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        using (var ctx = Ctx(caller, db))
            Assert.True((await BuildService(caller, ctx).RequestDeletionAsync()).IsSuccess);

        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var row = await ctx.Workspaces.FirstAsync(w => w.Id == ws.WorkspaceId);
            ctx.Workspaces.Remove(row);
            await ctx.SaveChangesAsync();
        }

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2).CancelDeletionAsync();
        // The guard's own precondition read finds nothing at all (never a live row to begin with in
        // this DbContext) — a plain NotFound, not the race-specific AlreadyDeleted Conflict the
        // locked re-check inside the transaction returns when the row vanishes mid-flight (that race
        // needs true concurrency and is not reproducible against the InMemory provider).
        Assert.True(result.IsNotFound);
    }

    // ── 6. Preview ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ReturnsCounts_AndAccountsDeletedWithWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx, email).PreviewDeletionAsync(token);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ws.WorkspaceId, result.Data!.WorkspaceId);
        Assert.Equal("Acme", result.Data.WorkspaceName);
        Assert.Equal(7, result.Data.GraceDays);
        // Every seeded identity was created here and has no membership elsewhere → all 3 counted.
        Assert.Equal(3, result.Data.AccountsDeletedWithWorkspace);
        Assert.True(result.Data.RequesterAccountDeleted);
        Assert.True(result.Data.RequiresPassword);
    }

    // ── 7. R8 tenancy ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantB_Admin_PauseAndCancel_AffectOnlyB()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace a;
        SeededWorkspace b;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            a = SeedWorkspace(seed, "Workspace A");
            b = SeedWorkspace(seed, "Workspace B");
        }

        var callerB = new FakeCurrentUser { Id = b.Admin.PublicId, TenantId = b.WorkspaceId };
        using (var ctx = Ctx(callerB, db))
            Assert.True((await BuildService(callerB, ctx).PauseAsync()).IsSuccess);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var rowA = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == a.WorkspaceId);
        var rowB = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == b.WorkspaceId);
        Assert.Null(rowA.PausedAt);
        Assert.NotNull(rowB.PausedAt);
    }

    [Fact]
    public async Task LinkMintedForA_CannotScheduleB()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace a;
        SeededWorkspace b;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            a = SeedWorkspace(seed, "Workspace A");
            b = SeedWorkspace(seed, "Workspace B");
        }

        var callerA = new FakeCurrentUser { Id = a.Admin.PublicId, TenantId = a.WorkspaceId };
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, callerA, db, email);

        var resetTokens = RealResetTokens();
        Assert.True(
            resetTokens.TryValidateScoped(
                token,
                TokenPurposes.DeleteWorkspace,
                out _,
                out _,
                out var payload
            )
        );
        Assert.NotNull(payload);
        // Tamper: swap the workspace id in the payload for B's — the signature no longer matches.
        var tampered = payload!.Replace(a.WorkspaceId.ToString("N"), b.WorkspaceId.ToString("N"));
        var forgedToken = string.Join(
            '.',
            token
                .Split('.')[..4]
                .Append(
                    Convert
                        .ToBase64String(System.Text.Encoding.UTF8.GetBytes(tampered))
                        .TrimEnd('=')
                        .Replace('+', '-')
                        .Replace('/', '_')
                )
                .Append(token.Split('.')[5])
        );

        using var ctx = Ctx(callerA, db);
        var result = await BuildService(callerA, ctx, email).PreviewDeletionAsync(forgedToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    // ── 8. Operator pause holds the delete (Opus HIGH 3) ─────────────────────────────────────

    [Fact]
    public async Task Job_OperatorPausedDuringGrace_NotDeleted()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        var op = new FakeCurrentUser { IsSuperAdmin = true, Id = Guid.NewGuid() };
        using (var seed = Ctx(op, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);
        using (var ctx = Ctx(caller, db))
        {
            var confirm = await BuildService(caller, ctx, email)
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "pw-admin",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.True(confirm.IsSuccess, confirm.Message);
        }

        // Force the schedule into the past (as if the grace period elapsed) and operator-pause it.
        using (var ctx = Ctx(op, db))
        {
            var row = await ctx
                .Workspaces.IgnoreQueryFilters()
                .FirstAsync(w => w.Id == ws.WorkspaceId);
            row.DeletionScheduledFor = DateTime.UtcNow.AddMinutes(-5);
            await ctx.SaveChangesAsync();
        }
        using (var ctx = Ctx(op, db))
            Assert.True((await BuildService(op, ctx).OperatorPauseAsync(ws.WorkspaceId)).IsSuccess);

        using var jobCtx = Ctx(op, db);
        var jobResult = await BuildService(op, jobCtx).ExecuteDueDeletionAsync(ws.WorkspaceId);
        Assert.False(jobResult.IsSuccess);
        Assert.Equal("held by operator pause", jobResult.Message);

        using var check = Ctx(op, db);
        Assert.True(
            await check.Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Id == ws.WorkspaceId)
        );
    }

    // ── 9. No-tenant / non-super-admin caller (code review fix pass, BLOCKER priority 1) ────────
    //
    // Every test above that exercises an ANONYMOUS or JOB method still builds its service with
    // `AdminCaller(ws)` (a tenant-scoped session caller) or a super-admin `op` — both bypass the
    // Workspace query filter for reasons that have nothing to do with the code path being real. In
    // production these methods run with NO usable tenant claim at all: `AuthController`'s anonymous
    // actions resolve `ICurrentUser` from an unauthenticated `HttpContext` (TenantId/Id null,
    // IsSuperAdmin false), and the hosted `WorkspaceDeletionService` resolves its DI scope with
    // whatever `ICurrentUser` implementation is registered for a request-less scope — never a
    // super-admin. `NoTenantCaller()` reproduces exactly that: no Id, no TenantId, not super admin.
    // Every test below FAILED before the IgnoreQueryFilters fix (verified by temporarily reverting
    // it and re-running) and passes after.

    private static FakeCurrentUser NoTenantCaller() => new();

    [Fact]
    public async Task Confirm_NoTenantCurrentUser_Succeeds()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var admin = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, admin, db, email);

        using var ctx = Ctx(NoTenantCaller(), db);
        var result = await BuildService(NoTenantCaller(), ctx, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ws.WorkspaceId, result.Data!.WorkspaceId);
    }

    [Fact]
    public async Task PauseInstead_NoTenantCurrentUser_Succeeds()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var admin = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, admin, db, email);

        using var ctx = Ctx(NoTenantCaller(), db);
        var result = await BuildService(NoTenantCaller(), ctx, email).PauseInsteadAsync(token);

        Assert.True(result.IsSuccess, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.NotNull(row.PausedAt);
    }

    [Fact]
    public async Task Preview_NoTenantCurrentUser_Succeeds()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var admin = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, admin, db, email);

        using var ctx = Ctx(NoTenantCaller(), db);
        var result = await BuildService(NoTenantCaller(), ctx, email).PreviewDeletionAsync(token);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(ws.WorkspaceId, result.Data!.WorkspaceId);
    }

    /// <summary>SpendLinkAsync's own tracked load (D18.5's 5th-wrong-password spend) — driven through
    /// ConfirmDeletionAsync with a no-tenant caller so the underlying spend happens in the exact
    /// context the anonymous confirm endpoint runs in.</summary>
    [Fact]
    public async Task Confirm_FiveWrongPasswords_NoTenantCurrentUser_LinkSpent()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var admin = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, admin, db, email);

        var lockout = new LoginAttemptLimiter(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
            ),
            Microsoft.Extensions.Options.Options.Create(
                new LoginLockoutOptions { Threshold = 5, WindowMinutes = 15 }
            )
        );
        var linkAttemptCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
        );

        for (var i = 0; i < 5; i++)
        {
            using var ctx = Ctx(NoTenantCaller(), db);
            var result = await BuildService(
                    NoTenantCaller(),
                    ctx,
                    email,
                    lockout: lockout,
                    linkAttemptCache: linkAttemptCache
                )
                .ConfirmDeletionAsync(
                    new ConfirmWorkspaceDeletionRequest
                    {
                        Token = token,
                        Password = "wrong",
                        WorkspaceName = "Acme",
                    }
                );
            Assert.False(result.IsSuccess);
        }

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionRequestedAt);
        Assert.Null(row.DeletionRequestedBy);
    }

    [Fact]
    public async Task SendDueRemindersAsync_NoTenantCurrentUser_StampsReminder_AndMails()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await ctx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-7);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-7); // grace = 7 days >= 48h → reminder eligible
            w.DeletionScheduledFor = now.AddHours(20); // due within the T-24h reminder window
            await ctx.SaveChangesAsync();
        }

        var email = new CapturingEmail();
        // BLOCKER regression: SendDueRemindersAsync runs on the hosted job's own DI scope — no
        // tenant claim, not a super admin. Before the fix its locked tracked load always returned
        // null, so DeletionReminderSentAt was NEVER stamped; for a grace period >= 48h that meant
        // ExecuteDueDeletionAsync (which requires a sent reminder, or grace < 48h) would then
        // PERMANENTLY refuse to ever hard-delete the workspace.
        using (var ctx = Ctx(NoTenantCaller(), db))
            await BuildService(NoTenantCaller(), ctx, email).SendDueRemindersAsync(now);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.NotNull(row.DeletionReminderSentAt);
        Assert.NotEmpty(email.Sent);
    }

    [Fact]
    public async Task ExecuteDueDeletionAsync_NoTenantCurrentUser_HardDeletes()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await ctx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-8);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-8);
            w.DeletionScheduledFor = now.AddMinutes(-5); // due
            w.DeletionReminderSentAt = now.AddHours(-25); // already sent
            await ctx.SaveChangesAsync();
        }

        var email = new CapturingEmail();
        using var ctx2 = Ctx(NoTenantCaller(), db);
        var result = await BuildService(NoTenantCaller(), ctx2, email)
            .ExecuteDueDeletionAsync(ws.WorkspaceId);

        Assert.True(result.IsSuccess, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        Assert.Null(
            await check
                .Workspaces.IgnoreQueryFilters()
                .SingleOrDefaultAsync(w => w.Id == ws.WorkspaceId)
        );
    }

    // ── 10. Concurrency (§6 test 9a, RacingUnitOfWork — as far as InMemory can model it: the real
    // `FOR UPDATE` lock is Postgres-only and is proven in the R11 rehearsal / e2e stack, §9; these
    // model the RACE — a second transaction committing in the gap before the lock — not the lock
    // itself, exactly like Db17HardDeleteAndCleanupTests' own concurrency tests) ──────────────────

    [Fact]
    public async Task Confirm_Concurrent_OnlyOneSchedules()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var winnerCompleted = false;

        var racingUow = new RacingUnitOfWork(
            new UnitOfWork(ctx),
            async () =>
            {
                // Models a second admin tab confirming the SAME link a moment earlier — its own
                // transaction commits inside the gap between this call's pre-lock read and its lock.
                using var concurrentCtx = Ctx(caller, db);
                var concurrentResult = await BuildService(
                        caller,
                        concurrentCtx,
                        new CapturingEmail()
                    )
                    .ConfirmDeletionAsync(
                        new ConfirmWorkspaceDeletionRequest
                        {
                            Token = token,
                            Password = "pw-admin",
                            WorkspaceName = "Acme",
                        }
                    );
                Assert.True(concurrentResult.IsSuccess, concurrentResult.Message);
                winnerCompleted = true;
            }
        );

        var result = await BuildServiceWithUow(racingUow, caller, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );

        Assert.True(winnerCompleted);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.NotNull(row.DeletionScheduledFor);
    }

    [Fact]
    public async Task Cancel_WhileJobHoldsLock_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-8);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-8);
            w.DeletionScheduledFor = now.AddMinutes(-5); // due
            w.DeletionReminderSentAt = now.AddHours(-25); // already sent
            await seedCtx.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        using var ctx = Ctx(caller, db);

        var racingUow = new RacingUnitOfWork(
            new UnitOfWork(ctx),
            async () =>
            {
                // Models the hosted job winning the race: it acquires the lock first, finds the
                // grace period elapsed, and hard-deletes the workspace before this Cancel gets in.
                using var jobCtx = Ctx(NoTenantCaller(), db);
                var jobResult = await BuildService(NoTenantCaller(), jobCtx, new CapturingEmail())
                    .ExecuteDueDeletionAsync(ws.WorkspaceId);
                Assert.True(jobResult.IsSuccess, jobResult.Message);
            }
        );

        var result = await BuildServiceWithUow(racingUow, caller).CancelDeletionAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.Workspace.AlreadyDeleted, result.Message);
    }

    [Fact]
    public async Task Reminder_RacingCancel_NoConstraintViolation()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-7);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-7);
            w.DeletionScheduledFor = now.AddHours(20); // due for the T-24h reminder
            await seedCtx.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        using var ctx = Ctx(NoTenantCaller(), db);

        var racingUow = new RacingUnitOfWork(
            new UnitOfWork(ctx),
            async () =>
            {
                // Models an admin cancelling in the gap before the reminder job's own lock.
                using var cancelCtx = Ctx(caller, db);
                var cancelResult = await BuildService(caller, cancelCtx).CancelDeletionAsync();
                Assert.True(cancelResult.IsSuccess, cancelResult.Message);
            }
        );

        // Must not throw (the schedule-consistency check constraint would reject stamping a
        // reminder on a now-unscheduled row under real Postgres) — the re-check inside the lock
        // must skip cleanly instead.
        await BuildServiceWithUow(racingUow, NoTenantCaller(), email).SendDueRemindersAsync(now);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionReminderSentAt);
        Assert.Null(row.DeletionScheduledFor);
        Assert.Empty(email.Sent);
    }

    // ── 11. Reminder edge cases ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reminder_Missed_ReschedulesPlus24h_Audited()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-7);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-7);
            // Downtime across T-24h: already due (<= now+1h) and no reminder sent yet.
            w.DeletionScheduledFor = now.AddMinutes(10);
            await seedCtx.SaveChangesAsync();
        }

        var email = new CapturingEmail();
        var audit = new FakeAuditWriter();
        using (var ctx = Ctx(NoTenantCaller(), db))
            await BuildService(NoTenantCaller(), ctx, email, audit).SendDueRemindersAsync(now);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.NotNull(row.DeletionReminderSentAt);
        Assert.True(row.DeletionScheduledFor > now.AddHours(23));
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.WorkspaceDeletionRescheduled);
        Assert.NotEmpty(email.Sent);
    }

    [Fact]
    public async Task Reminder_GraceUnder2Days_NotSent()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddHours(-1);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            // Grace < 48h: E2 (confirmed) is the only notice — no separate T-24h reminder.
            w.DeletionConfirmedAt = now.AddHours(-1);
            w.DeletionScheduledFor = now.AddHours(23);
            await seedCtx.SaveChangesAsync();
        }

        var email = new CapturingEmail();
        using (var ctx = Ctx(NoTenantCaller(), db))
            await BuildService(NoTenantCaller(), ctx, email).SendDueRemindersAsync(now);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionReminderSentAt);
        Assert.Empty(email.Sent);
    }

    // ── 12. Preview / actual parity (§6 test 9a) ─────────────────────────────────────────────

    [Fact]
    public async Task Preview_AccountsCount_EqualsRowsActuallyDeleted()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        Guid otherWorkspaceId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            var other = SeedWorkspace(seed, "Other");
            otherWorkspaceId = other.WorkspaceId;

            // secondAdmin (created in `ws`) also has an ENDED membership in `other` — must NOT be
            // counted as deleted-with-workspace (Opus HIGH 1: any state, ended included).
            var endedMembership = new WorkspaceMembership
            {
                UserId = ws.SecondAdmin.Id,
                OwnerId = otherWorkspaceId,
                RoleId = other.AdminRole.Id,
                Role = other.AdminRole,
                IsActive = false,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow.AddDays(-30),
                LeftAt = DateTime.UtcNow.AddDays(-1),
            };
            seed.WorkspaceMemberships.Add(endedMembership);
            await seed.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(caller, db))
        {
            var preview = await BuildService(caller, ctx, email).PreviewDeletionAsync(token);
            Assert.True(preview.IsSuccess, preview.Message);

            // admin + deputy are created here with no membership elsewhere → counted; secondAdmin
            // has an ended membership elsewhere → NOT counted (re-homed instead).
            Assert.Equal(2, preview.Data!.AccountsDeletedWithWorkspace);
        }

        // Parity only needs the SAME shared query used for the delete set — reason "admin" skips
        // the "owner_requested" guarded pre-check (which would otherwise require the deletion to
        // actually be due) without touching IdentitiesDeletedWithWorkspace at all.
        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var uow = new UnitOfWork(ctx);
            var tenants = new TenantService(
                uow,
                new IdentityHasher(),
                new NoopFileStorage(),
                new NoopSettings(),
                new NoopBillingProvider(),
                new MembershipService(uow)
            );
            var hardDelete = await tenants.HardDeleteAsync(ws.WorkspaceId, "admin");
            Assert.True(hardDelete.IsSuccess, hardDelete.Message);
        }

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        // The two identities created only in `ws` are gone; the re-homed secondAdmin survives.
        Assert.Null(
            await verify.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == ws.Admin.Id)
        );
        Assert.Null(
            await verify.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.Id == ws.Deputy.Id)
        );
        var survivor = await verify
            .Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == ws.SecondAdmin.Id);
        Assert.Contains(
            verify
                .WorkspaceMemberships.IgnoreQueryFilters()
                .Where(m => m.UserId == survivor.Id)
                .ToList(),
            m => m.OwnerId == otherWorkspaceId
        );
    }

    // ── 13. Review gaps (REVIEW-DB18-CODE-2026-09-24.md rows G6/O23/O24/O25) ────────────────────

    /// <summary>Opus HIGH 3 / O24: HardDeleteAsync's "owner_requested" guarded pre-check refuses a
    /// workspace that was never actually confirmed (no DeletionScheduledFor at all) — writes no
    /// audit row and touches no files.</summary>
    [Fact]
    public async Task HardDelete_OwnerRequested_NotScheduled_Refused_NoAudit_NoFiles()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var uow = new UnitOfWork(ctx);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var tenants = new TenantService(
            uow,
            new IdentityHasher(),
            files,
            new NoopSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow),
            audit
        );

        var result = await tenants.HardDeleteAsync(ws.WorkspaceId, "owner_requested");

        Assert.False(result.IsSuccess);
        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.NotNull(
            await ctx
                .Workspaces.IgnoreQueryFilters()
                .SingleOrDefaultAsync(w => w.Id == ws.WorkspaceId)
        );
    }

    /// <summary>Opus HIGH 3 / O24: an operator pause holds a DUE scheduled deletion — same guard,
    /// driven directly against TenantService this time (ExecuteDueDeletionAsync's own pre-check
    /// already covers the WorkspaceLifecycleService entry point via Job_OperatorPausedDuringGrace_
    /// NotDeleted above; this proves TenantService.HardDeleteAsync refuses it too, on its own, no
    /// audit/no files, in case anything ever calls it directly again).</summary>
    [Fact]
    public async Task HardDelete_OwnerRequested_OperatorPaused_Refused()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-8);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-8);
            w.DeletionScheduledFor = now.AddMinutes(-5); // due
            w.PausedAt = now.AddHours(-1);
            w.PausedBy = Guid.NewGuid();
            w.PausedByOperator = true; // held
            await seedCtx.SaveChangesAsync();
        }

        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var uow = new UnitOfWork(ctx);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var tenants = new TenantService(
            uow,
            new IdentityHasher(),
            files,
            new NoopSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow),
            audit
        );

        var result = await tenants.HardDeleteAsync(ws.WorkspaceId, "owner_requested");

        Assert.False(result.IsSuccess);
        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
    }

    /// <summary>O24: the hosted job's real hard-delete path (ExecuteDueDeletionAsync →
    /// TenantService.HardDeleteAsync("owner_requested")) actually deletes the owner's files and
    /// writes exactly one System-actor audit row — the existing
    /// ExecuteDueDeletionAsync_NoTenantCurrentUser_HardDeletes test only asserted the workspace row
    /// was gone, never the file-delete/audit side effects.</summary>
    [Fact]
    public async Task Job_DueDeletion_HardDeletes_AuditsSystemActor_AndDeletesFiles()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await ctx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-8);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-8);
            w.DeletionScheduledFor = now.AddMinutes(-5); // due
            w.DeletionReminderSentAt = now.AddHours(-25); // already sent
            await ctx.SaveChangesAsync();
        }

        var email = new CapturingEmail();
        var audit = new FakeAuditWriter();
        var files = new RecordingFileStorage();
        using var ctx2 = Ctx(NoTenantCaller(), db);
        var result = await BuildService(NoTenantCaller(), ctx2, email, audit, fileStorage: files)
            .ExecuteDueDeletionAsync(ws.WorkspaceId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(new[] { ws.WorkspaceId.ToString("N") }, files.DeletedOwners);
        var entry = Assert.Single(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Equal(AuditActorKind.System, entry.ActorKindOverride);
        Assert.Null(entry.OwnerId);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        Assert.Null(
            await check
                .Workspaces.IgnoreQueryFilters()
                .SingleOrDefaultAsync(w => w.Id == ws.WorkspaceId)
        );
    }

    /// <summary>O23/G6: the job's hard-delete races a concurrent Cancel that commits between the
    /// job's own (already-passed) due pre-check and HardDeleteAsync's locked re-check — the locked
    /// re-check must abort cleanly (DeletionPreconditionChangedException, caught by the hosted
    /// loop as a benign skip — see WorkspaceDeletionService's own catch), never touching the
    /// filesystem or writing the hard-deleted audit row. Uses the same RacingUnitOfWork technique as
    /// Cancel_WhileJobHoldsLock_Conflict, roles reversed.</summary>
    [Fact]
    public async Task Job_CancelledBetweenQueryAndDelete_Skipped_NoFileDelete()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var now = DateTime.UtcNow;
        using (var seedCtx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var w = await seedCtx
                .Workspaces.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == ws.WorkspaceId);
            w.DeletionRequestedAt = now.AddDays(-8);
            w.DeletionRequestedBy = ws.Admin.PublicId;
            w.DeletionConfirmedAt = now.AddDays(-8);
            w.DeletionScheduledFor = now.AddMinutes(-5); // due
            w.DeletionReminderSentAt = now.AddHours(-25);
            await seedCtx.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(NoTenantCaller(), db);

        var racingUow = new RacingUnitOfWork(
            new UnitOfWork(ctx),
            async () =>
            {
                // Models an admin cancelling in the gap between the job's own pre-check (already
                // passed, above) and its FOR UPDATE lock inside HardDeleteAsync's transaction.
                using var cancelCtx = Ctx(caller, db);
                var cancelResult = await BuildService(caller, cancelCtx).CancelDeletionAsync();
                Assert.True(cancelResult.IsSuccess, cancelResult.Message);
            }
        );

        await Assert.ThrowsAsync<DeletionPreconditionChangedException>(() =>
            BuildServiceWithUow(racingUow, NoTenantCaller(), audit: audit, fileStorage: files)
                .ExecuteDueDeletionAsync(ws.WorkspaceId)
        );

        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        Assert.Null(row.DeletionScheduledFor); // the race's Cancel won and survives
    }

    /// <summary>O23/G6: PauseInsteadAsync racing a Confirm that wins in the gap between its own
    /// pre-lock read and its lock — the locked re-check's ValidateTokenAsync (which refuses once
    /// DeletionScheduledFor != null) must fail cleanly with DeletionLinkInvalid, never a constraint
    /// violation/500 and never clobbering the winner's schedule. Mirrors
    /// Confirm_Concurrent_OnlyOneSchedules with the two calls' roles reversed.</summary>
    [Fact]
    public async Task PauseInstead_RacingConfirm_NoConstraintViolation()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using var ctx = Ctx(caller, db);
        var winnerCompleted = false;

        var racingUow = new RacingUnitOfWork(
            new UnitOfWork(ctx),
            async () =>
            {
                // Models a concurrent Confirm winning the race — commits inside the gap between
                // PauseInstead's pre-lock read and its own lock.
                using var concurrentCtx = Ctx(caller, db);
                var concurrentResult = await BuildService(
                        caller,
                        concurrentCtx,
                        new CapturingEmail()
                    )
                    .ConfirmDeletionAsync(
                        new ConfirmWorkspaceDeletionRequest
                        {
                            Token = token,
                            Password = "pw-admin",
                            WorkspaceName = "Acme",
                        }
                    );
                Assert.True(concurrentResult.IsSuccess, concurrentResult.Message);
                winnerCompleted = true;
            }
        );

        var result = await BuildServiceWithUow(racingUow, caller, email).PauseInsteadAsync(token);

        Assert.True(winnerCompleted);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var row = await check
            .Workspaces.IgnoreQueryFilters()
            .SingleAsync(w => w.Id == ws.WorkspaceId);
        // The winner's schedule survives — PauseInstead's own write never landed.
        Assert.NotNull(row.DeletionScheduledFor);
        Assert.Null(row.PausedAt);
    }

    /// <summary>Gemini LOW #2 (StateService): a soft-deleted workspace row (DeletedAt != null) is
    /// never frozen, whatever its PausedAt/DeletionScheduledFor columns still carry — the state
    /// service's own explicit `w.DeletedAt == null` predicate.</summary>
    [Fact]
    public async Task StateService_SoftDeletedWorkspace_NotFrozen()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceId,
                    Name = "Acme",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceId,
                    PausedAt = DateTime.UtcNow,
                    DeletedAt = DateTime.UtcNow,
                }
            );
            await seed.SaveChangesAsync();
        }

        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var state = new WorkspaceStateService(new UnitOfWork(ctx));
        var freeze = await state.GetAsync(workspaceId);

        Assert.False(freeze.IsFrozen);
        Assert.False(freeze.IsPaused);
        Assert.False(freeze.PausedByOperator);
        Assert.Null(freeze.DeletionScheduledFor);
    }

    /// <summary>Opus LOW (D18.9): a converted workspace still inside the 72h re-verify window
    /// (DemoExpiresAt still set) but already converted (DemoConvertedAt != null) is a real workspace
    /// — only a LIVE, unconverted demo is blocked from pausing (see Pause_LiveDemo_Refused).</summary>
    [Fact]
    public async Task ConvertedDemoInVerifyWindow_CanPause()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            var row = await seed.Workspaces.FirstAsync(w => w.Id == ws.WorkspaceId);
            row.DemoExpiresAt = DateTime.UtcNow.AddHours(70); // still inside the 72h window...
            row.DemoConvertedAt = DateTime.UtcNow.AddHours(-2); // ...but already converted.
            await seed.SaveChangesAsync();
        }

        var caller = AdminCaller(ws);
        using var ctx = Ctx(caller, db);
        var result = await BuildService(caller, ctx).PauseAsync();
        Assert.True(result.IsSuccess, result.Message);
    }

    /// <summary>DB-18 §6 test 4 gap: once the requesting admin LEAVES the workspace (membership
    /// LeftAt stamped — GetMembershipAsync's own `LeftAt == null` predicate), their still-unexpired
    /// deletion-confirmation link must no longer validate. Mirrors Confirm_AfterDemotion_LinkInvalid,
    /// using MembershipService.EndAsync (the real "leave" mechanic) instead of a role edit.</summary>
    [Fact]
    public async Task Confirm_AfterLeave_LinkInvalid()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
            ws = SeedWorkspace(seed);

        var caller = AdminCaller(ws);
        var email = new CapturingEmail();
        var token = await RequestAndExtractTokenAsync(this, caller, db, email);

        using (var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var membership = await ctx.Set<WorkspaceMembership>()
                .IgnoreQueryFilters()
                .FirstAsync(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.WorkspaceId);
            await new MembershipService(new UnitOfWork(ctx)).EndAsync(
                membership,
                MembershipEndReason.Left,
                ws.Admin.PublicId
            );
            await ctx.SaveChangesAsync();
        }

        using var ctx2 = Ctx(caller, db);
        var result = await BuildService(caller, ctx2, email)
            .ConfirmDeletionAsync(
                new ConfirmWorkspaceDeletionRequest
                {
                    Token = token,
                    Password = "pw-admin",
                    WorkspaceName = "Acme",
                }
            );
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.DeletionLinkInvalid, result.Message);
    }

    // ── 14. Register frozen-workspace guard (D18.11) — AuthService fixture ─────────────────────

    private static void SeedRegisterFixture(
        AppDbContext seed,
        Guid ownerId,
        out int memberRoleId,
        string projectKey = "reg-site"
    )
    {
        var memberRole = new Role
        {
            Name = "Stakeholder",
            GrantsAdmin = false,
            IsActive = true,
            OwnerId = ownerId,
        };
        seed.Roles.Add(memberRole);
        seed.SaveChanges();
        memberRoleId = memberRole.Id;

        seed.Set<Project>()
            .Add(
                new Project
                {
                    Key = projectKey,
                    Name = "Reg Site",
                    OwnerId = ownerId,
                }
            );
        seed.SaveChanges();
    }

    /// <summary>D18.11: a frozen workspace refuses a brand-new stakeholder registration — the guard
    /// runs before any identity/membership lookup at all.</summary>
    [Fact]
    public async Task Register_FrozenWorkspace_Refused()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        int memberRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            SeedRegisterFixture(seed, ws.WorkspaceId, out memberRoleId);
            var row = await seed.Workspaces.FirstAsync(w => w.Id == ws.WorkspaceId);
            row.PausedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        using var ctx = Ctx(NoTenantCaller(), db);
        var result = await BuildAuthService(
                NoTenantCaller(),
                ctx,
                new WorkspaceStateService(new UnitOfWork(ctx))
            )
            .RegisterAsync(
                new RegisterRequest
                {
                    Email = "newstakeholder@t.com",
                    Password = "password123",
                    DisplayName = "New Stakeholder",
                    RoleId = memberRoleId,
                    ProjectKey = "reg-site",
                }
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.FrozenNoNewMembers, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        Assert.Null(
            await check
                .Users.IgnoreQueryFilters()
                .SingleOrDefaultAsync(u => u.Email == "newstakeholder@t.com")
        );
    }

    /// <summary>D18.11: the SAME frozen guard also blocks the "re-apply after rejection" path —
    /// proves the freeze check runs unconditionally before RegisterAsync even inspects the existing
    /// (Rejected) membership, so a rejected stakeholder cannot re-queue while the workspace is
    /// frozen either.</summary>
    [Fact]
    public async Task Register_ReapplyAfterRejection_FrozenWorkspace_Refused()
    {
        var db = Guid.NewGuid().ToString();
        SeededWorkspace ws;
        int memberRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            ws = SeedWorkspace(seed);
            SeedRegisterFixture(seed, ws.WorkspaceId, out memberRoleId);

            var memberRole = await seed.Roles.SingleAsync(r => r.Id == memberRoleId);
            var identity = new User
            {
                Email = "rejected@t.com",
                PasswordHash = "h:password123",
                DisplayName = "Rejected",
                PublicId = Guid.NewGuid(),
                RoleId = memberRoleId,
                IsActive = false,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, ws.WorkspaceId, memberRole);

            var membership = await seed.Set<WorkspaceMembership>()
                .IgnoreQueryFilters()
                .SingleAsync(m => m.UserId == identity.Id && m.OwnerId == ws.WorkspaceId);
            membership.ApprovalStatus = ApprovalStatus.Rejected;
            await seed.SaveChangesAsync();

            var row = await seed.Workspaces.FirstAsync(w => w.Id == ws.WorkspaceId);
            row.PausedAt = DateTime.UtcNow;
            await seed.SaveChangesAsync();
        }

        using var ctx = Ctx(NoTenantCaller(), db);
        var result = await BuildAuthService(
                NoTenantCaller(),
                ctx,
                new WorkspaceStateService(new UnitOfWork(ctx))
            )
            .RegisterAsync(
                new RegisterRequest
                {
                    Email = "rejected@t.com",
                    Password = "password123",
                    DisplayName = "Rejected",
                    RoleId = memberRoleId,
                    ProjectKey = "reg-site",
                }
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.FrozenNoNewMembers, result.Message);

        using var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var membershipAfter = await check
            .Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Include(m => m.User)
            .SingleAsync(m => m.OwnerId == ws.WorkspaceId && m.User.Email == "rejected@t.com");
        // Still Rejected — the frozen guard fired before the re-queue logic was ever reached.
        Assert.Equal(ApprovalStatus.Rejected, membershipAfter.ApprovalStatus);
    }
}
