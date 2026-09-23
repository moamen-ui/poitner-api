using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-17 (F4): demo state moved onto <c>workspaces</c>. Covers ExtendAsync (self-service, §3.6),
/// WarnExpiringAsync/SweepThrottleRowsAsync (§3.5/§3.7), OnEmailVerifiedAsync (§3.4, pre-wired
/// verification mode) and the additional UpgradeAsync facts (workspace name, session-workspace
/// targeting, tenant isolation). Uses the same InMemory + real UnitOfWork/MembershipService harness
/// as <see cref="DemoUpgradeTests"/>.
/// </summary>
public class Db17DemoServiceTests
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

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;

        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.Email;

        public string IssueSelection(User user) => "selection-for-" + user.Email;

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "imp-for-" + user.Email;
    }

    private sealed class SpyEmailService : IEmailService
    {
        public List<(string To, string Subject, string Body)> Sent { get; } = [];
        public bool ReturnValue { get; set; } = true;

        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
        {
            Sent.Add((to, subject, htmlBody));
            return Task.FromResult(ReturnValue);
        }
    }

    /// <summary>Settings that can be overridden per test (DemoTtlHours etc.); everything else falls
    /// back to the caller's supplied default.</summary>
    private sealed class FakeSettings : ISettingsService
    {
        public Dictionary<string, int> Ints { get; } = new();

        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) =>
            Task.FromResult(Ints.TryGetValue(key, out var v) ? v : fallback);

        public Task SetIntAsync(string key, int value)
        {
            Ints[key] = value;
            return Task.CompletedTask;
        }
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

        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(DefaultBranding());
    }

    private sealed class RecordingAuditWriter : IAuditWriter
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task WriteAsync(AuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext Ctx(string dbName, IConfiguration? config = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser(),
            config ?? new ConfigurationBuilder().Build()
        );

    private static DemoService BuildService(
        AppDbContext db,
        FakeSettings? settings = null,
        SpyEmailService? email = null,
        RecordingAuditWriter? audit = null,
        IConfiguration? config = null
    )
    {
        var uow = new UnitOfWork(db);
        return new DemoService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenService(),
            email ?? new SpyEmailService(),
            settings ?? new FakeSettings(),
            new NoopBrandingService(),
            new MembershipService(uow),
            audit,
            config: config
        );
    }

    /// <summary>Seeds a live demo workspace (admin identity + membership + Workspace row with
    /// DemoExpiresAt set) — the shape UpgradeAsync/ExtendAsync/Warn now read.</summary>
    private static (User Admin, Guid WorkspaceId) SeedDemoWorkspace(
        AppDbContext db,
        DateTime? demoExpiresAt = null,
        DateTime? demoExtendedAt = null,
        int? ttlOverride = null,
        string? recipientEmail = "real@user.com"
    )
    {
        var workspaceId = Guid.NewGuid();
        var pid = Guid.NewGuid();
        var role = db.Roles.FirstOrDefault(r => r.Name == "Workspace Admin");
        if (role == null)
        {
            role = new Role { Name = "Workspace Admin", OwnerId = null };
            db.Roles.Add(role);
            db.SaveChanges();
        }

        var expires = demoExpiresAt ?? DateTime.UtcNow.AddHours(24);
        var admin = new User
        {
            PublicId = pid,
            Email = $"demo-{pid.ToString("N")[..8]}@demo.pointer",
            PasswordHash = "hash:demo",
            DisplayName = "Demo User",
            RoleId = role.Id,
            Role = role,
            OwnerId = workspaceId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            ExpiresAt = expires,
            DemoTtlHoursOverride = ttlOverride,
            RecipientEmail = recipientEmail,
        };
        db.Users.Add(admin);
        db.SaveChanges();

        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = expires,
                DemoExtendedAt = demoExtendedAt,
                DemoTtlHoursOverride = ttlOverride,
            }
        );
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = admin.Id,
                OwnerId = workspaceId,
                RoleId = role.Id,
                Role = role,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        return (admin, workspaceId);
    }

    // ── ExtendAsync (§3.6) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extend_Once_MovesExpiry_StampsExtendedAt_AuditsDemoExtended()
    {
        var db = Ctx(nameof(Extend_Once_MovesExpiry_StampsExtendedAt_AuditsDemoExtended));
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var audit = new RecordingAuditWriter();
        var svc = BuildService(db, audit: audit);

        var result = await svc.ExtendAsync(admin.PublicId, workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.Data!.CanExtend);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.DemoExtended, entry.Action);
        Assert.True(entry.After!.ContainsKey("expires_at"));

        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.NotNull(workspace.DemoExtendedAt);
        Assert.True(workspace.DemoExpiresAt > DateTime.UtcNow.AddHours(23));

        // Dual-write onto the admin identity (D17.1).
        var reloadedAdmin = db.Users.Single(u => u.PublicId == admin.PublicId);
        Assert.True(reloadedAdmin.DemoExtended);
    }

    [Fact]
    public async Task Extend_Twice_Fails_AlreadyExtended()
    {
        var db = Ctx(nameof(Extend_Twice_Fails_AlreadyExtended));
        var (admin, workspaceId) = SeedDemoWorkspace(db, demoExtendedAt: DateTime.UtcNow);
        var svc = BuildService(db);

        var result = await svc.ExtendAsync(admin.PublicId, workspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.AlreadyExtended, result.Message);
    }

    [Fact]
    public async Task Extend_AfterExpiry_Fails()
    {
        var db = Ctx(nameof(Extend_AfterExpiry_Fails));
        var (admin, workspaceId) = SeedDemoWorkspace(
            db,
            demoExpiresAt: DateTime.UtcNow.AddHours(-1)
        );
        var svc = BuildService(db);

        var result = await svc.ExtendAsync(admin.PublicId, workspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }

    [Fact]
    public async Task Extend_NonDemoWorkspace_Forbidden()
    {
        var db = Ctx(nameof(Extend_NonDemoWorkspace_Forbidden));
        var workspaceId = Guid.NewGuid();
        var role = new Role { Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        var user = new User
        {
            PublicId = Guid.NewGuid(),
            Email = "real@x.com",
            PasswordHash = "h",
            DisplayName = "Real",
            RoleId = role.Id,
            Role = role,
            OwnerId = workspaceId,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        db.Users.Add(user);
        db.SaveChanges();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Real Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = user.Id,
                OwnerId = workspaceId,
                RoleId = role.Id,
                Role = role,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        var svc = BuildService(db);
        var result = await svc.ExtendAsync(user.PublicId, workspaceId);

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Demo.NotDemoUser, result.Message);
    }

    [Fact]
    public async Task Extend_UsesTtlOverride_WhenSet()
    {
        var db = Ctx(nameof(Extend_UsesTtlOverride_WhenSet));
        var expires = DateTime.UtcNow.AddHours(1);
        var (admin, workspaceId) = SeedDemoWorkspace(db, demoExpiresAt: expires, ttlOverride: 5);
        var svc = BuildService(db);

        var result = await svc.ExtendAsync(admin.PublicId, workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        // anchor = expires (still in the future) + 5h override, not the global default (24h).
        Assert.True(result.Data!.ExpiresAt <= expires.AddHours(5).AddMinutes(1));
        Assert.True(result.Data!.ExpiresAt >= expires.AddHours(5).AddMinutes(-1));
    }

    [Fact]
    public async Task Extend_TargetsSessionWorkspace_Only()
    {
        // One identity administering two demos (Gemini Pro #3) — extending A must not touch B.
        var db = Ctx(nameof(Extend_TargetsSessionWorkspace_Only));
        var (adminA, workspaceA) = SeedDemoWorkspace(db);
        var roleA = db.Roles.Single(r => r.Name == "Workspace Admin");
        var workspaceB = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceB,
                Name = "Demo Workspace B",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceB,
                DemoExpiresAt = DateTime.UtcNow.AddHours(24),
            }
        );
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = adminA.Id,
                OwnerId = workspaceB,
                RoleId = roleA.Id,
                Role = roleA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        var svc = BuildService(db);
        var result = await svc.ExtendAsync(adminA.PublicId, workspaceA);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(db.Workspaces.Single(w => w.Id == workspaceB).DemoExtendedAt);
        Assert.NotNull(db.Workspaces.Single(w => w.Id == workspaceA).DemoExtendedAt);
    }

    // ── R8: tenant B cannot extend/convert tenant A's demo ──────────────────────────────────

    [Fact]
    public async Task Extend_TenantB_Admin_CannotExtendA()
    {
        var db = Ctx(nameof(Extend_TenantB_Admin_CannotExtendA));
        var (_, workspaceA) = SeedDemoWorkspace(db);
        var (adminB, _) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        var result = await svc.ExtendAsync(adminB.PublicId, workspaceA);

        Assert.True(result.IsForbidden);
        Assert.Null(db.Workspaces.Single(w => w.Id == workspaceA).DemoExtendedAt);
    }

    [Fact]
    public async Task Upgrade_TenantB_Identity_CannotConvertA()
    {
        var db = Ctx(nameof(Upgrade_TenantB_Identity_CannotConvertA));
        var (_, workspaceA) = SeedDemoWorkspace(db);
        var (adminB, _) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        var result = await svc.UpgradeAsync(
            adminB.PublicId,
            workspaceA,
            new UpgradeDemoRequest { Email = "someone@x.com", Password = "supersecret" }
        );

        Assert.True(result.IsForbidden);
    }

    // ── UpgradeAsync additions (§3.4) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Upgrade_ClearsWorkspaceTtl_SetsConverted_RenamesToPlaceholder_KeepsData()
    {
        var db = Ctx(
            nameof(Upgrade_ClearsWorkspaceTtl_SetsConverted_RenamesToPlaceholder_KeepsData)
        );
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        var result = await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest { Email = "permanent@user.com", Password = "supersecret" }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(result.Data!.User.DemoExpiresAt);

        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.Null(workspace.DemoExpiresAt);
        Assert.NotNull(workspace.DemoConvertedAt);
        Assert.Equal(Workspace.PlaceholderName, workspace.Name);
    }

    [Fact]
    public async Task Upgrade_WithWorkspaceName_SetsIt()
    {
        var db = Ctx(nameof(Upgrade_WithWorkspaceName_SetsIt));
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        var result = await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest
            {
                Email = "permanent@user.com",
                Password = "supersecret",
                WorkspaceName = "Acme Inc",
            }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("Acme Inc", db.Workspaces.Single(w => w.Id == workspaceId).Name);
    }

    [Fact]
    public async Task Upgrade_WorkspaceNameTooLong_Fails()
    {
        var db = Ctx(nameof(Upgrade_WorkspaceNameTooLong_Fails));
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        var result = await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest
            {
                Email = "permanent@user.com",
                Password = "supersecret",
                WorkspaceName = new string('a', 121),
            }
        );

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Upgrade_ExpiredWorkspace_Fails_DemoExpired()
    {
        // The workspace's own TTL lapsed, but the legacy users.expires_at happens to be null — the
        // workspace is the authority, not the user row.
        var db = Ctx(nameof(Upgrade_ExpiredWorkspace_Fails_DemoExpired));
        var (admin, workspaceId) = SeedDemoWorkspace(
            db,
            demoExpiresAt: DateTime.UtcNow.AddHours(-1)
        );
        var user = db.Users.Single(u => u.PublicId == admin.PublicId);
        user.ExpiresAt = null;
        db.SaveChanges();

        var svc = BuildService(db);
        var result = await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest { Email = "permanent@user.com", Password = "supersecret" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }

    [Fact]
    public async Task Upgrade_TargetsSessionWorkspace_NotAnotherDemoOfSameIdentity()
    {
        var db = Ctx(nameof(Upgrade_TargetsSessionWorkspace_NotAnotherDemoOfSameIdentity));
        var (adminA, workspaceA) = SeedDemoWorkspace(db);
        var roleA = db.Roles.Single(r => r.Name == "Workspace Admin");
        var workspaceB = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceB,
                Name = "Demo Workspace B",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceB,
                DemoExpiresAt = DateTime.UtcNow.AddHours(24),
            }
        );
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = adminA.Id,
                OwnerId = workspaceB,
                RoleId = roleA.Id,
                Role = roleA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        var svc = BuildService(db);
        var result = await svc.UpgradeAsync(
            adminA.PublicId,
            workspaceA,
            new UpgradeDemoRequest { Email = "permanent@user.com", Password = "supersecret" }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.NotNull(db.Workspaces.Single(w => w.Id == workspaceA).DemoConvertedAt);
        Assert.Null(db.Workspaces.Single(w => w.Id == workspaceB).DemoConvertedAt);
        Assert.NotNull(db.Workspaces.Single(w => w.Id == workspaceB).DemoExpiresAt);
    }

    [Fact]
    public async Task Upgrade_WithVerificationFlag_KeepsTtl72h_ClearedByOnEmailVerified()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Demo:ConvertRequiresVerification"] = "true" }
            )
            .Build();
        var db = Ctx(
            nameof(Upgrade_WithVerificationFlag_KeepsTtl72h_ClearedByOnEmailVerified),
            config
        );
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildService(db, config: config);

        var result = await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest { Email = "permanent@user.com", Password = "supersecret" }
        );

        Assert.True(result.IsSuccess, result.Message);
        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.NotNull(workspace.DemoConvertedAt);
        Assert.NotNull(workspace.DemoExpiresAt);
        Assert.True(workspace.DemoExpiresAt > DateTime.UtcNow.AddHours(71));

        var identity = db.Users.Single(u => u.PublicId == admin.PublicId);
        await svc.OnEmailVerifiedAsync(identity);

        Assert.Null(db.Workspaces.Single(w => w.Id == workspaceId).DemoExpiresAt);
    }

    [Fact]
    public async Task OnEmailVerified_FlagOff_NoOp()
    {
        var db = Ctx(nameof(OnEmailVerified_FlagOff_NoOp));
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildService(db);

        await svc.UpgradeAsync(
            admin.PublicId,
            workspaceId,
            new UpgradeDemoRequest { Email = "permanent@user.com", Password = "supersecret" }
        );
        // Flag off: convert already cleared DemoExpiresAt — calling OnEmailVerifiedAsync must be a
        // harmless no-op (finds nothing to clear).
        var identity = db.Users.Single(u => u.PublicId == admin.PublicId);
        await svc.OnEmailVerifiedAsync(identity);

        Assert.Null(db.Workspaces.Single(w => w.Id == workspaceId).DemoExpiresAt);
    }

    // ── WarnExpiringAsync (§3.5) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WarnExpiring_SendsOnce_ToRecipientEmail_StampsEvenWhenSendFails()
    {
        var db = Ctx(nameof(WarnExpiring_SendsOnce_ToRecipientEmail_StampsEvenWhenSendFails));
        var (_, workspaceId) = SeedDemoWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(1));
        var email = new SpyEmailService { ReturnValue = false };
        var svc = BuildService(db, email: email);

        var warned = await svc.WarnExpiringAsync(DateTime.UtcNow, TimeSpan.FromHours(2));

        Assert.Equal(1, warned);
        Assert.Single(email.Sent);
        Assert.Equal("real@user.com", email.Sent[0].To);
        Assert.NotNull(db.Workspaces.Single(w => w.Id == workspaceId).DemoExpiryWarnedAt);

        // Second call: already warned → nothing sent.
        var warnedAgain = await svc.WarnExpiringAsync(DateTime.UtcNow, TimeSpan.FromHours(2));
        Assert.Equal(0, warnedAgain);
        Assert.Single(email.Sent);
    }

    [Fact]
    public async Task WarnExpiring_OutsideWindow_Nothing()
    {
        var db = Ctx(nameof(WarnExpiring_OutsideWindow_Nothing));
        SeedDemoWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(10));
        var email = new SpyEmailService();
        var svc = BuildService(db, email: email);

        var warned = await svc.WarnExpiringAsync(DateTime.UtcNow, TimeSpan.FromHours(2));

        Assert.Equal(0, warned);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task WarnExpiring_NoRecipientEmail_StampsWithoutSending()
    {
        var db = Ctx(nameof(WarnExpiring_NoRecipientEmail_StampsWithoutSending));
        var (_, workspaceId) = SeedDemoWorkspace(
            db,
            demoExpiresAt: DateTime.UtcNow.AddHours(1),
            recipientEmail: null
        );
        var email = new SpyEmailService();
        var svc = BuildService(db, email: email);

        var warned = await svc.WarnExpiringAsync(DateTime.UtcNow, TimeSpan.FromHours(2));

        Assert.Equal(1, warned);
        Assert.Empty(email.Sent);
        Assert.NotNull(db.Workspaces.Single(w => w.Id == workspaceId).DemoExpiryWarnedAt);
    }

    // ── SweepThrottleRowsAsync (§3.7, R14) ───────────────────────────────────────────────────

    [Fact]
    public async Task SweepThrottleRows_DeletesOldHashedAndLegacyRawRows_KeepsToday()
    {
        var db = Ctx(nameof(SweepThrottleRows_DeletesOldHashedAndLegacyRawRows_KeepsToday));
        var now = DateTime.UtcNow;
        // NOTE: the synchronous SaveChanges() this fixture uses to seed does NOT auto-stamp
        // CreatedAt (only the async SaveChangesAsync() override does — AppDbContext.cs) — set it
        // explicitly on every row so the 2-day cutoff has something real to compare against.
        var today = new AppSetting
        {
            Key = $"demo_email_{PseudonymHasher.EmailHash("a@x.com")}_{now:yyyyMMdd}",
            Value = "1",
            CreatedAt = now,
        };
        var oldHashed = new AppSetting
        {
            Key = $"demo_email_{PseudonymHasher.EmailHash("b@x.com")}_{now.AddDays(-3):yyyyMMdd}",
            Value = "1",
            CreatedAt = now.AddDays(-3),
        };
        var oldRaw = new AppSetting
        {
            Key = $"demo_email_someone@x.com_{now.AddDays(-3):yyyyMMdd}",
            Value = "1",
            CreatedAt = now.AddDays(-3),
        };
        db.AppSettings.AddRange(today, oldHashed, oldRaw);
        db.SaveChanges();

        var svc = BuildService(db);
        var deleted = await svc.SweepThrottleRowsAsync(now);

        Assert.Equal(2, deleted);
        var remaining = db.AppSettings.IgnoreQueryFilters().Select(s => s.Key).ToList();
        Assert.Single(remaining);
        Assert.Contains(today.Key, remaining);
    }

    // ── Provision (§3.3/§3.6 dual-write + hashed throttle) ──────────────────────────────────

    private static void SeedWorkspaceAdminRole(AppDbContext db)
    {
        if (!db.Roles.Any(r => r.Name == "Workspace Admin"))
        {
            db.Roles.Add(new Role { Name = "Workspace Admin", OwnerId = null });
            db.SaveChanges();
        }
    }

    [Fact]
    public async Task Provision_SetsWorkspaceDemoExpiresAt_AndUserExpiresAt_SameInstant()
    {
        var db = Ctx(nameof(Provision_SetsWorkspaceDemoExpiresAt_AndUserExpiresAt_SameInstant));
        SeedWorkspaceAdminRole(db);
        var svc = BuildService(db);

        var result = await svc.ProvisionAsync("https://demo.pointer.example", "person@real.com");

        Assert.True(result.IsSuccess, result.Message);
        var user = db.Users.IgnoreQueryFilters().Single(u => u.IsDemo);
        var workspace = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == user.OwnerId);
        Assert.Equal(user.ExpiresAt, workspace.DemoExpiresAt);
        Assert.Null(workspace.DemoConvertedAt);
        Assert.Null(workspace.DemoExtendedAt);
    }

    [Fact]
    public async Task Provision_ThrottleKey_IsHashed_NeverContainsAddress()
    {
        var db = Ctx(nameof(Provision_ThrottleKey_IsHashed_NeverContainsAddress));
        SeedWorkspaceAdminRole(db);
        var svc = BuildService(db);

        var result = await svc.ProvisionAsync("https://demo.pointer.example", "Person@Real.com");
        Assert.True(result.IsSuccess, result.Message);

        Assert.False(db.AppSettings.IgnoreQueryFilters().Any(s => s.Key.Contains("@")));
        var key = Assert.Single(db.AppSettings.IgnoreQueryFilters().Select(s => s.Key));
        Assert.Matches(@"^demo_email_[0-9a-f]{16}_\d{8}$", key);
    }

    [Fact]
    public async Task Provision_PerEmailLimit_StillCountsAcrossCaseVariants()
    {
        var db = Ctx(nameof(Provision_PerEmailLimit_StillCountsAcrossCaseVariants));
        SeedWorkspaceAdminRole(db);
        var settings = new FakeSettings();
        settings.Ints[ISettingsService.DemoPerEmailPerDay] = 1;
        var svc = BuildService(db, settings: settings);

        var first = await svc.ProvisionAsync("https://demo.pointer.example", "Foo@X.com");
        Assert.True(first.IsSuccess, first.Message);

        var second = await svc.ProvisionAsync("https://demo.pointer.example", "foo@x.com");
        Assert.False(second.IsSuccess);
    }
}
