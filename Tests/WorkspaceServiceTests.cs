using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Workspace;
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
/// DB-03b: GET/PUT of the caller's own workspace name — placeholder flag, validation, the
/// super-admin/quick-access guards mirrored from CommentFieldService, tenant isolation (R8), and
/// the two downstream surfaces that consume the row (AuthService.MeAsync's TenantName and
/// TenantService.ListAsync's WorkspaceName). Fixture copied from CommentFieldsTests.cs (in-memory
/// AppDbContext + FakeCurrentUser).
/// </summary>
public class WorkspaceServiceTests
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

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string password, string hash) => hash == "hashed:" + password;
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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class FakeBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse Response() =>
            new() { ProductName = "Pointer" };

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    Response()
                )
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    Response()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Response());
    }

    private sealed class FakeResetTokens : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "reset";

        public bool TryValidate(string token, out Guid id, out Guid stamp)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            return false;
        }

        public string CreateScoped(Guid id, Guid stamp, string purpose, string? payload = null) =>
            "r";

        public bool TryValidateScoped(
            string token,
            string purpose,
            out Guid id,
            out Guid stamp,
            out string? payload
        )
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            payload = null;
            return false;
        }
    }

    private static AppDbContext InMemoryContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static void SeedWorkspace(string dbName, Guid id, string name)
    {
        using var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        seed.Workspaces.Add(
            new Workspace
            {
                Id = id,
                Name = name,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = id,
            }
        );
        seed.SaveChanges();
    }

    // ── 1. Get_ReturnsPlaceholderFlag_UntilRenamed ──────────────────────────────────────

    [Fact]
    public async Task Get_ReturnsPlaceholderFlag_UntilRenamed()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, Workspace.PlaceholderName);

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };

        using (var db = InMemoryContext(admin, dbName))
        {
            var svc = new WorkspaceService(new UnitOfWork(db), admin);
            var before = await svc.GetAsync();
            Assert.True(before.IsSuccess, before.Message);
            Assert.True(before.Data!.IsPlaceholderName);
            Assert.Null(before.Data!.UpdatedAt);

            var renamed = await svc.RenameAsync(
                new UpdateWorkspaceNameRequest { Name = "Acme Inc" }
            );
            Assert.True(renamed.IsSuccess, renamed.Message);
            Assert.Equal("Acme Inc", renamed.Data!.Name);
            Assert.False(renamed.Data!.IsPlaceholderName);
            Assert.NotNull(renamed.Data!.UpdatedAt);
        }

        using (var db = InMemoryContext(admin, dbName))
        {
            var svc = new WorkspaceService(new UnitOfWork(db), admin);
            var after = await svc.GetAsync();
            Assert.Equal("Acme Inc", after.Data!.Name);
            Assert.False(after.Data!.IsPlaceholderName);
        }
    }

    // ── 2. Rename_Blank_Or_TooLong_Or_ControlChars_Rejected ─────────────────────────────

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Rename_Blank_Rejected(string name)
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        using var db = InMemoryContext(admin, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), admin);

        var result = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = name });

        Assert.False(result.IsSuccess);
        Assert.Equal("Original", db.Workspaces.Single(w => w.Id == tenant).Name);
    }

    [Fact]
    public async Task Rename_TooLong_Rejected()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        using var db = InMemoryContext(admin, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), admin);

        var result = await svc.RenameAsync(
            new UpdateWorkspaceNameRequest { Name = new string('a', 121) }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal("Original", db.Workspaces.Single(w => w.Id == tenant).Name);
    }

    [Fact]
    public async Task Rename_ControlChars_Rejected()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        using var db = InMemoryContext(admin, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), admin);

        var result = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = "AcmeInc" });

        Assert.False(result.IsSuccess);
        Assert.Equal("Original", db.Workspaces.Single(w => w.Id == tenant).Name);
    }

    [Fact]
    public async Task Rename_Valid_Accepted()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        using var db = InMemoryContext(admin, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), admin);

        var result = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = "Acme" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("Acme", db.Workspaces.Single(w => w.Id == tenant).Name);
    }

    // ── 3. Rename_SuperAdmin_And_QuickAccess_Forbidden ──────────────────────────────────

    [Fact]
    public async Task Rename_SuperAdmin_Forbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var db = InMemoryContext(superAdmin, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), superAdmin);

        var getResult = await svc.GetAsync();
        Assert.True(getResult.IsForbidden);

        var renameResult = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = "Acme" });
        Assert.True(renameResult.IsForbidden);
    }

    [Fact]
    public async Task Rename_QuickAccess_Forbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        SeedWorkspace(dbName, tenant, "Original");

        var quickAccess = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            IsQuickAccess = true,
        };
        using var db = InMemoryContext(quickAccess, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), quickAccess);

        var getResult = await svc.GetAsync();
        Assert.True(getResult.IsForbidden);

        var renameResult = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = "Acme" });
        Assert.True(renameResult.IsForbidden);
    }

    // ── 4. Rename_TenantB_CannotSeeOrRenameTenantA ──────────────────────────────────────

    [Fact]
    public async Task Rename_TenantB_CannotSeeOrRenameTenantA()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantA,
                    Name = "Workspace A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantA,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantB,
                    Name = "Workspace B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantB,
                }
            );
            seed.SaveChanges();
        }

        var userB = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantB };

        using (var db = InMemoryContext(userB, dbName))
        {
            var svc = new WorkspaceService(new UnitOfWork(db), userB);

            var getResult = await svc.GetAsync();
            Assert.True(getResult.IsSuccess, getResult.Message);
            Assert.Equal(tenantB, getResult.Data!.Id);
            Assert.Equal("Workspace B", getResult.Data!.Name);

            var renameResult = await svc.RenameAsync(
                new UpdateWorkspaceNameRequest { Name = "Renamed B" }
            );
            Assert.True(renameResult.IsSuccess, renameResult.Message);
        }

        using (var verify = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            Assert.Equal("Workspace A", verify.Workspaces.Single(w => w.Id == tenantA).Name);
            Assert.Equal("Renamed B", verify.Workspaces.Single(w => w.Id == tenantB).Name);
        }
    }

    /// <summary>DB-18 §3.4 R8 tenancy: GetAsync reads through the plain tenant query filter (never
    /// IgnoreQueryFilters) keyed off the CALLER's own TenantId — a tenant-B admin's response must
    /// carry only B's own pause/deletion state, never A's, even though A is paused and mid-grace.</summary>
    [Fact]
    public async Task WorkspaceResponse_TenantB_SeesNoStateOfA()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantA,
                    Name = "Workspace A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantA,
                    PausedAt = DateTime.UtcNow,
                    PausedBy = Guid.NewGuid(),
                    DeletionRequestedAt = DateTime.UtcNow,
                    DeletionScheduledFor = DateTime.UtcNow.AddDays(7),
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenantB,
                    Name = "Workspace B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenantB,
                }
            );
            seed.SaveChanges();
        }

        var userB = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantB };
        using var db = InMemoryContext(userB, dbName);
        var svc = new WorkspaceService(new UnitOfWork(db), userB);

        var result = await svc.GetAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(tenantB, result.Data!.Id);
        Assert.Equal("Workspace B", result.Data.Name);
        Assert.Null(result.Data.PausedAt);
        Assert.False(result.Data.PausedByOperator);
        Assert.Null(result.Data.DeletionRequestedAt);
        Assert.Null(result.Data.DeletionScheduledFor);
    }

    // ── 5. MeResponse_TenantName_FollowsRename ──────────────────────────────────────────

    [Fact]
    public async Task MeResponse_TenantName_FollowsRename()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var adminPublicId = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = Workspace.PlaceholderName,
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var role = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                IsSystem = true,
                OwnerId = tenant,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var adminUser = new User
            {
                PublicId = adminPublicId,
                Email = "admin@acme.com",
                PasswordHash = "hash",
                DisplayName = "Jane Doe",
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(adminUser);
            seed.SaveChanges();
            TestSeed.Join(seed, adminUser, tenant, role);
        }

        var caller = new FakeCurrentUser { Id = adminPublicId, TenantId = tenant };

        using (var db = InMemoryContext(caller, dbName))
        {
            var workspaceSvc = new WorkspaceService(new UnitOfWork(db), caller);
            var renamed = await workspaceSvc.RenameAsync(
                new UpdateWorkspaceNameRequest { Name = "Acme Inc" }
            );
            Assert.True(renamed.IsSuccess, renamed.Message);
        }

        using (var db = InMemoryContext(caller, dbName))
        {
            var authSvc = new AuthService(
                new UnitOfWork(db),
                new FakePasswordHasher(),
                new FakeTokenService(),
                caller,
                new FakeSettings(),
                new FakeResetTokens(),
                new NoopEmail(),
                new FakeBrandingService(),
                new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
                new FakeLoginAttemptLimiter(),
                new MembershipService(new UnitOfWork(db))
            );

            var me = await authSvc.MeAsync();
            Assert.True(me.IsSuccess, me.Message);
            Assert.Equal("Acme Inc", me.Data!.TenantName);
        }
    }

    // ── 6. TenantService_ListAsync_IncludesWorkspaceName ────────────────────────────────

    [Fact]
    public async Task TenantService_ListAsync_IncludesWorkspaceName()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var adminPublicId = Guid.NewGuid();

        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "Acme Inc",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var role = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                IsSystem = true,
                OwnerId = tenant,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var adminUser = new User
            {
                PublicId = adminPublicId,
                Email = "admin@acme.com",
                PasswordHash = "hash",
                DisplayName = "Jane Doe",
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(adminUser);
            seed.SaveChanges();
            TestSeed.Join(seed, adminUser, tenant, role);
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var db = InMemoryContext(superAdmin, dbName);
        var svc = new TenantService(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(db))
        );

        var result = await svc.ListAsync();

        Assert.True(result.IsSuccess, result.Message);
        var row = Assert.Single(result.Data!);
        Assert.Equal("Acme Inc", row.WorkspaceName);
        Assert.Equal("Jane Doe", row.DisplayName);
    }
}
