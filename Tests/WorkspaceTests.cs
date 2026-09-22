using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Tenant;
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
/// DB-03: the workspaces table, its FK on every owner_id, and the workspace-name-is-its-own-attribute
/// invariant (Q3) — never derived from a users row.
/// </summary>
public class WorkspaceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;

        public bool Verify(string password, string hash) => hash == "hashed:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");
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
    }

    /// <summary>
    /// Sqlite shared-cache fixture (enforces FKs and unique indexes, unlike InMemory). Copied from
    /// Tests/UsageEventFirstCommentTests.cs:43-60.
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

    private static AppDbContext InMemoryContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    // ── 1. Project_WithUnknownOwner_IsRejectedByForeignKey ──────────────────────────────

    [Fact]
    public async Task Project_WithUnknownOwner_IsRejectedByForeignKey()
    {
        using var db = new TestDb();
        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = db.MakeContext(admin);

        ctx.Projects.Add(
            new Project
            {
                Key = "orphan",
                Name = "Orphan",
                OwnerId = Guid.NewGuid(),
            }
        );

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ── 2. TenantService_CreateAsync_MintsWorkspaceRow_WithPlaceholderName ──────────────

    [Fact]
    public async Task TenantService_CreateAsync_MintsWorkspaceRow_WithPlaceholderName()
    {
        var dbName = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = InMemoryContext(superAdmin, dbName))
        {
            seed.Roles.Add(
                new Role
                {
                    Name = "Workspace Admin",
                    GrantsAdmin = true,
                    IsActive = true,
                    IsSystem = true,
                }
            );
            seed.SaveChanges();
        }

        using var db = InMemoryContext(superAdmin, dbName);
        var svc = new TenantService(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider()
        );

        var result = await svc.CreateAsync(
            new CreateTenantRequest
            {
                Email = "jane@acme.com",
                Password = "password123",
                DisplayName = "Jane Doe",
            }
        );

        Assert.True(result.IsSuccess, result.Message);
        var workspace = db
            .Workspaces.IgnoreQueryFilters()
            .Single(w => w.Id == result.Data!.OwnerId);
        Assert.Equal(Workspace.PlaceholderName, workspace.Name);
        Assert.NotEqual("Jane Doe", workspace.Name);
    }

    // ── 3. InviteAccept_NewWorkspace_UsesInviteDisplayName ──────────────────────────────

    [Fact]
    public async Task InviteAccept_NewWorkspace_UsesInviteDisplayName()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = InMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Roles.Add(
                new Role
                {
                    Name = "Workspace Admin",
                    GrantsAdmin = true,
                    IsActive = true,
                    IsSystem = true,
                }
            );
            seed.Invites.Add(
                new Invite
                {
                    OwnerId = null,
                    Code = "invite-code-1",
                    ExpiresAt = DateTime.UtcNow.AddDays(7),
                    Uses = 0,
                    DisplayName = "Acme Inc",
                }
            );
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();
        using var db = InMemoryContext(anon, dbName);
        var svc = new InviteService(
            new UnitOfWork(db),
            anon,
            new FakePasswordHasher(),
            new FakeTokenService(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            new NoopEmail(),
            new FakeBrandingService()
        );

        var result = await svc.AcceptAsync(
            new AcceptInviteRequest
            {
                Code = "invite-code-1",
                Email = "founder@newco.com",
                Password = "password123",
                DisplayName = "Jane Doe",
            }
        );

        Assert.True(result.IsSuccess, result.Message);
        var newUser = db.Users.IgnoreQueryFilters().Single(u => u.Email == "founder@newco.com");
        var workspace = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == newUser.OwnerId);
        Assert.Equal("Acme Inc", workspace.Name);
    }

    // ── 4. HardDeleteOrder_CoversEveryOwnerCarryingEntity (R8.5 reflection enforcer) ────

    [Fact]
    public void HardDeleteOrder_CoversEveryOwnerCarryingEntity()
    {
        var ownerCarrying = typeof(BaseEntity)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsClass
                && !t.IsAbstract
                && t.GetProperty("OwnerId") != null
                && t != typeof(Workspace)
                && t != typeof(UsageEvent)
            )
            .ToList();

        var missing = ownerCarrying.Where(t => !TenantService.HardDeleteOrder.Contains(t)).ToList();
        Assert.True(
            missing.Count == 0,
            "Type(s) not covered by TenantService.HardDeleteOrder: "
                + string.Join(", ", missing.Select(t => t.Name))
        );

        Assert.Equal(22, TenantService.HardDeleteOrder.Length);
    }

    // ── 5. HardDelete_RemovesEverything_EvenWithSuggestionNotification ──────────────────

    [Fact]
    public async Task HardDelete_RemovesEverything_EvenWithSuggestionNotification()
    {
        using var db = new TestDb();
        var ownerPublicId = Guid.NewGuid();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };

        int projectId,
            suggestionId,
            planId;
        using (var seed = db.MakeContext(superAdmin))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = ownerPublicId,
                    Name = "Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = ownerPublicId,
                }
            );

            var role = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                IsSystem = true,
                OwnerId = ownerPublicId,
            };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            var admin = new User
            {
                PublicId = ownerPublicId,
                Email = "admin@delete-me.com",
                PasswordHash = "hash",
                DisplayName = "Admin",
                RoleId = role.Id,
                OwnerId = ownerPublicId,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(admin);

            var project = new Project
            {
                Key = "to-delete",
                Name = "To Delete",
                OwnerId = ownerPublicId,
            };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;

            var comment = new Comment
            {
                ProjectId = projectId,
                OwnerId = ownerPublicId,
                AuthorId = ownerPublicId,
                Environment = EnvironmentTag.Production,
                Status = CommentStatus.Open,
                Body = "delete me",
            };
            seed.Comments.Add(comment);

            var suggestion = new PredefinedActionSuggestion
            {
                OwnerId = ownerPublicId,
                ProjectId = projectId,
                Text = "Add a button",
                Prompt = "Add a button",
            };
            seed.PredefinedActionSuggestions.Add(suggestion);
            await seed.SaveChangesAsync();
            suggestionId = suggestion.Id;

            seed.Notifications.Add(
                new Notification
                {
                    OwnerId = ownerPublicId,
                    UserId = ownerPublicId,
                    Type = NotificationType.SuggestionSubmitted,
                    CommentId = null,
                    ProjectId = projectId,
                    SuggestionId = suggestionId,
                }
            );

            var plan = new Plan
            {
                Name = "Free",
                Slug = "free-" + Guid.NewGuid().ToString("N")[..8],
            };
            seed.Plans.Add(plan);
            await seed.SaveChangesAsync();
            planId = plan.Id;

            seed.Subscriptions.Add(new Subscription { OwnerId = ownerPublicId, PlanId = planId });
            seed.WorkspaceSettings.Add(new WorkspaceSetting { OwnerId = ownerPublicId });

            await seed.SaveChangesAsync();
        }

        using var ctx = db.MakeContext(superAdmin);
        var svc = new TenantService(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider()
        );

        var result = await svc.HardDeleteAsync(ownerPublicId);
        Assert.True(result.IsSuccess, result.Message);

        using var verify = db.MakeContext(superAdmin);
        foreach (var t in TenantService.HardDeleteOrder)
        {
            var set = (System.Collections.IEnumerable)
                typeof(DbContext)
                    .GetMethod(nameof(DbContext.Set), Type.EmptyTypes)!
                    .MakeGenericMethod(t)
                    .Invoke(verify, null)!;
            var queryable = ((IQueryable<object>)set).IgnoreQueryFilters();
            var rows = queryable.ToList(); // materialize before reflecting over OwnerId
            var prop = t.GetProperty("OwnerId")!;
            var count = rows.Count(x =>
            {
                var value = prop.GetValue(x);
                return value is Guid g ? g == ownerPublicId : (Guid?)value == ownerPublicId;
            });
            Assert.True(count == 0, $"{t.Name} still has row(s) owned by the deleted tenant");
        }

        Assert.Equal(0, verify.Workspaces.IgnoreQueryFilters().Count(w => w.Id == ownerPublicId));
    }

    // ── 6. Workspace_TenantB_CannotReadTenantA ──────────────────────────────────────────

    [Fact]
    public void Workspace_TenantB_CannotReadTenantA()
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

        using var db = InMemoryContext(
            new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantB },
            dbName
        );
        var visible = db.Workspaces.ToList();

        var only = Assert.Single(visible);
        Assert.Equal(tenantB, only.Id);
    }

    // ── 7. Me_TenantName_ComesFromWorkspaceRow_NotAdmin ─────────────────────────────────

    [Fact]
    public async Task Me_TenantName_ComesFromWorkspaceRow_NotAdmin()
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
            seed.Users.Add(
                new User
                {
                    PublicId = adminPublicId,
                    Email = "admin@acme.com",
                    PasswordHash = "hash",
                    DisplayName = "Jane Doe",
                    RoleId = role.Id,
                    OwnerId = tenant,
                    ApprovalStatus = ApprovalStatus.Approved,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = adminPublicId, TenantId = tenant };
        using var db = InMemoryContext(caller, dbName);
        var svc = new AuthService(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new FakeTokenService(),
            caller,
            new FakeSettings(),
            new FakeResetTokens(),
            new NoopEmail(),
            new FakeBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter()
        );

        var result = await svc.MeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("Acme Inc", result.Data!.TenantName);
    }
}
