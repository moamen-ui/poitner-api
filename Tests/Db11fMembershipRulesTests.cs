using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pointer.API.Seed;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Response;
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

    // ── 5. DeleteSet_IsMembershipOnly_EveryShape ────────────────────────────────────────────

    [Fact]
    public async Task DeleteSet_IsMembershipOnly_EveryShape()
    {
        var dbName = Guid.NewGuid().ToString();
        var w = Guid.NewGuid();
        var x = Guid.NewGuid();
        var uow0 = new UnitOfWork(Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName));

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

            User Make(
                string email,
                Guid? ownerId,
                int roleId,
                bool erased = false,
                int? mergedInto = null
            ) =>
                new()
                {
                    Email = email,
                    PasswordHash = "h",
                    DisplayName = email,
                    PublicId = Guid.NewGuid(),
                    OwnerId = ownerId,
                    RoleId = roleId,
                    IsActive = !erased,
                    DeletedAt = erased ? DateTime.UtcNow : null,
                    ErasedAt = erased ? DateTime.UtcNow : null,
                    MergedIntoUserId = mergedInto,
                };

            var a = Make("a@x", w, wRole.Id);
            var b = Make("b@x", w, wRole.Id);
            var c = Make("c@x", x, wRole.Id);
            var d = Make("d@x", w, wRole.Id, erased: true);
            var e = Make("e@x", w, wRole.Id, erased: true);
            var f = Make("f@x", null, superRole.Id);
            var g = Make("g@x", null, superRole.Id);
            var j = Make("j@x", w, wRole.Id);
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

            var h = Make("h@x", null, wRole.Id, erased: true, mergedInto: a.Id); // (h) merged tombstone of (a) → in
            var i = Make("i@x", null, wRole.Id, erased: true, mergedInto: c.Id); // (i) merged tombstone of (c) ...
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

    // ── 6. DeleteSet_EqualsLegacyRule_WhenInvariantI1Holds ──────────────────────────────────

    [Fact]
    public async Task DeleteSet_EqualsLegacyRule_WhenInvariantI1Holds()
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

            User Make(string email, Guid? ownerId, int roleId, bool erased = false) =>
                new()
                {
                    Email = email,
                    PasswordHash = "h",
                    DisplayName = email,
                    PublicId = Guid.NewGuid(),
                    OwnerId = ownerId,
                    RoleId = roleId,
                    IsActive = !erased,
                    DeletedAt = erased ? DateTime.UtcNow : null,
                    ErasedAt = erased ? DateTime.UtcNow : null,
                };

            var a = Make("a@x", w, wRole.Id);
            var b = Make("b@x", w, wRole.Id);
            var c = Make("c@x", x, wRole.Id);
            var d = Make("d@x", w, wRole.Id, erased: true);
            var e = Make("e@x", w, wRole.Id, erased: true);
            var f = Make("f@x", null, superRole.Id);
            var g = Make("g@x", null, superRole.Id);
            seed.Users.AddRange(a, b, c, d, e, f, g);
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

            Join(a, w);
            Join(b, w);
            Join(b, x, leftAt: DateTime.UtcNow.AddDays(-1));
            Join(c, x);
            Join(c, w);
            Join(d, w, live: false, leftAt: DateTime.UtcNow.AddDays(-1));
            Join(e, w, live: true);
            Join(g, w);
            seed.SaveChanges();
        }

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var uow = new UnitOfWork(verify);

        var newSet = TenantService
            .IdentitiesDeletedWithWorkspace(uow, w)
            .Select(u => u.Email)
            .ToHashSet();
        // The pre-DB-11f predicate, inlined here (Part B deletes this test — the column is gone).
        var oldSet = verify
            .Users.IgnoreQueryFilters()
            .Where(u =>
                u.OwnerId == w
                && !verify
                    .Set<WorkspaceMembership>()
                    .IgnoreQueryFilters()
                    .Any(m => m.UserId == u.Id && m.OwnerId != w)
            )
            .Select(u => u.Email)
            .ToHashSet();

        Assert.Equal(oldSet, newSet);
    }

    // ── 7. HardDelete_RehomedIdentityWithTenantRole_Succeeds_UnderRealForeignKeys ───────────

    [Fact]
    public async Task HardDelete_RehomedIdentityWithTenantRole_Succeeds_UnderRealForeignKeys()
    {
        using var db = new TestDb();
        var w = Guid.NewGuid();
        var x = Guid.NewGuid();
        int identityId;
        int developerRoleId;

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
            var developer = new Role { Name = "Developer", IsActive = true };
            var superRole = new Role
            {
                Name = "SA",
                IsSuperAdmin = true,
                IsActive = true,
            };
            var wRole = new Role
            {
                Name = "WOwnedRole",
                OwnerId = w,
                IsActive = true,
            };
            seed.Roles.AddRange(developer, superRole, wRole);
            await seed.SaveChangesAsync();
            developerRoleId = developer.Id;

            var identity = new User
            {
                Email = "rehome@w.com",
                PasswordHash = "h",
                DisplayName = "Rehome",
                PublicId = Guid.NewGuid(),
                OwnerId = w,
                RoleId = wRole.Id, // W-owned role — the latent FK bug this test proves is fixed
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
            // Membership in X holds the SUPER role — proves the re-point never copies a MEMBERSHIP's
            // role (only the legacy users.role_id, and only to the least-privilege global role).
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = identity.Id,
                        OwnerId = x,
                        RoleId = superRole.Id,
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
        Assert.Equal(x, survivor.OwnerId);
        Assert.Equal(developerRoleId, survivor.RoleId);
        Assert.Null(verify.Roles.IgnoreQueryFilters().SingleOrDefault(r => r.OwnerId == w));
    }

    // ── 8. HardDelete_MembershipLessIdentityReferencingWorkspace_RefusedBeforeAnySideEffect ─

    [Fact]
    public async Task HardDelete_MembershipLessIdentityReferencingWorkspace_RefusedBeforeAnySideEffect()
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
                OwnerId = w,
                RoleId = role.Id,
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

            // Shape (j): legacy owner_id points at W, but NO membership anywhere — invariant I1 broken.
            var orphan = new User
            {
                Email = "orphan@w.com",
                PasswordHash = "h",
                DisplayName = "Orphan",
                PublicId = Guid.NewGuid(),
                OwnerId = w,
                RoleId = role.Id,
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

        Assert.False(result.IsSuccess);
        Assert.Contains("DB-11f invariant I1", result.Message);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Empty(fileStorage.DeletedOwners);

        using var verify = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        Assert.NotNull(verify.Workspaces.IgnoreQueryFilters().SingleOrDefault(x => x.Id == w));
        Assert.NotNull(
            verify.Projects.IgnoreQueryFilters().SingleOrDefault(p => p.Id == projectId)
        );
        Assert.NotNull(verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == adminUserId));
        Assert.NotNull(
            verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == orphanUserId)
        );
    }

    // ── 8b. HardDelete_NoLeastPrivilegeGlobalRole_RefusedBeforeAnySideEffect ────────────────

    [Fact]
    public async Task HardDelete_NoLeastPrivilegeGlobalRole_RefusedBeforeAnySideEffect()
    {
        using var db = new TestDb();
        var w = Guid.NewGuid();
        var x = Guid.NewGuid();

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
            // No least-privilege global role exists — every global role is admin/quick-access/super.
            var wRole = new Role
            {
                Name = "WOwnedRole",
                OwnerId = w,
                IsActive = true,
            };
            var globalAdmin = new Role
            {
                Name = "GlobalAdmin",
                OwnerId = null,
                IsActive = true,
                GrantsAdmin = true,
            };
            seed.Roles.AddRange(wRole, globalAdmin);
            await seed.SaveChangesAsync();

            var identity = new User
            {
                Email = "rehome2@w.com",
                PasswordHash = "h",
                DisplayName = "Rehome2",
                PublicId = Guid.NewGuid(),
                OwnerId = w,
                RoleId = wRole.Id,
                IsActive = true,
            };
            seed.Users.Add(identity);
            await seed.SaveChangesAsync();

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
            seed.Set<WorkspaceMembership>()
                .Add(
                    new WorkspaceMembership
                    {
                        UserId = identity.Id,
                        OwnerId = x,
                        RoleId = globalAdmin.Id,
                        IsActive = true,
                        ApprovalStatus = ApprovalStatus.Approved,
                        JoinedAt = DateTime.UtcNow.AddDays(-1),
                        SecurityStamp = Guid.NewGuid(),
                    }
                );
            await seed.SaveChangesAsync();
        }

        var fileStorage = new RecordingFileStorage();
        var audit = new FakeAuditWriter();
        using var ctx = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
        var svc = BuildTenantService(ctx, fileStorage, audit);

        var result = await svc.HardDeleteAsync(w, "admin");

        Assert.False(result.IsSuccess);
        Assert.Contains("no global least-privilege role", result.Message);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.TenantHardDeleted);
        Assert.Empty(fileStorage.DeletedOwners);
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
                OwnerId = workspaceB,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
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
                OwnerId = legacyOwner,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
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
                ApprovalStatus = ApprovalStatus.Approved,
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
            new FakeAuditWriter()
        );

        var result = await auth.LoginWithApiKeyAsync(
            new LoginWithApiKeyRequest { ApiKey = rawKey }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.InvalidApiKey, result.Message);
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
    }

    [Fact]
    public async Task Profile_And_Preferences_Visible_WhenCreationRoleBelongsToAnotherWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var w = Guid.NewGuid();
        var xTenant = Guid.NewGuid();
        int userId;

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
            TestSeed.Join(seed, user, xTenant, xRole); // live membership in X only
        }

        var caller = new FakeCurrentUser { TenantId = xTenant };
        using var ctx = Ctx(caller, dbName);
        var svc = BuildProfileService(caller, ctx);

        var result = await svc.GetByIdAsync(userId);

        // On `main` this 404s (the INNER JOIN via Include(u => u.Role) against the filtered Role set).
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("XDev", result.Data!.User.RoleName);
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
                ApprovalStatus = ApprovalStatus.Approved,
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
        Assert.True(superRole == null || member2.RoleId != superRole.Id);

        // Regression guard: a membership-less address still promotes.
        var dbName2 = Guid.NewGuid().ToString();
        const string freshEmail = "fresh-admin@x.com";
        var provider2 = BuildSeederProvider(dbName2, freshEmail);
        await AdminSeeder.SeedAsync(provider2);
        using var verify2 = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName2);
        var fresh = verify2.Users.IgnoreQueryFilters().Single(u => u.Email == freshEmail);
        var superRole2 = verify2.Roles.IgnoreQueryFilters().Single(r => r.IsSuperAdmin);
        Assert.Equal(superRole2.Id, fresh.RoleId);
    }
}
