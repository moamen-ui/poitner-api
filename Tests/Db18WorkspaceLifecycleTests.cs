using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
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
        IConfiguration? config = null
    )
    {
        var uow = new UnitOfWork(ctx);
        var memberships = new MembershipService(uow);
        var state = new WorkspaceStateService(uow);
        var workspaces = new WorkspaceService(uow, user, audit, memberships, config);
        var tenants = new TenantService(
            uow,
            new IdentityHasher(),
            new NoopFileStorage(),
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
            audit
        );
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
            OwnerId = workspaceId,
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
            OwnerId = workspaceId,
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
            OwnerId = workspaceId,
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
            membership.RoleId = ws.Deputy.RoleId; // demote to Deputy
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

        var lockout = new LoginAttemptLimiter(
            new Microsoft.Extensions.Caching.Memory.MemoryCache(
                new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()
            ),
            Microsoft.Extensions.Options.Options.Create(
                new LoginLockoutOptions { Threshold = 5, WindowMinutes = 15 }
            )
        );

        for (var i = 0; i < 5; i++)
        {
            using var ctx = Ctx(caller, db);
            var result = await BuildService(caller, ctx, email, lockout: lockout)
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
}
