using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Regression guard for the bug fixed alongside this test: <c>TenantService.CreateAsync</c> and
/// <c>AuthService.RegisterAdminAsync</c> both mint a brand-new workspace id and then create the
/// admin identity with <c>users.owner_id</c> set to that id. <c>users.owner_id</c> is a real FK to
/// <c>workspaces.id</c> (<c>fk_users_workspaces_owner_id</c>, see
/// <c>Infrastructure/Mappings/UserMapping.cs</c>) — if the identity is added AND SAVED in its own
/// <c>SaveChangesAsync()</c> call before the <c>Workspace</c> row exists, Postgres rejects the
/// insert with 23503.
///
/// EF Core's InMemory provider does not enforce foreign keys, so a bug like this passes silently
/// against the InMemory fixture the rest of the suite uses (see WorkspaceTests.cs's
/// <c>TenantService_CreateAsync_MintsWorkspaceRow_WithPlaceholderName</c>, which never caught it).
/// This file uses the same Sqlite shared-cache fixture as WorkspaceTests.cs's
/// <c>Project_WithUnknownOwner_IsRejectedByForeignKey</c> — Sqlite (unlike InMemory) enforces FK
/// constraints, so a re-introduction of the ordering bug fails these tests with a
/// <see cref="DbUpdateException"/> instead of passing silently.
/// </summary>
public class WorkspaceBeforeIdentityOrderingTests
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
    }

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class FakeToken : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) => "t";
        public string IssueSelection(User user) => "sel";
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
        public Task<bool> SendAsync(string to, string subject, string html, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) =>
            Task.FromResult("");
        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse Response() =>
            new() { ProductName = "Pointer" };

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(Response())
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(Response())
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Response());
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(key == ISettingsService.ScopedAdminSignupEnabled ? true : fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    /// <summary>
    /// Sqlite shared-cache fixture (enforces FKs and unique indexes, unlike InMemory). Copied from
    /// Tests/WorkspaceTests.cs's identically-named fixture.
    /// </summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static void SeedWorkspaceAdminRole(TestDb db)
    {
        using var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        seed.Roles.Add(
            new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsSystem = true,
                IsActive = true,
            }
        );
        seed.SaveChanges();
    }

    // ── TenantService.CreateAsync ───────────────────────────────────────────────

    [Fact]
    public async Task TenantService_CreateAsync_SavesWorkspaceBeforeIdentity_UnderRealForeignKeys()
    {
        using var db = new TestDb();
        SeedWorkspaceAdminRole(db);

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = db.MakeContext(superAdmin);
        var uow = new UnitOfWork(ctx);
        var svc = new TenantService(
            uow,
            new IdentityHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(uow)
        );

        // Before the fix: 23503 (fk_users_workspaces_owner_id) — the identity's SaveChangesAsync()
        // ran before the Workspace row existed. Sqlite enforces the same FK, so a regression here
        // throws a DbUpdateException instead of silently succeeding (as it would under InMemory).
        var result = await svc.CreateAsync(
            new CreateTenantRequest
            {
                Email = "new-tenant@example.com",
                Password = "password123",
                DisplayName = "New Tenant",
            }
        );

        Assert.True(result.IsSuccess, result.Message);

        var workspace = ctx.Workspaces.IgnoreQueryFilters().Single(w => w.Id == result.Data!.OwnerId);
        var user = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "new-tenant@example.com");
        Assert.Equal(workspace.Id, user.OwnerId);
    }

    // ── AuthService.RegisterAdminAsync (self-serve admin signup) ────────────────

    [Fact]
    public async Task AuthService_RegisterAdminAsync_SavesWorkspaceBeforeIdentity_UnderRealForeignKeys()
    {
        using var db = new TestDb();
        SeedWorkspaceAdminRole(db);

        var anon = new FakeCurrentUser();
        using var ctx = db.MakeContext(anon);
        var uow = new UnitOfWork(ctx);
        var auth = new AuthService(
            uow,
            new IdentityHasher(),
            new FakeToken(),
            anon,
            new FakeSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );

        // Before the fix: 23503 (fk_users_workspaces_owner_id) — same ordering bug as TenantService.
        var result = await auth.RegisterAdminAsync(
            new RegisterAdminRequest
            {
                Email = "self-signup@example.com",
                Password = "password123",
                DisplayName = "Self Signup",
            }
        );

        Assert.True(result.IsSuccess, result.Message);

        var user = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "self-signup@example.com");
        var workspace = ctx.Workspaces.IgnoreQueryFilters().Single(w => w.Id == user.OwnerId);
        Assert.Equal(user.OwnerId, workspace.Id);
    }
}
