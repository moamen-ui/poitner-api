using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Resources;
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
/// DB-17 review finding #5 (test debt): the operator (Guid-workspace) paths —
/// <c>TenantService.ExtendDemoAsync</c>/<c>SetDemoConfigAsync</c> — had thin-to-no coverage before
/// this review. Also finding #6 (LOW): both refuse a CONVERTED workspace (<c>DemoConvertedAt !=
/// null</c>) with <c>Demo.AlreadyUpgraded</c>, even when <c>DemoExpiresAt</c> is still non-null for
/// the <c>Demo:ConvertRequiresVerification</c> re-verification grace. And the operator/self-service
/// extension interplay: both share the SAME one-extension-total flag (workspaces.demo_extended_at),
/// proven end to end with the real <see cref="TenantService"/> and <see cref="DemoService"/> sharing one
/// workspace.
/// </summary>
public class Db17TenantServiceDemoOperatorTests
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
            "t";

        public string IssueSelection(User user) => "s";

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "i";
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
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

    private static AppDbContext Ctx(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static TenantService BuildTenantService(AppDbContext db, IUnitOfWork uow) =>
        new(
            uow,
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow)
        );

    private static DemoService BuildDemoService(IUnitOfWork uow) =>
        new(
            uow,
            new FakePasswordHasher(),
            new FakeTokenService(),
            new NoopEmailService(),
            new FakeSettings(),
            new NoopBrandingService(),
            new MembershipService(uow)
        );

    /// <summary>Seeds a live demo workspace with its admin identity + membership.</summary>
    private static (User Admin, Guid WorkspaceId) SeedDemoWorkspace(
        AppDbContext db,
        DateTime? demoExpiresAt = null,
        DateTime? demoExtendedAt = null,
        DateTime? demoConvertedAt = null
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

        var admin = new User
        {
            PublicId = pid,
            Email = $"demo-{pid.ToString("N")[..8]}@demo.pointer",
            PasswordHash = "hash:demo",
            DisplayName = "Demo User",
            RoleId = role.Id,
            Role = role,
            IsActive = true,
            IsDemo = true,
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
                DemoExpiresAt = demoExpiresAt ?? DateTime.UtcNow.AddHours(24),
                DemoExtendedAt = demoExtendedAt,
                DemoConvertedAt = demoConvertedAt,
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

    // ── TenantService.ExtendDemoAsync(Guid) — operator path ─────────────────────────────────

    [Fact]
    public async Task OperatorExtendDemoAsync_MovesExpiry_StampsExtendedAt()
    {
        var db = Ctx(nameof(OperatorExtendDemoAsync_MovesExpiry_StampsExtendedAt));
        var (_, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.ExtendDemoAsync(workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.NotNull(workspace.DemoExtendedAt);
        Assert.True(workspace.DemoExpiresAt > DateTime.UtcNow.AddHours(23));
    }

    [Fact]
    public async Task OperatorExtendDemoAsync_NotFound_WhenNotADemo()
    {
        var db = Ctx(nameof(OperatorExtendDemoAsync_NotFound_WhenNotADemo));
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Real Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        db.SaveChanges();
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.ExtendDemoAsync(workspaceId);

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task OperatorExtendDemoAsync_Twice_Fails_AlreadyExtended()
    {
        var db = Ctx(nameof(OperatorExtendDemoAsync_Twice_Fails_AlreadyExtended));
        var (_, workspaceId) = SeedDemoWorkspace(db, demoExtendedAt: DateTime.UtcNow);
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.ExtendDemoAsync(workspaceId);

        Assert.False(result.IsSuccess);
    }

    /// <summary>DB-17 review finding #6 (LOW): a converted workspace (even one still holding a
    /// non-null DemoExpiresAt for the Demo:ConvertRequiresVerification grace) is not "still a demo"
    /// for extension — it is already upgraded.</summary>
    [Fact]
    public async Task OperatorExtendDemoAsync_ConvertedWorkspace_Fails_AlreadyUpgraded()
    {
        var db = Ctx(nameof(OperatorExtendDemoAsync_ConvertedWorkspace_Fails_AlreadyUpgraded));
        var (_, workspaceId) = SeedDemoWorkspace(
            db,
            // The Demo:ConvertRequiresVerification grace state: converted AND still non-null.
            demoExpiresAt: DateTime.UtcNow.AddHours(48),
            demoConvertedAt: DateTime.UtcNow
        );
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.ExtendDemoAsync(workspaceId);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.AlreadyUpgraded, result.Message);
    }

    /// <summary>DB-17 review finding #5: proves the operator and self-service extend paths share
    /// the SAME one-extension-total flag (workspaces.demo_extended_at) end to end with the real services.</summary>
    [Fact]
    public async Task OperatorExtend_ThenSelfExtend_Fails()
    {
        var db = Ctx(nameof(OperatorExtend_ThenSelfExtend_Fails));
        var (admin, workspaceId) = SeedDemoWorkspace(db);
        var uow = new UnitOfWork(db);
        var tenantService = BuildTenantService(db, uow);
        var demoService = BuildDemoService(uow);

        var operatorResult = await tenantService.ExtendDemoAsync(workspaceId);
        Assert.True(operatorResult.IsSuccess, operatorResult.Message);

        var selfResult = await demoService.ExtendAsync(admin.PublicId, workspaceId);

        Assert.False(selfResult.IsSuccess);
        Assert.Equal(MessageKeys.Demo.AlreadyExtended, selfResult.Message);
    }

    // ── TenantService.SetDemoConfigAsync(Guid) — operator path ──────────────────────────────

    [Fact]
    public async Task OperatorSetDemoConfigAsync_SetsOverrides()
    {
        var db = Ctx(nameof(OperatorSetDemoConfigAsync_SetsOverrides));
        var (_, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.SetDemoConfigAsync(workspaceId, commentCapOverride: 5, ttlHoursOverride: 48);

        Assert.True(result.IsSuccess, result.Message);
        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.Equal(5, workspace.DemoCommentCapOverride);
        Assert.Equal(48, workspace.DemoTtlHoursOverride);
    }

    [Fact]
    public async Task OperatorSetDemoConfigAsync_ClearsOverrides_WhenNull()
    {
        var db = Ctx(nameof(OperatorSetDemoConfigAsync_ClearsOverrides_WhenNull));
        var workspaceId = Guid.NewGuid();
        var pid = Guid.NewGuid();
        var role = new Role { Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        db.SaveChanges();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = DateTime.UtcNow.AddHours(24),
                DemoCommentCapOverride = 5,
                DemoTtlHoursOverride = 48,
            }
        );
        db.SaveChanges();
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.SetDemoConfigAsync(workspaceId, commentCapOverride: null, ttlHoursOverride: null);

        Assert.True(result.IsSuccess, result.Message);
        var workspace = db.Workspaces.Single(w => w.Id == workspaceId);
        Assert.Null(workspace.DemoCommentCapOverride);
        Assert.Null(workspace.DemoTtlHoursOverride);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task OperatorSetDemoConfigAsync_RejectsNonPositiveCommentCap(int badCap)
    {
        var db = Ctx(nameof(OperatorSetDemoConfigAsync_RejectsNonPositiveCommentCap) + badCap);
        var (_, workspaceId) = SeedDemoWorkspace(db);
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.SetDemoConfigAsync(workspaceId, commentCapOverride: badCap, ttlHoursOverride: null);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task OperatorSetDemoConfigAsync_NotFound_WhenNotADemo()
    {
        var db = Ctx(nameof(OperatorSetDemoConfigAsync_NotFound_WhenNotADemo));
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Real Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        db.SaveChanges();
        var svc = BuildTenantService(db, new UnitOfWork(db));

        var result = await svc.SetDemoConfigAsync(workspaceId, commentCapOverride: 5, ttlHoursOverride: null);

        Assert.True(result.IsNotFound);
    }
}
