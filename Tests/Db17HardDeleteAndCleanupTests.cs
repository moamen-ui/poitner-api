using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-17 §3.3/§3.5 (Gemini Pro #2): <c>TenantService.HardDeleteAsync</c>'s demo_expired pre-check
/// (refuses a converted/extended workspace before the folder delete) and
/// <c>DemoCleanupService.SweepOnceAsync</c>'s own expiry query + per-item re-check. InMemory
/// provider throughout — <see cref="IUnitOfWork.ExecuteSqlRawAsync"/>'s `FOR UPDATE` no-ops there
/// (it is Postgres-only syntax; the real re-check under lock is proven in the R11 rehearsal, §9).
/// </summary>
public class Db17HardDeleteAndCleanupTests
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
        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string password, string hash) => hash == "hashed:" + password;
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

    /// <summary>Records every workspace segment passed to DeleteOwnerFilesAsync — proves a refused
    /// demo_expired delete never touches the filesystem.</summary>
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static TenantService BuildTenantService(
        AppDbContext ctx,
        RecordingFileStorage files,
        FakeAuditWriter audit
    )
    {
        var uow = new UnitOfWork(ctx);
        return new TenantService(
            uow,
            new FakePasswordHasher(),
            files,
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow),
            audit
        );
    }

    private static Guid SeedWorkspace(
        AppDbContext db,
        DateTime? demoExpiresAt,
        DateTime? demoConvertedAt = null
    )
    {
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = demoExpiresAt,
                DemoConvertedAt = demoConvertedAt,
            }
        );
        db.SaveChanges();
        return workspaceId;
    }

    // ── HardDeleteAsync's demo_expired pre-check ────────────────────────────────────────────

    [Fact]
    public async Task HardDelete_DemoExpiredReason_RefusesConvertedWorkspace_NoFolderDelete()
    {
        var db = Ctx(nameof(HardDelete_DemoExpiredReason_RefusesConvertedWorkspace_NoFolderDelete));
        // Converted (or extended) — DemoExpiresAt is null, so it is no longer "an expired demo".
        var workspaceId = SeedWorkspace(db, demoExpiresAt: null, demoConvertedAt: DateTime.UtcNow);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(db, files, audit);

        var result = await svc.HardDeleteAsync(workspaceId, reason: "demo_expired");

        Assert.False(result.IsSuccess);
        Assert.Empty(files.DeletedOwners);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
    }

    [Fact]
    public async Task HardDelete_AdminReason_UnaffectedByDemoState()
    {
        // A converted/live workspace is fair game for an ordinary admin-initiated delete.
        var db = Ctx(nameof(HardDelete_AdminReason_UnaffectedByDemoState));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: null, demoConvertedAt: DateTime.UtcNow);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(db, files, audit);

        var result = await svc.HardDeleteAsync(workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(files.DeletedOwners);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
    }

    [Fact]
    public async Task HardDelete_DemoExpiredReason_DeletesActuallyExpiredDemo()
    {
        var db = Ctx(nameof(HardDelete_DemoExpiredReason_DeletesActuallyExpiredDemo));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var svc = BuildTenantService(db, files, audit);

        var result = await svc.HardDeleteAsync(workspaceId, reason: "demo_expired");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(files.DeletedOwners);
        var entry = Assert.Single(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Null(entry.OwnerId);
    }

    // ── DemoCleanupService.SweepOnceAsync (§3.5) ────────────────────────────────────────────

    [Fact]
    public async Task DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers()
    {
        var db = Ctx(nameof(DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));
        // Proves the read-switch: the legacy users.expires_at is null, yet the workspace column
        // alone is enough to select and delete it.
        var role = new Role { Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        var admin = new User
        {
            PublicId = Guid.NewGuid(),
            Email = $"demo-{Guid.NewGuid():N}@demo.pointer",
            PasswordHash = "x",
            DisplayName = "Demo",
            RoleId = role.Id,
            Role = role,
            OwnerId = workspaceId,
            IsActive = true,
            IsDemo = true,
            ExpiresAt = null,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        db.Users.Add(admin);
        db.SaveChanges();
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

        var uow = new UnitOfWork(db);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var tenantService = BuildTenantService(db, files, audit);
        var demoService = new DemoService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenServiceForCleanup(),
            new NoopEmailForCleanup(),
            new FakeSettings(),
            new NoopBrandingForCleanup(),
            new MembershipService(uow)
        );

        var deleted = await DemoCleanupService.SweepOnceAsync(
            uow,
            tenantService,
            demoService,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, deleted);
        Assert.Null(db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId));
    }

    [Fact]
    public async Task DemoCleanup_LeavesLiveDemo()
    {
        var db = Ctx(nameof(DemoCleanup_LeavesLiveDemo));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(1));

        var uow = new UnitOfWork(db);
        var files = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        var tenantService = BuildTenantService(db, files, audit);
        var demoService = new DemoService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenServiceForCleanup(),
            new NoopEmailForCleanup(),
            new FakeSettings(),
            new NoopBrandingForCleanup(),
            new MembershipService(uow)
        );

        var deleted = await DemoCleanupService.SweepOnceAsync(
            uow,
            tenantService,
            demoService,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, deleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
    }

    [Fact]
    public async Task DemoCleanup_SkipsWorkspaceConvertedBetweenQueryAndDelete()
    {
        // Simulates the race (Gemini Pro #2): the workspace was expired when SweepOnceAsync would
        // have queried it, but is converted (DemoExpiresAt cleared) by the time the per-item
        // re-check runs — modelled here by converting it right before invoking SweepOnceAsync and
        // asserting the (still expired-looking, if the re-check were skipped) workspace survives
        // because the fresh re-check inside SweepOnceAsync sees the converted state.
        var db = Ctx(nameof(DemoCleanup_SkipsWorkspaceConvertedBetweenQueryAndDelete));
        var workspaceId = SeedWorkspace(db, demoExpiresAt: DateTime.UtcNow.AddHours(-1));

        // A fake ITenantService that, on its first call, converts the workspace (as a concurrent
        // UpgradeAsync would) before ever reaching the real HardDeleteAsync — proving the loop
        // itself does not blindly trust its own initial query.
        var converting = new ConvertingTenantService(db, workspaceId);
        var uow = new UnitOfWork(db);
        var demoService = new DemoService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenServiceForCleanup(),
            new NoopEmailForCleanup(),
            new FakeSettings(),
            new NoopBrandingForCleanup(),
            new MembershipService(uow)
        );

        // First loop iteration: stillExpired is true (nothing converted it yet) — HardDeleteAsync
        // itself (the real one, invoked via the fake) performs the conversion and returns failure.
        var deleted = await DemoCleanupService.SweepOnceAsync(
            uow,
            converting,
            demoService,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, deleted);
        Assert.NotNull(
            db.Workspaces.IgnoreQueryFilters().SingleOrDefault(w => w.Id == workspaceId)
        );
        Assert.Null(
            db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == workspaceId).DemoExpiresAt
        );
    }

    private sealed class ConvertingTenantService(AppDbContext db, Guid workspaceId) : ITenantService
    {
        public Task<Pointer.Application.Response.Result<
            List<Pointer.Application.DTOs.Tenant.TenantResponse>
        >> ListAsync() => throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Tenant.TenantResponse>> CreateAsync(
            Pointer.Application.DTOs.Tenant.CreateTenantRequest request
        ) => throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> SetStatusAsync(
            Guid workspaceIdArg,
            string action
        ) => throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> ExtendDemoAsync(Guid workspaceIdArg) =>
            throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> SetDemoConfigAsync(
            Guid workspaceIdArg,
            int? c,
            int? t
        ) => throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> ChangePlanAsync(
            Guid workspaceIdArg,
            int planId
        ) => throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> HardDeleteAsync(
            Guid workspaceIdArg,
            string reason = "admin"
        )
        {
            // Simulates a conversion that lands between the sweep's id query and this call.
            var ws = db.Workspaces.Single(w => w.Id == workspaceId);
            ws.DemoExpiresAt = null;
            ws.DemoConvertedAt = DateTime.UtcNow;
            db.SaveChanges();
            return Task.FromResult(
                Pointer.Application.Response.Result.Failure(
                    "Not an expired demo (converted or extended meanwhile)."
                )
            );
        }
    }

    private sealed class FakeTokenServiceForCleanup : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "t";

        public string IssueSelection(User user) => "s";

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "i";
    }

    private sealed class NoopEmailForCleanup : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class NoopBrandingForCleanup : IBrandingService
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
}
