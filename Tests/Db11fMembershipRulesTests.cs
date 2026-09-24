using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pointer.API.Seed;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Audit;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11f Part A — the new membership-only delete rule (<see cref="TenantService.IdentitiesDeletedWithWorkspace"/>),
/// the read-only I1 pre-flight, home workspace, session role resolution and the D11f.6/D11f.8 guards.
/// </summary>
public class Db11fMembershipRulesTests
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

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
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

    private sealed class FakeResetTokenService : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";

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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class SpyEmailService : IEmailService
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

    /// <summary>Records DeleteOwnerFilesAsync calls — proves the I1 pre-flight refuses BEFORE this
    /// (ordinarily pre-transaction) side effect runs.</summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        public List<string> DeletedOwners { get; } = new();

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

    private static AppDbContext Ctx(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new ConfigurationBuilder().Build()
        );

    /// <summary>Sqlite, real FKs — same shape as WorkspaceTests.TestDb.</summary>
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
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static TenantService BuildTenantService(
        AppDbContext ctx,
        IFileStorage? fileStorage = null,
        IAuditWriter? audit = null
    ) =>
        new(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            fileStorage ?? new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(ctx)),
            audit
        );

    private static JwtTokenService RealTokenService() =>
        new(
            Options.Create(
                new JwtOptions
                {
                    SigningKey = new string('k', 40),
                    Issuer = "pointer-api",
                    LifetimeHours = 12,
                    SelectionLifetimeMinutes = 5,
                }
            )
        );

    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        SpyEmailService? email = null
    )
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new FakePasswordHasher(),
            RealTokenService(),
            user,
            new FakeSettings(),
            new FakeResetTokenService(),
            (IEmailService?)email ?? new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );
    }

    private static ProfileService BuildProfileService(ICurrentUser user, AppDbContext ctx) =>
        new(
            new UnitOfWork(ctx),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            user
        );

    private static PreferencesService BuildPreferencesService(
        ICurrentUser user,
        AppDbContext ctx
    ) => new(new UnitOfWork(ctx), user, new MembershipService(new UnitOfWork(ctx)));

    private static UserService BuildUserService(ICurrentUser user, AppDbContext ctx) =>
        new(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            user,
            new NoopEmail(),
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx))
        );

    private static InviteService BuildInviteService(
        ICurrentUser user,
        AppDbContext ctx,
        ISettingsService? settings = null,
        IUnitOfWork? unitOfWork = null
    ) =>
        new(
            unitOfWork ?? new UnitOfWork(ctx),
            user,
            new FakePasswordHasher(),
            RealTokenService(),
            settings ?? new FakeSettings(),
            new PassThroughEntitlements(),
            new NoopEmail(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx))
        );

    /// <summary>
    /// The EF Core InMemory provider never throws a 23505 duplicate-key violation on its own (no
    /// unique constraints are enforced) — same precedent as ChangeEmailTests.ThrowDuplicateKeyUnitOfWork.
    /// This variant throws only on the Nth <see cref="SaveChangesAsync"/> call, so the FIRST save in a
    /// multi-save flow (e.g. the quick-access invite's own insert) succeeds normally and only a LATER
    /// save (e.g. the identity+membership insert racing a concurrent duplicate) fails — simulating the
    /// exact TOCTOU window the comment at InviteService.cs's quick-access catch describes.
    /// </summary>
    private sealed class ThrowOnNthSaveUnitOfWork(IUnitOfWork inner, int throwOnCallNumber)
        : IUnitOfWork
    {
        private int _calls;

        public IRepository<T> Repository<T>()
            where T : BaseEntity => inner.Repository<T>();

        public DbSet<UsageEvent> UsageEvents => inner.UsageEvents;
        public DbSet<UsageDaily> UsageDaily => inner.UsageDaily;
        public DbSet<Workspace> Workspaces => inner.Workspaces;
        public DbSet<UserAlias> UserAliases => inner.UserAliases;
        public DbSet<AuditEvent> AuditEvents => inner.AuditEvents;
        public DbSet<ImpersonationSession> ImpersonationSessions => inner.ImpersonationSessions;

        public Task<int> SaveChangesAsync()
        {
            _calls++;
            if (_calls == throwOnCallNumber)
                throw new DbUpdateException(
                    "simulated 23505",
                    new PostgresException(
                        "duplicate key value violates unique constraint \"ux_users_email_owner_live\"",
                        "ERROR",
                        "ERROR",
                        "23505",
                        constraintName: "ux_users_email_owner_live"
                    )
                );
            return inner.SaveChangesAsync();
        }

        public Task ExecuteInTransactionAsync(Func<Task> action) =>
            inner.ExecuteInTransactionAsync(action);

        public void PreserveCreatedAtOnInsert(BaseEntity entity) =>
            inner.PreserveCreatedAtOnInsert(entity);

        public void ClearChangeTracker() => inner.ClearChangeTracker();

        public Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now) =>
            inner.AtomicClaimInviteSlotAsync(inviteId, now);

        public Task ExecuteSqlRawAsync(string sql, params object[] parameters) =>
            inner.ExecuteSqlRawAsync(sql, parameters);
    }

    private static DemoService BuildDemoService(AppDbContext ctx) =>
        new(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            RealTokenService(),
            new NoopEmail(),
            new FakeSettings(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx))
        );

    /// <summary>Task A15 / test 14c: records, per SaveChangesAsync call, the set of entity CLR types
    /// that were Added — same technique as DemoServiceAnalyticsFailureTests' interceptor, but via the
    /// plain DbContext.SavingChanges event (no interceptor registration needed).</summary>
    private static List<HashSet<Type>> TrackAddedTypesPerSave(AppDbContext ctx)
    {
        var saves = new List<HashSet<Type>>();
        ctx.SavingChanges += (o, _) =>
            saves.Add(
                ((DbContext)o!)
                    .ChangeTracker.Entries()
                    .Where(e => e.State == EntityState.Added)
                    .Select(e => e.Entity.GetType())
                    .ToHashSet()
            );
        return saves;
    }

    /// <summary>Task A15 (cross-review Opus MEDIUM): the FIRST save that inserts a User must also
    /// insert its first WorkspaceMembership in the SAME SaveChanges call — never a later one, which
    /// would leave a membership-less identity if the process crashed in between (I1).</summary>
    private static void AssertUserAndMembershipSavedTogether(List<HashSet<Type>> saves)
    {
        var firstWithUser = saves.FirstOrDefault(s => s.Contains(typeof(User)));
        Assert.NotNull(firstWithUser);
        Assert.Contains(typeof(WorkspaceMembership), firstWithUser!);
    }

    // ── 14c. Creators_SaveIdentityAndFirstMembership_InOneSaveChanges (task A15) ────────────

    private sealed class AdminSignupEnabledSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(key == ISettingsService.ScopedAdminSignupEnabled || fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private const string StrongPassword = "Str0ng!Passw0rd99";

    [Fact]
    public async Task Creators_SaveIdentityAndFirstMembership_InOneSaveChanges()
    {
        // 1. TenantService.CreateAsync (builder: WorkspaceTests.cs:601-608).
        {
            var dbName = Guid.NewGuid().ToString();
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Roles.Add(
                    new Role
                    {
                        Name = "Workspace Admin",
                        GrantsAdmin = true,
                        IsActive = true,
                    }
                );
                seed.SaveChanges();
            }

            using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildTenantService(ctx);

            var result = await svc.CreateAsync(
                new CreateTenantRequest
                {
                    Email = "new-tenant-admin@x.com",
                    Password = StrongPassword,
                    DisplayName = "New Tenant Admin",
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 2. UserService.CreateAsync (builder: WorkspaceAdminOwnershipTests.cs).
        {
            var dbName = Guid.NewGuid().ToString();
            var tenant = Guid.NewGuid();
            int devRoleId;
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = tenant,
                        Name = "T",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = tenant,
                    }
                );
                var devRole = new Role { Name = "Developer", IsActive = true };
                seed.Roles.Add(devRole);
                seed.SaveChanges();
                devRoleId = devRole.Id;
            }

            var caller = new FakeCurrentUser { TenantId = tenant, IsAdmin = true };
            using var ctx = Ctx(caller, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildUserService(caller, ctx);

            var result = await svc.CreateAsync(
                new CreateUserRequest
                {
                    Email = "new-member@x.com",
                    Password = StrongPassword,
                    DisplayName = "New Member",
                    RoleId = devRoleId,
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 3. InviteService accept — join an EXISTING workspace (builder: InviteServiceTests.cs BuildService).
        {
            var dbName = Guid.NewGuid().ToString();
            var tenant = Guid.NewGuid();
            string inviteCode;
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = tenant,
                        Name = "T",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = tenant,
                    }
                );
                var devRole = new Role { Name = "Developer", IsActive = true };
                seed.Roles.Add(devRole);
                seed.SaveChanges();

                inviteCode = Guid.NewGuid().ToString("N");
                seed.Set<Invite>()
                    .Add(
                        new Invite
                        {
                            OwnerId = tenant,
                            Code = inviteCode,
                            RoleId = devRole.Id,
                            ExpiresAt = DateTime.UtcNow.AddDays(7),
                            MaxUses = null,
                            Uses = 0,
                        }
                    );
                seed.SaveChanges();
            }

            var anon = new FakeCurrentUser();
            using var ctx = Ctx(anon, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildInviteService(anon, ctx);

            var result = await svc.AcceptAsync(
                new AcceptInviteRequest
                {
                    Code = inviteCode,
                    Email = "join-existing@x.com",
                    Password = StrongPassword,
                    DisplayName = "Join Existing",
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 4. InviteService accept — CREATE a new workspace (invite.OwnerId == null).
        {
            var dbName = Guid.NewGuid().ToString();
            string inviteCode;
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Roles.Add(
                    new Role
                    {
                        Name = "Workspace Admin",
                        GrantsAdmin = true,
                        IsActive = true,
                    }
                );
                seed.SaveChanges();

                inviteCode = Guid.NewGuid().ToString("N");
                seed.Set<Invite>()
                    .Add(
                        new Invite
                        {
                            OwnerId = null,
                            Code = inviteCode,
                            ExpiresAt = DateTime.UtcNow.AddDays(7),
                            MaxUses = 1,
                            Uses = 0,
                        }
                    );
                seed.SaveChanges();
            }

            var anon = new FakeCurrentUser();
            using var ctx = Ctx(anon, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildInviteService(anon, ctx);

            var result = await svc.AcceptAsync(
                new AcceptInviteRequest
                {
                    Code = inviteCode,
                    Email = "new-workspace@x.com",
                    Password = StrongPassword,
                    DisplayName = "New Workspace Owner",
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 5. InviteService quick-access provisioning (CreateAsync with a QuickAccess-pinned role).
        {
            var dbName = Guid.NewGuid().ToString();
            var tenant = Guid.NewGuid();
            int quickRoleId;
            int projectId;
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = tenant,
                        Name = "T",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = tenant,
                    }
                );
                var quickRole = new Role
                {
                    Name = "Client",
                    QuickAccess = true,
                    IsActive = true,
                    OwnerId = tenant,
                };
                seed.Roles.Add(quickRole);
                var project = new Project
                {
                    Key = "quick-access-app",
                    Name = "App",
                    AppUrl = "https://client.example.com",
                    OwnerId = tenant,
                };
                seed.Projects.Add(project);
                seed.SaveChanges();
                quickRoleId = quickRole.Id;
                projectId = project.Id;
            }

            var admin = new FakeCurrentUser { TenantId = tenant, IsAdmin = true };
            using var ctx = Ctx(admin, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildInviteService(admin, ctx);

            var result = await svc.CreateAsync(
                new CreateInviteRequest
                {
                    RoleId = quickRoleId,
                    Email = "quick-access@x.com",
                    ProjectId = projectId,
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 6. AuthService.RegisterAsync (stakeholder register).
        {
            var dbName = Guid.NewGuid().ToString();
            var tenant = Guid.NewGuid();
            int stakeholderRoleId;
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Workspaces.Add(
                    new Workspace
                    {
                        Id = tenant,
                        Name = "T",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = tenant,
                    }
                );
                var stakeholderRole = new Role
                {
                    Name = "Stakeholder",
                    IsActive = true,
                    OwnerId = tenant,
                };
                seed.Roles.Add(stakeholderRole);
                var project = new Project
                {
                    Key = "stakeholder-app",
                    Name = "App",
                    OwnerId = tenant,
                };
                seed.Projects.Add(project);
                seed.SaveChanges();
                stakeholderRoleId = stakeholderRole.Id;
            }

            var anon = new FakeCurrentUser();
            using var ctx = Ctx(anon, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildAuthService(anon, ctx);

            var result = await svc.RegisterAsync(
                new RegisterRequest
                {
                    Email = "stakeholder@x.com",
                    Password = StrongPassword,
                    DisplayName = "Stakeholder",
                    RoleId = stakeholderRoleId,
                    ProjectKey = "stakeholder-app",
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 7. AuthService.RegisterAdminAsync (self-service workspace signup).
        {
            var dbName = Guid.NewGuid().ToString();
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Roles.Add(
                    new Role
                    {
                        Name = "Workspace Admin",
                        GrantsAdmin = true,
                        IsActive = true,
                    }
                );
                seed.SaveChanges();
            }

            var anon = new FakeCurrentUser();
            using var ctx = Ctx(anon, dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildAuthService(anon, ctx);
            // RegisterAdminAsync needs ScopedAdminSignupEnabled=true — swap the settings double via
            // a fresh AuthService built the same way BuildAuthService does, but with that setting on.
            var uow = new UnitOfWork(ctx);
            var svcWithSignupEnabled = new AuthService(
                uow,
                new FakePasswordHasher(),
                RealTokenService(),
                anon,
                new AdminSignupEnabledSettings(),
                new FakeResetTokenService(),
                new NoopEmail(),
                new NoopBrandingService(),
                new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
                new FakeLoginAttemptLimiter(),
                new MembershipService(uow)
            );

            var result = await svcWithSignupEnabled.RegisterAdminAsync(
                new RegisterAdminRequest
                {
                    Email = "self-signup-admin@x.com",
                    Password = StrongPassword,
                    DisplayName = "Self Signup",
                }
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }

        // 8. DemoService.ProvisionAsync (builder: Db17DemoServiceTests.cs).
        {
            var dbName = Guid.NewGuid().ToString();
            using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            {
                seed.Roles.Add(
                    new Role
                    {
                        Name = "Workspace Admin",
                        GrantsAdmin = true,
                        IsActive = true,
                    }
                );
                seed.SaveChanges();
            }

            using var ctx = Ctx(new FakeCurrentUser(), dbName);
            var saves = TrackAddedTypesPerSave(ctx);
            var svc = BuildDemoService(ctx);

            var result = await svc.ProvisionAsync(
                "https://app.pointer.moamen.work",
                "demo-recipient@x.com"
            );

            Assert.True(result.IsSuccess, result.Message);
            AssertUserAndMembershipSavedTogether(saves);
        }
    }

    // ── F1 (task item 1/2): InviteService.CreateAsync's super-admin-to-existing-workspace path
    // must never mint an invite pinned to a role other than the REAL global Deputy role — even when
    // a foreign workspace's decoy role shares its exact name (the name-lookup fix, InviteService.cs
    // ~155-163: `&& r.OwnerId == null`).
    [Fact]
    public async Task InviteService_CreateAsync_SuperAdminBranch_NeverPinsAForeignDecoyDeputyRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var target = Guid.NewGuid();
        var decoyOwner = Guid.NewGuid();
        int realDeputyRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = target,
                    Name = "Target",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = target,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = decoyOwner,
                    Name = "Decoy",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = decoyOwner,
                }
            );
            // Decoy seeded FIRST (lower id) and sharing the exact global Deputy role's name, but
            // OWNED by an unrelated workspace — before the fix, a super-admin caller's Role query
            // (which bypasses the tenant filter) could resolve THIS one instead of the real global
            // role, because the lookup only matched on Name.
            var decoyDeputy = new Role
            {
                Name = "Workspace Admin Deputy",
                OwnerId = decoyOwner,
                GrantsAdmin = true,
                IsActive = true,
            };
            var realAdminRole = new Role
            {
                Name = "Workspace Admin",
                OwnerId = null,
                GrantsAdmin = true,
                IsActive = true,
            };
            var realDeputyRole = new Role
            {
                Name = "Workspace Admin Deputy",
                OwnerId = null,
                GrantsAdmin = true,
                IsActive = true,
            };
            seed.Roles.AddRange(decoyDeputy, realAdminRole, realDeputyRole);
            seed.SaveChanges();
            realDeputyRoleId = realDeputyRole.Id;

            var targetAdmin = new User
            {
                Email = "target-admin@x.com",
                PasswordHash = "h",
                DisplayName = "TargetAdmin",
                PublicId = Guid.NewGuid(),
                RoleId = realAdminRole.Id,
                IsActive = true,
            };
            seed.Users.Add(targetAdmin);
            seed.SaveChanges();
            TestSeed.Join(seed, targetAdmin, target, realAdminRole);
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = Ctx(superAdmin, dbName);
        var svc = BuildInviteService(superAdmin, ctx);

        var result = await svc.CreateAsync(new CreateInviteRequest { TargetOwnerId = target });

        Assert.True(result.IsSuccess, result.Message);
        var invite = ctx.Invites.IgnoreQueryFilters().Single(i => i.OwnerId == target);
        Assert.Equal(realDeputyRoleId, invite.RoleId);
    }

    // ── Quick-access race (task item 5): on the duplicate-email Conflict, the invite use saved
    // earlier is rolled back — no used invite is left behind with no member ever created.
    [Fact]
    public async Task QuickAccessInvite_DuplicateEmailRace_RollsBackTheOrphanedInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int quickRoleId;
        int projectId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var quickRole = new Role
            {
                Name = "Client",
                QuickAccess = true,
                IsActive = true,
                OwnerId = tenant,
            };
            seed.Roles.Add(quickRole);
            var project = new Project
            {
                Key = "race-app",
                Name = "App",
                AppUrl = "https://client.example.com",
                OwnerId = tenant,
            };
            seed.Projects.Add(project);
            seed.SaveChanges();
            quickRoleId = quickRole.Id;
            projectId = project.Id;
        }

        var admin = new FakeCurrentUser { TenantId = tenant, IsAdmin = true };
        using var ctx = Ctx(admin, dbName);
        // Call #1 (the invite's own insert) succeeds; call #2 (identity + membership) fails with the
        // same shape a real duplicate-email race produces (a concurrent request created the same
        // identity in the window between this request's "does it exist" check and its own insert).
        var throwingUow = new ThrowOnNthSaveUnitOfWork(new UnitOfWork(ctx), throwOnCallNumber: 2);
        var svc = BuildInviteService(admin, ctx, unitOfWork: throwingUow);

        var result = await svc.CreateAsync(
            new CreateInviteRequest
            {
                RoleId = quickRoleId,
                Email = "raced@x.com",
                ProjectId = projectId,
            }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.AccountExists, result.Message);

        // The invite the try-block saved BEFORE the race was discovered must be gone — not left
        // behind as a "used" invite with no member ever created.
        Assert.Empty(ctx.Invites.IgnoreQueryFilters().Where(i => i.Email == "raced@x.com"));
        Assert.Empty(ctx.Users.IgnoreQueryFilters().Where(u => u.Email == "raced@x.com"));
    }

    // ── 5. DeleteSet_IsMembershipOnly_EveryShape ────────────────────────────────────────────

    [Fact]
    public async Task DeleteSet_IsMembershipOnly_EveryShape()
    {
        var dbName = Guid.NewGuid().ToString();
        var w = Guid.NewGuid();
        var x = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = w,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = w,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = x,
                    Name = "X",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = x,
                }
            );
            var superRole = new Role
            {
                Name = "SA",
                IsSuperAdmin = true,
                IsActive = true,
            };
            var wRole = new Role
            {
                Name = "WRole",
                OwnerId = w,
                IsActive = true,
            };
            seed.Roles.AddRange(superRole, wRole);
            seed.SaveChanges();

            User Make(string email, int roleId, bool erased = false, int? mergedInto = null) =>
                new()
                {
                    Email = email,
                    PasswordHash = "h",
                    DisplayName = email,
                    PublicId = Guid.NewGuid(),
                    RoleId = roleId,
                    IsActive = !erased,
                    DeletedAt = erased ? DateTime.UtcNow : null,
                    ErasedAt = erased ? DateTime.UtcNow : null,
                    MergedIntoUserId = mergedInto,
                };

            var a = Make("a@x", wRole.Id);
            var b = Make("b@x", wRole.Id);
            var c = Make("c@x", wRole.Id);
            var d = Make("d@x", wRole.Id, erased: true);
            var e = Make("e@x", wRole.Id, erased: true);
            var f = Make("f@x", superRole.Id);
            var g = Make("g@x", superRole.Id);
            var j = Make("j@x", wRole.Id);
            seed.Users.AddRange(a, b, c, d, e, f, g, j);
            seed.SaveChanges();

            void Join(User u, Guid ws, bool live = true, DateTime? leftAt = null) =>
                seed.Set<WorkspaceMembership>()
                    .Add(
                        new WorkspaceMembership
                        {
                            UserId = u.Id,
                            OwnerId = ws,
                            RoleId = wRole.Id,
                            IsActive = live,
                            ApprovalStatus = ApprovalStatus.Approved,
                            JoinedAt = DateTime.UtcNow.AddDays(-2),
                            LeftAt = leftAt,
                            LeftReason = leftAt != null ? MembershipEndReason.Removed : null,
                            SecurityStamp = Guid.NewGuid(),
                        }
                    );

            Join(a, w); // (a) live W → in
            Join(b, w); // (b) live W ...
            Join(b, x, leftAt: DateTime.UtcNow.AddDays(-1)); // ... + ended X → out
            Join(c, x); // (c) live X ...
            Join(c, w); // ... + live W → out (belongs elsewhere too)
            Join(d, w, live: false, leftAt: DateTime.UtcNow.AddDays(-1)); // (d) erased, ended W → in
            Join(e, w, live: true); // (e) soft-deleted, pending/live W → in
            // (f) super admin, no membership → out
            Join(g, w); // (g) super admin with an anomalous live W membership → out
            // (j) owner W, no membership anywhere → out
            seed.SaveChanges();

            var h = Make("h@x", wRole.Id, erased: true, mergedInto: a.Id); // (h) merged tombstone of (a) → in
            var i = Make("i@x", wRole.Id, erased: true, mergedInto: c.Id); // (i) merged tombstone of (c) ...
            seed.Users.AddRange(h, i);
            seed.SaveChanges();
            seed.UserAliases.Add(
                new UserAlias
                {
                    AliasPublicId = i.PublicId,
                    UserId = c.Id,
                    SourceWorkspaceId = w,
                    MergedAt = DateTime.UtcNow,
                }
            ); // ... whose alias records W as its source → in
            seed.SaveChanges();
        }

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var uow = new UnitOfWork(verify);
        var emails = TenantService
            .IdentitiesDeletedWithWorkspace(uow, w)
            .Select(u => u.Email)
            .ToList();

        Assert.Equal(new[] { "a@x", "d@x", "e@x", "h@x", "i@x" }, emails.OrderBy(e => e).ToArray());
    }

    // ── 6. HardDelete_MembershipLessIdentity_Survives_AndDoesNotBlock ──────────────────────
    // DB-11f Part B: tests 6 (DeleteSet_EqualsLegacyRule_WhenInvariantI1Holds), 8
    // (HardDelete_MembershipLessIdentityReferencingWorkspace_RefusedBeforeAnySideEffect) and 8b
    // (HardDelete_NoLeastPrivilegeGlobalRole_RefusedBeforeAnySideEffect) are deleted here (doc §6
    // item 16): all three read users.owner_id or exercise the Part A-only re-point/pre-flight code
    // TenantService B6 removes. Test 7 (HardDelete_RehomedIdentityWithTenantRole_…) was ALSO
    // deleted in the first Part B pass, beyond what §6 item 16 authorized — the code review (Gemini
    // MEDIUM) caught this: it is the only test exercising a real-FK hard delete of a workspace-owned
    // Role alongside a surviving multi-workspace identity. Re-added below, rewritten for Part B (no
    // more users.role_id tenant role to re-point — RoleId stays null throughout). See §15 for the
    // adjudication record.

    [Fact]
    public async Task HardDelete_MembershipLessIdentity_Survives_AndDoesNotBlock()
    {
        using var db = new TestDb();
        var w = Guid.NewGuid();
        int projectId;
        int adminUserId,
            orphanUserId;

        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = w,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = w,
                }
            );
            var role = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = w,
            };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            var admin = new User
            {
                Email = "admin@w.com",
                PasswordHash = "h",
                DisplayName = "Admin",
                PublicId = Guid.NewGuid(),
                IsActive = true,
            };
            seed.Users.Add(admin);
            await seed.SaveChangesAsync();
            adminUserId = admin.Id;
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = admin.Id,
                        OwnerId = w,
                        RoleId = role.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow,
                        SecurityStamp = Guid.NewGuid(),
                    }
                );

            // Shape (j): no membership anywhere and no users.owner_id column any more — Part B makes
            // this shape survive every hard delete instead of blocking it (D11f.4 §3.7 Part B).
            var orphan = new User
            {
                Email = "orphan@w.com",
                PasswordHash = "h",
                DisplayName = "Orphan",
                PublicId = Guid.NewGuid(),
                IsActive = true,
            };
            seed.Users.Add(orphan);
            await seed.SaveChangesAsync();
            orphanUserId = orphan.Id;

            var project = new Project
            {
                Key = "p",
                Name = "P",
                OwnerId = w,
            };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;
        }

        var fileStorage = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        using var ctx = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var svc = BuildTenantService(ctx, fileStorage, audit);

        var result = await svc.HardDeleteAsync(w, "admin");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Contains(w.ToString("N"), fileStorage.DeletedOwners);

        using var verify = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        Assert.Null(verify.Workspaces.IgnoreQueryFilters().SingleOrDefault(x => x.Id == w));
        Assert.Null(verify.Projects.IgnoreQueryFilters().SingleOrDefault(p => p.Id == projectId));
        // The admin belonged only to W (no membership elsewhere) — deleted with the workspace.
        Assert.Null(verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == adminUserId));
        // The membership-less orphan belongs nowhere, so it is not in the delete set — it survives.
        Assert.NotNull(
            verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == orphanUserId)
        );
    }

    // ── 7. HardDelete_RehomedIdentityWithWOwnedRoleMembership_Succeeds_UnderRealForeignKeys ─
    // Re-added per the Part B code review (Gemini MEDIUM): the doc's §6 item 16 named only tests
    // 6, 8 and 8b for deletion, but this one — the only test in the suite that hard-deletes a
    // workspace-owned Role under REAL Sqlite foreign keys while a surviving multi-workspace
    // identity keeps a membership elsewhere — was deleted too. Rewritten for Part B: `users.role_id`
    // no longer carries a tenant role at all (it is nullable and platform-role-only, D11f.2), so the
    // identity is seeded with RoleId = null throughout and its tenant role lives only on the
    // WorkspaceMembership rows. This is a deviation from §6 item 16's list, recorded in §15.

    [Fact]
    public async Task HardDelete_RehomedIdentityWithWOwnedRoleMembership_Succeeds_UnderRealForeignKeys()
    {
        using var db = new TestDb();
        var w = Guid.NewGuid();
        var x = Guid.NewGuid();
        int identityId;
        int wRoleId;
        int globalRoleId;

        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = w,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = w,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = x,
                    Name = "X",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = x,
                }
            );
            var wRole = new Role
            {
                Name = "WOwnedRole",
                OwnerId = w,
                IsActive = true,
            };
            var globalRole = new Role { Name = "Developer", IsActive = true };
            seed.Roles.AddRange(wRole, globalRole);
            await seed.SaveChangesAsync();
            wRoleId = wRole.Id;
            globalRoleId = globalRole.Id;

            var identity = new User
            {
                Email = "rehome@w.com",
                PasswordHash = "h",
                DisplayName = "Rehome",
                PublicId = Guid.NewGuid(),
                RoleId = null, // Part B: users.role_id is platform-role-only, never a tenant role
                IsActive = true,
            };
            seed.Users.Add(identity);
            await seed.SaveChangesAsync();
            identityId = identity.Id;

            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = identity.Id,
                        OwnerId = w,
                        RoleId = wRole.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow.AddDays(-2),
                        SecurityStamp = Guid.NewGuid(),
                    }
                );
            // Membership in X holds an ordinary GLOBAL role — the survivor keeps exactly this one.
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = identity.Id,
                        OwnerId = x,
                        RoleId = globalRole.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow.AddDays(-1),
                        SecurityStamp = Guid.NewGuid(),
                    }
                );
            await seed.SaveChangesAsync();
        }

        using var ctx = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var svc = BuildTenantService(ctx);

        var result = await svc.HardDeleteAsync(w);
        Assert.True(result.IsSuccess, result.Message);

        using var verify = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var survivor = verify.Users.IgnoreQueryFilters().Single(u => u.Id == identityId);
        Assert.Null(survivor.RoleId);
        var survivingMembership = verify
            .Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == identityId);
        Assert.Equal(x, survivingMembership.OwnerId);
        Assert.Equal(globalRoleId, survivingMembership.RoleId);
        Assert.Null(verify.Roles.IgnoreQueryFilters().SingleOrDefault(r => r.Id == wRoleId));
    }

    // ── 9. HomeWorkspace_IsEarliestMembershipOfAnyState ─────────────────────────────────────

    [Fact]
    public async Task HomeWorkspace_IsEarliestMembershipOfAnyState()
    {
        var dbName = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        int userId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = a,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = a,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = b,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = b,
                }
            );
            var role = new Role { Name = "Dev", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var user = new User
            {
                Email = "home@x.com",
                PasswordHash = "h",
                DisplayName = "Home",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            userId = user.Id;

            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = user.Id,
                        OwnerId = a,
                        RoleId = role.Id,
                        IsActive = false,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow.AddDays(-10),
                        LeftAt = DateTime.UtcNow.AddDays(-5),
                        LeftReason = MembershipEndReason.Removed,
                        SecurityStamp = Guid.NewGuid(),
                    }
                );
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = user.Id,
                        OwnerId = b,
                        RoleId = role.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow.AddDays(-1),
                        SecurityStamp = Guid.NewGuid(),
                    }
                );
            seed.SaveChanges();
        }

        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var svc = new MembershipService(new UnitOfWork(ctx));

        Assert.Equal(a, await svc.HomeWorkspaceIdAsync(userId));

        var noMembershipDb = Guid.NewGuid().ToString();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, noMembershipDb))
        {
            var role = new Role
            {
                Name = "SA",
                IsSuperAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            seed.Users.Add(
                new User
                {
                    Email = "sa@x.com",
                    PasswordHash = "h",
                    DisplayName = "SA",
                    PublicId = Guid.NewGuid(),
                    RoleId = role.Id,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }
        using var ctx2 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, noMembershipDb);
        var svc2 = new MembershipService(new UnitOfWork(ctx2));
        var saId = ctx2.Users.IgnoreQueryFilters().Single(u => u.Email == "sa@x.com").Id;
        Assert.Null(await svc2.HomeWorkspaceIdAsync(saId));
    }

    // ── 10. Login_Picker_IsHome_FollowsEarliestMembership ───────────────────────────────────

    [Fact]
    public async Task Login_Picker_IsHome_FollowsEarliestMembership()
    {
        var dbName = Guid.NewGuid().ToString();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        int roleId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceA,
                    Name = "A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceA,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceB,
                    Name = "B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceB,
                }
            );
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;

            // Legacy OwnerId points at B — proves it is no longer read for "home".
            var identity = new User
            {
                Email = "picker@x.com",
                PasswordHash = "hashed:pw12345",
                DisplayName = "Picker",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role); // joined A first
            TestSeed.Join(seed, identity, workspaceB, role); // then B
        }

        var anon = new FakeCurrentUser();
        var auth = BuildAuthService(anon, Ctx(anon, dbName));

        var result = await auth.LoginAsync(
            new LoginRequest { Email = "picker@x.com", Password = "pw12345" }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("choose-workspace", result.Data!.Status);
        Assert.Contains(result.Data.Workspaces!, w => w.WorkspaceId == workspaceA && w.IsHome);
        Assert.Contains(result.Data.Workspaces!, w => w.WorkspaceId == workspaceB && !w.IsHome);
    }

    // ── 11. PasswordResetMail_NamesHomeWorkspace_NotLegacyOwner ─────────────────────────────

    [Fact]
    public async Task PasswordResetMail_NamesHomeWorkspace_NotLegacyOwner()
    {
        var dbName = Guid.NewGuid().ToString();
        var legacyOwner = Guid.NewGuid();
        var home = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = legacyOwner,
                    Name = "Legacy",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = legacyOwner,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = home,
                    Name = "Home",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = home,
                }
            );
            var role = new Role { Name = "Dev", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var user = new User
            {
                Email = "reset@x.com",
                PasswordHash = "hashed:whatever",
                DisplayName = "Reset",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, home, role);
        }

        var anon = new FakeCurrentUser();
        var email = new SpyEmailService();
        var auth = BuildAuthService(anon, Ctx(anon, dbName), email);

        var result = await auth.RequestPasswordResetAsync(
            new ForgotPasswordRequest { Email = "reset@x.com" }
        );

        Assert.True(result.IsSuccess);
        Assert.Single(email.Sent);
        Assert.Contains("Home", email.Sent[0].Html);
        Assert.DoesNotContain("Legacy", email.Sent[0].Html);
    }

    // ── 12. SessionRole_NeverFallsBackToIdentityRole_ForMembers ─────────────────────────────

    [Fact]
    public void SessionRole_NeverFallsBackToIdentityRole_ForMembers()
    {
        var svc = RealTokenService();

        // (i) non-super identity, admin-tier role, NO membership → claims blank/0/false.
        var adminRole = new Role
        {
            Id = 5,
            Name = "Workspace Admin",
            GrantsAdmin = true,
        };
        var nonSuperIdentity = new User
        {
            Id = 1,
            Email = "a@b.c",
            DisplayName = "A",
            RoleId = adminRole.Id,
            Role = adminRole,
        };
        var token1 = svc.Issue(nonSuperIdentity, null);
        var jwt1 = new JwtSecurityTokenHandler().ReadJwtToken(token1);
        Assert.Equal("", jwt1.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("0", jwt1.Claims.First(c => c.Type == "role_id").Value);
        Assert.Equal("false", jwt1.Claims.First(c => c.Type == "is_admin").Value);

        // (ii) a membership whose Role navigation was never loaded → still no role, never the identity's.
        var membershipNoRole = new WorkspaceMembership
        {
            Id = 9,
            OwnerId = Guid.NewGuid(),
            RoleId = 42,
            Role = null!,
            SecurityStamp = Guid.NewGuid(),
        };
        var token2 = svc.Issue(nonSuperIdentity, membershipNoRole);
        var jwt2 = new JwtSecurityTokenHandler().ReadJwtToken(token2);
        Assert.Equal("", jwt2.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("42", jwt2.Claims.First(c => c.Type == "role_id").Value);

        // (iii) a super admin with no membership → the platform role.
        var superRole = new Role
        {
            Id = 1,
            Name = "Admin",
            IsSuperAdmin = true,
            GrantsAdmin = true,
        };
        var superIdentity = new User
        {
            Id = 2,
            Email = "sa@b.c",
            DisplayName = "SA",
            RoleId = superRole.Id,
            Role = superRole,
        };
        var token3 = svc.Issue(superIdentity, null);
        var jwt3 = new JwtSecurityTokenHandler().ReadJwtToken(token3);
        Assert.Equal("true", jwt3.Claims.First(c => c.Type == "is_super_admin").Value);
        Assert.Equal("1", jwt3.Claims.First(c => c.Type == "role_id").Value);
    }

    // (iv) end-to-end through AuthService.MeAsync/BuildMeAsync: a tenant token whose membership has
    // ENDED (GetMembershipAsync's LeftAt == null filter finds nothing — e.g. within the /me 60s
    // cache window right after being removed) never falls back to the identity's own admin-tier
    // role. On `main` (pre-DB-11f) the legacy `identity.Role` fallback made this `true`.
    [Fact]
    public async Task MeAsync_TenantToken_MembershipEnded_IsAdminFalse()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        Guid publicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var adminRole = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();

            var user = new User
            {
                Email = "ended-member@x.com",
                PasswordHash = "h",
                DisplayName = "Ended",
                PublicId = Guid.NewGuid(),
                RoleId = adminRole.Id, // legacy identity role is still admin-tier
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            publicId = user.PublicId;

            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = user.Id,
                        OwnerId = tenant,
                        RoleId = adminRole.Id,
                        ApprovalStatus = ApprovalStatus.Approved,
                        IsActive = false,
                        SecurityStamp = Guid.NewGuid(),
                        JoinedAt = DateTime.UtcNow.AddDays(-1),
                        LeftAt = DateTime.UtcNow,
                    }
                );
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { TenantId = tenant, Id = publicId };
        using var ctx = Ctx(caller, dbName);
        var auth = BuildAuthService(caller, ctx);

        var me = await auth.MeAsync();

        Assert.True(me.IsSuccess, me.Message);
        Assert.False(me.Data!.IsAdmin);
    }

    // ── 13. ApiKeyLogin_NullOwnerKey_NonSuperAdmin_Refused ──────────────────────────────────

    [Fact]
    public async Task ApiKeyLogin_NullOwnerKey_NonSuperAdmin_Refused()
    {
        var dbName = Guid.NewGuid().ToString();
        var protector = new TestApiKeyProtector();
        const string rawKey = "ptr_test_raw_key_0123456789";

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var role = new Role { Name = "Dev", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var identity = new User
            {
                Email = "keyholder@x.com",
                PasswordHash = "h",
                DisplayName = "KeyHolder",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();

            seed.Set<ApiKey>()
                .Add(
                    new ApiKey
                    {
                        UserId = identity.Id,
                        OwnerId = null, // pre-DB-11a leftover
                        Prefix = rawKey[..8],
                        Hash = protector.Hash(rawKey),
                        Encrypted = protector.Encrypt(rawKey),
                        Scopes = 0,
                    }
                );
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();
        var audit = new FakeAuditWriter();
        var uow = new UnitOfWork(Ctx(anon, dbName));
        var auth = new AuthService(
            uow,
            new FakePasswordHasher(),
            RealTokenService(),
            anon,
            new FakeSettings(),
            new FakeResetTokenService(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(uow, protector),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow),
            audit
        );

        var result = await auth.LoginWithApiKeyAsync(
            new LoginWithApiKeyRequest { ApiKey = rawKey }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.InvalidApiKey, result.Message);
        Assert.Contains(
            audit.Entries,
            e =>
                e.Action == AuditActions.AuthLoginFailed
                && e.After != null
                && e.After.TryGetValue("reason", out var reason)
                && reason == "invalid_credentials"
        );

        // Positive case: the super admin's OWN null-owner key still signs in (MFA not enrolled).
        var superRawKey = "ptr_test_raw_key_super_0000000";
        var superDbName = Guid.NewGuid().ToString();
        Guid superPublicId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, superDbName))
        {
            var superRole = new Role
            {
                Name = "Admin",
                IsSuperAdmin = true,
                GrantsAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(superRole);
            seed.SaveChanges();

            var superIdentity = new User
            {
                Email = "sa-key@x.com",
                PasswordHash = "h",
                DisplayName = "SA",
                PublicId = Guid.NewGuid(),
                RoleId = superRole.Id,
                IsActive = true,
            };
            seed.Users.Add(superIdentity);
            seed.SaveChanges();
            superPublicId = superIdentity.PublicId;

            seed.Set<ApiKey>()
                .Add(
                    new ApiKey
                    {
                        UserId = superIdentity.Id,
                        OwnerId = null,
                        Prefix = superRawKey[..8],
                        Hash = protector.Hash(superRawKey),
                        Encrypted = protector.Encrypt(superRawKey),
                        Scopes = 0,
                    }
                );
            seed.SaveChanges();
        }

        var superAnon = new FakeCurrentUser();
        var superUow = new UnitOfWork(Ctx(superAnon, superDbName));
        var superAuth = new AuthService(
            superUow,
            new FakePasswordHasher(),
            RealTokenService(),
            superAnon,
            new FakeSettings(),
            new FakeResetTokenService(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(superUow, protector),
            new FakeLoginAttemptLimiter(),
            new MembershipService(superUow),
            new FakeAuditWriter()
        );

        var superResult = await superAuth.LoginWithApiKeyAsync(
            new LoginWithApiKeyRequest { ApiKey = superRawKey }
        );

        Assert.True(superResult.IsSuccess, superResult.Message);
        Assert.Equal(superPublicId, superResult.Data!.User!.Id);
        Assert.True(superResult.Data!.User!.IsSuperAdmin);
    }

    // ── 14 / 14b. Profile role name ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Profile_RoleName_IsCurrentMembershipRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int userId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var dev = new Role { Name = "Developer", IsActive = true };
            var pm = new Role { Name = "PM", IsActive = true };
            seed.Roles.AddRange(dev, pm);
            seed.SaveChanges();

            var user = new User
            {
                Email = "profile@x.com",
                PasswordHash = "h",
                DisplayName = "Profile",
                PublicId = Guid.NewGuid(),
                RoleId = dev.Id, // creation-time role — must NOT be what shows
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            userId = user.Id;
            TestSeed.Join(seed, user, tenant, pm); // role later changed to PM on the membership
        }

        var caller = new FakeCurrentUser { TenantId = tenant };
        using var ctx = Ctx(caller, dbName);
        var svc = BuildProfileService(caller, ctx);

        var result = await svc.GetByIdAsync(userId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("PM", result.Data!.User.RoleName);

        // Gemini MEDIUM (D11f.7): a super admin caller with NO tenant in hand (viewing a member from
        // no workspace scope) sees the earliest LIVE membership's role, even when an EARLIER ended
        // membership exists — never "earliest of any state" once a live one exists.
        var tenant2 = Guid.NewGuid();
        var tenant3 = Guid.NewGuid();
        int multiWorkspaceUserId;
        using (var seed2 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed2.Workspaces.Add(
                new Workspace
                {
                    Id = tenant2,
                    Name = "T2",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant2,
                }
            );
            seed2.Workspaces.Add(
                new Workspace
                {
                    Id = tenant3,
                    Name = "T3",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant3,
                }
            );
            var oldRole = new Role { Name = "OldRole", IsActive = true };
            var newRole = new Role { Name = "NewRole", IsActive = true };
            var superRole = new Role
            {
                Name = "Admin",
                IsSuperAdmin = true,
                GrantsAdmin = true,
                IsActive = true,
            };
            seed2.Roles.AddRange(oldRole, newRole, superRole);
            seed2.SaveChanges();

            var multiUser = new User
            {
                Email = "multi@x.com",
                PasswordHash = "h",
                DisplayName = "Multi",
                PublicId = Guid.NewGuid(),
                RoleId = oldRole.Id,
                IsActive = true,
            };
            seed2.Users.Add(multiUser);
            seed2.SaveChanges();
            multiWorkspaceUserId = multiUser.Id;

            // EARLIER, now-ended membership in T2.
            seed2
                .Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = multiUser.Id,
                        OwnerId = tenant2,
                        RoleId = oldRole.Id,
                        ApprovalStatus = ApprovalStatus.Approved,
                        IsActive = false,
                        SecurityStamp = Guid.NewGuid(),
                        JoinedAt = DateTime.UtcNow.AddDays(-10),
                        LeftAt = DateTime.UtcNow.AddDays(-5),
                    }
                );
            seed2.SaveChanges();
            // LATER, still-live membership in T3.
            TestSeed.Join(seed2, multiUser, tenant3, newRole);
        }

        var superCaller = new FakeCurrentUser { IsSuperAdmin = true };
        using var superCtx = Ctx(superCaller, dbName);
        var superSvc = BuildProfileService(superCaller, superCtx);
        var multiResult = await superSvc.GetByIdAsync(multiWorkspaceUserId);
        Assert.True(multiResult.IsSuccess, multiResult.Message);
        Assert.Equal("NewRole", multiResult.Data!.User.RoleName);

        // The super admin's OWN profile shows the platform (super) role name.
        var superRoleId2 = superCtx.Roles.IgnoreQueryFilters().Single(r => r.IsSuperAdmin).Id;
        var superIdentity = new User
        {
            Email = "sa-profile@x.com",
            PasswordHash = "h",
            DisplayName = "SA",
            PublicId = Guid.NewGuid(),
            RoleId = superRoleId2,
            IsActive = true,
        };
        superCtx.Users.Add(superIdentity);
        superCtx.SaveChanges();
        var superSelfCaller = new FakeCurrentUser
        {
            IsSuperAdmin = true,
            Id = superIdentity.PublicId,
        };
        using var superSelfCtx = Ctx(superSelfCaller, dbName);
        var superSelfSvc = BuildProfileService(superSelfCaller, superSelfCtx);
        var superSelfResult = await superSelfSvc.GetByIdAsync(superIdentity.Id);
        Assert.True(superSelfResult.IsSuccess, superSelfResult.Message);
        var superRoleName = superSelfCtx
            .Roles.IgnoreQueryFilters()
            .Single(r => r.IsSuperAdmin)
            .Name;
        Assert.Equal(superRoleName, superSelfResult.Data!.User.RoleName);
    }

    [Fact]
    public async Task Profile_And_Preferences_Visible_WhenCreationRoleBelongsToAnotherWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var w = Guid.NewGuid();
        var xTenant = Guid.NewGuid();
        int userId;
        Guid userPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = w,
                    Name = "W",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = w,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = xTenant,
                    Name = "X",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = xTenant,
                }
            );
            var wRole = new Role
            {
                Name = "WOnly",
                OwnerId = w,
                IsActive = true,
            };
            var xRole = new Role
            {
                Name = "XDev",
                OwnerId = xTenant,
                IsActive = true,
            };
            seed.Roles.AddRange(wRole, xRole);
            seed.SaveChanges();

            var user = new User
            {
                Email = "cross@x.com",
                PasswordHash = "h",
                DisplayName = "Cross",
                PublicId = Guid.NewGuid(),
                RoleId = wRole.Id, // creation role owned by W
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            userId = user.Id;
            userPublicId = user.PublicId;
            TestSeed.Join(seed, user, xTenant, xRole); // live membership in X only
        }

        var caller = new FakeCurrentUser { TenantId = xTenant };
        using var ctx = Ctx(caller, dbName);
        var svc = BuildProfileService(caller, ctx);

        var result = await svc.GetByIdAsync(userId);

        // On `main` this 404s (the INNER JOIN via Include(u => u.Role) against the filtered Role set).
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("XDev", result.Data!.User.RoleName);

        var byPublicId = await svc.GetByPublicIdAsync(userPublicId);
        Assert.True(byPublicId.IsSuccess, byPublicId.Message);
        Assert.Equal("XDev", byPublicId.Data!.User.RoleName);

        // The caller acts AS this identity (preferences are self-service) — same tenant X.
        var selfCaller = new FakeCurrentUser { TenantId = xTenant, Id = userPublicId };
        using var selfCtx = Ctx(selfCaller, dbName);
        var prefs = BuildPreferencesService(selfCaller, selfCtx);
        var prefsResult = await prefs.UpdateAsync(new UpdatePreferencesRequest { Theme = "dark" });
        Assert.True(prefsResult.IsSuccess, prefsResult.Message);
    }

    // ── D11f.7 ex-member (orchestrator decision, 2026-09-24, not owner-asked): a tenant-X caller
    // viewing a FORMER member of X (no live membership there anymore) falls back to that SAME
    // workspace's latest ended membership role, instead of "".
    [Fact]
    public async Task Profile_ExMember_FallsBackToLatestEndedMembershipRoleInSameWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();
        int userId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = otherTenant,
                    Name = "Other",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = otherTenant,
                }
            );
            var firstEndedRole = new Role { Name = "FirstEnded", IsActive = true };
            var latestEndedRole = new Role { Name = "LatestEnded", IsActive = true };
            var otherTenantRole = new Role { Name = "OtherTenantRole", IsActive = true };
            seed.Roles.AddRange(firstEndedRole, latestEndedRole, otherTenantRole);
            seed.SaveChanges();

            var user = new User
            {
                Email = "ex-member@x.com",
                PasswordHash = "h",
                DisplayName = "ExMember",
                PublicId = Guid.NewGuid(),
                RoleId = firstEndedRole.Id,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            userId = user.Id;

            // TWO ended memberships in tenant T — the LATEST one's role must win.
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = user.Id,
                        OwnerId = tenant,
                        RoleId = firstEndedRole.Id,
                        ApprovalStatus = ApprovalStatus.Approved,
                        IsActive = false,
                        SecurityStamp = Guid.NewGuid(),
                        JoinedAt = DateTime.UtcNow.AddDays(-30),
                        LeftAt = DateTime.UtcNow.AddDays(-20),
                    }
                );
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = user.Id,
                        OwnerId = tenant,
                        RoleId = latestEndedRole.Id,
                        ApprovalStatus = ApprovalStatus.Approved,
                        IsActive = false,
                        SecurityStamp = Guid.NewGuid(),
                        JoinedAt = DateTime.UtcNow.AddDays(-10),
                        LeftAt = DateTime.UtcNow.AddDays(-5),
                    }
                );
            // A LIVE membership in an unrelated workspace must never leak into tenant T's fallback.
            seed.SaveChanges();
            TestSeed.Join(seed, user, otherTenant, otherTenantRole);
        }

        var caller = new FakeCurrentUser { TenantId = tenant };
        using var ctx = Ctx(caller, dbName);
        var svc = BuildProfileService(caller, ctx);

        var result = await svc.GetByIdAsync(userId);

        // On `main` (pre-fix) this returns "".
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("LatestEnded", result.Data!.User.RoleName);
    }

    // ── 14d. AdminSeeder_DoesNotPromoteAWorkspaceMember ─────────────────────────────────────

    private static ServiceProvider BuildSeederProvider(string dbName, string adminEmail)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser { IsSuperAdmin = true });
        services.AddScoped(sp => new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            sp.GetRequiredService<ICurrentUser>(),
            new ConfigurationBuilder().Build()
        ));
        services.AddScoped<IPasswordHasher>(_ => new FakePasswordHasher());
        services.AddScoped<ISettingsService>(_ => new FakeSettings());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ADMIN:EMAIL"] = adminEmail,
                    ["ADMIN:PASSWORD"] = "operator-password-123",
                }
            )
            .Build();
        services.AddSingleton<IConfiguration>(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task AdminSeeder_DoesNotPromoteAWorkspaceMember()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        const string memberEmail = "member-admin@x.com";

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var member = new User
            {
                Email = memberEmail,
                PasswordHash = "h",
                DisplayName = "Member",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(member);
            seed.SaveChanges();
            TestSeed.Join(seed, member, tenant, role);
        }

        var provider = BuildSeederProvider(dbName, memberEmail);
        await AdminSeeder.SeedAsync(provider);

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var member2 = verify.Users.IgnoreQueryFilters().Single(u => u.Email == memberEmail);
        var superRole = verify.Roles.IgnoreQueryFilters().SingleOrDefault(r => r.IsSuperAdmin);
        Assert.NotNull(superRole);
        Assert.NotEqual(superRole!.Id, member2.RoleId);
        // The reconcile step's throw is caught in its own try/catch (task A17) — role seeding
        // (step 1, runs before the reconcile) and plan seeding (step 3, its own try/catch after)
        // must both still have run; boot is not blocked by the refused promotion.
        Assert.True(
            verify.Roles.IgnoreQueryFilters().Count() >= 7,
            "the 7 default roles must still be seeded"
        );
        Assert.Single(verify.Plans.IgnoreQueryFilters().Where(p => p.Slug == "free"));
        Assert.Single(verify.Plans.IgnoreQueryFilters().Where(p => p.Slug == "legacy"));

        // Regression guard: a membership-less address still promotes (the user == null branch —
        // a brand-new identity created by the seeder itself).
        var dbName2 = Guid.NewGuid().ToString();
        const string freshEmail = "fresh-admin@x.com";
        var provider2 = BuildSeederProvider(dbName2, freshEmail);
        await AdminSeeder.SeedAsync(provider2);
        using var verify2 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName2);
        var fresh = verify2.Users.IgnoreQueryFilters().Single(u => u.Email == freshEmail);
        var superRole2 = verify2.Roles.IgnoreQueryFilters().Single(r => r.IsSuperAdmin);
        Assert.Equal(superRole2.Id, fresh.RoleId);

        // Regression guard (the reconcile ELSE branch): an EXISTING identity with NO membership
        // anywhere and a non-super RoleId, matching ADMIN__EMAIL, IS still promoted — D11f.8 only
        // refuses a WORKSPACE MEMBER, never a membership-less existing identity.
        var dbName3 = Guid.NewGuid().ToString();
        const string existingNoMembershipEmail = "existing-no-membership-admin@x.com";
        int existingUserId;
        using (var seed3 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName3))
        {
            var devRole = new Role { Name = "Developer", IsActive = true };
            seed3.Roles.Add(devRole);
            seed3.SaveChanges();

            var existing = new User
            {
                Email = existingNoMembershipEmail,
                PasswordHash = "h",
                DisplayName = "Existing",
                PublicId = Guid.NewGuid(),
                RoleId = devRole.Id, // non-super — must be reconciled to the super role
                IsActive = true,
            };
            seed3.Users.Add(existing);
            seed3.SaveChanges();
            existingUserId = existing.Id; // no membership row is created for this identity
        }
        var provider3 = BuildSeederProvider(dbName3, existingNoMembershipEmail);
        await AdminSeeder.SeedAsync(provider3);
        using var verify3 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName3);
        var existingAfter = verify3.Users.IgnoreQueryFilters().Single(u => u.Id == existingUserId);
        var superRole3 = verify3.Roles.IgnoreQueryFilters().Single(r => r.IsSuperAdmin);
        Assert.Equal(superRole3.Id, existingAfter.RoleId);
    }

    // ── 17. User_HasNoLegacyTenancyMembers ──────────────────────────────────────────────────

    [Fact]
    public void User_HasNoLegacyTenancyMembers()
    {
        using var db = Ctx(
            new FakeCurrentUser { IsSuperAdmin = true },
            nameof(User_HasNoLegacyTenancyMembers)
        );
        var entityType = db.Model.FindEntityType(typeof(User))!;

        Assert.Null(typeof(User).GetProperty("OwnerId"));
        Assert.Null(typeof(User).GetProperty("ApprovalStatus"));
        Assert.Null(entityType.FindProperty("OwnerId"));
        Assert.Null(entityType.FindProperty("ApprovalStatus"));

        Assert.DoesNotContain(
            entityType.GetIndexes(),
            ix => ix.Name == "ux_users_email_owner_live"
        );
        Assert.DoesNotContain(
            entityType.GetIndexes(),
            ix => ix.Properties.Any(p => p.Name == "OwnerId")
        );

        Assert.True(entityType.FindProperty("RoleId")!.IsNullable);

        // Guards over-deletion — these stay.
        Assert.NotNull(typeof(User).GetProperty("Role"));
        Assert.NotNull(typeof(User).GetProperty("IsActive"));
        Assert.NotNull(typeof(User).GetProperty("MergedIntoUserId"));
    }

    // ── 18. NewIdentity_And_DemoProvision_WriteNoPlatformRole ───────────────────────────────

    [Fact]
    public async Task NewIdentity_And_DemoProvision_WriteNoPlatformRole()
    {
        var dbName = Guid.NewGuid().ToString();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var membershipService = new MembershipService(new UnitOfWork(seed));
            var identity = membershipService.NewIdentity(
                "newidentity@x.com",
                "hash",
                "New Identity",
                role,
                Guid.NewGuid()
            );
            Assert.Null(identity.RoleId);
        }

        var demoDbName = dbName + "-demo";
        using (var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, demoDbName))
        {
            db.Roles.Add(new Role { Name = "Workspace Admin", OwnerId = null });
            db.SaveChanges();
            var uow = new UnitOfWork(db);
            // A REAL AuditWriter sharing the SAME DbContext as the UnitOfWork (the production
            // shape) — not a no-op — so a re-added `demoUser.Role = role` write would actually be
            // persisted by the audit write's SaveChangesAsync and would be caught below (review
            // finding: the Noop default made this assertion vacuous).
            var audit = new AuditWriter(
                db,
                new FakeCurrentUser { IsSuperAdmin = true },
                new FakeHttpContextAccessor(),
                new ConfigurationBuilder().Build(),
                NullLogger<AuditWriter>.Instance
            );
            var svc = new DemoService(
                uow,
                new FakePasswordHasher(),
                RealTokenService(),
                new NoopEmail(),
                new FakeSettings(),
                new NoopBrandingService(),
                new MembershipService(uow),
                audit
            );

            var result = await svc.ProvisionAsync(
                "https://demo.pointer.example",
                "person@real.com"
            );
            Assert.True(result.IsSuccess, result.Message);
        }

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, demoDbName);
        var demoUser = verify.Users.IgnoreQueryFilters().Single(u => u.IsDemo);
        Assert.Null(demoUser.RoleId);

        // Part (3) of §6 item 18: AdminSeeder against an empty database leaves the seeded super
        // admin's RoleId pointing at the (global) super-admin role.
        var seederDbName = Guid.NewGuid().ToString();
        const string seederAdminEmail = "empty-db-admin@x.com";
        var provider = BuildSeederProvider(seederDbName, seederAdminEmail);
        await AdminSeeder.SeedAsync(provider);
        using var seederVerify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, seederDbName);
        var seededAdmin = seederVerify
            .Users.IgnoreQueryFilters()
            .Single(u => u.Email == seederAdminEmail);
        var seededSuperRole = seederVerify.Roles.IgnoreQueryFilters().Single(r => r.IsSuperAdmin);
        Assert.Equal(seededSuperRole.Id, seededAdmin.RoleId);
    }

    // ── Review item 4: AppDbContext.EnforcePlatformRoleInvariant ────────────────────────────
    // users.role_id must be null or point at a GLOBAL (OwnerId == null) IsSuperAdmin role — never a
    // tenant role. Covers all three shapes on the async save path (the one production actually uses).

    [Fact]
    public async Task PlatformRoleInvariant_RefusesUserWithATenantRoleId()
    {
        var dbName = Guid.NewGuid().ToString();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var tenantRole = new Role
        {
            Name = "Developer",
            OwnerId = Guid.NewGuid(),
            IsActive = true,
        };
        ctx.Roles.Add(tenantRole);
        await ctx.SaveChangesAsync();

        ctx.Users.Add(
            new User
            {
                Email = "member@x.com",
                PasswordHash = "h",
                DisplayName = "Member",
                PublicId = Guid.NewGuid(),
                RoleId = tenantRole.Id,
                IsActive = true,
            }
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
        Assert.Contains("DB-11f invariant", ex.Message);
    }

    [Fact]
    public async Task PlatformRoleInvariant_AllowsUserWithTheGlobalSuperAdminRoleId()
    {
        var dbName = Guid.NewGuid().ToString();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var superRole = new Role
        {
            Name = "Admin",
            IsSuperAdmin = true,
            IsActive = true,
        };
        ctx.Roles.Add(superRole);
        await ctx.SaveChangesAsync();

        ctx.Users.Add(
            new User
            {
                Email = "super@x.com",
                PasswordHash = "h",
                DisplayName = "Super",
                PublicId = Guid.NewGuid(),
                RoleId = superRole.Id,
                IsActive = true,
            }
        );

        await ctx.SaveChangesAsync(); // must not throw

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var saved = verify.Users.IgnoreQueryFilters().Single(u => u.Email == "super@x.com");
        Assert.Equal(superRole.Id, saved.RoleId);
    }

    [Fact]
    public async Task PlatformRoleInvariant_AllowsUserWithNullRoleId()
    {
        var dbName = Guid.NewGuid().ToString();
        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        ctx.Users.Add(
            new User
            {
                Email = "member2@x.com",
                PasswordHash = "h",
                DisplayName = "Member2",
                PublicId = Guid.NewGuid(),
                RoleId = null,
                IsActive = true,
            }
        );

        await ctx.SaveChangesAsync(); // must not throw

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var saved = verify.Users.IgnoreQueryFilters().Single(u => u.Email == "member2@x.com");
        Assert.Null(saved.RoleId);
    }

    [Fact]
    public async Task PlatformRoleInvariant_RefusesReassigningAnExistingUserToATenantRoleId()
    {
        var dbName = Guid.NewGuid().ToString();
        int userId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var user = new User
            {
                Email = "member3@x.com",
                PasswordHash = "h",
                DisplayName = "Member3",
                PublicId = Guid.NewGuid(),
                RoleId = null,
                IsActive = true,
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        using var ctx = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var tenantRole = new Role
        {
            Name = "Developer",
            OwnerId = Guid.NewGuid(),
            IsActive = true,
        };
        ctx.Roles.Add(tenantRole);
        await ctx.SaveChangesAsync();

        var tracked = await ctx.Users.SingleAsync(u => u.Id == userId);
        tracked.RoleId = tenantRole.Id; // a genuine reassignment, not an incidental touch
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SaveChangesAsync());
        Assert.Contains("DB-11f invariant", ex.Message);
    }
}
