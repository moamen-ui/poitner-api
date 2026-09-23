using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.API.Auth;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11a core invariants (DB-RULES R8.7): a workspace_memberships row, never users.owner_id, is
/// what makes a person "of" a workspace, and ending a membership in one workspace never touches the
/// same identity's membership elsewhere.
/// </summary>
public class WorkspaceMembershipTests
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

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) => Task.FromResult("");
        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");
        public string IssueSelection(User user) => "selection-for-" + user.PublicId.ToString("N");
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
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
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

    private static UserService BuildUserService(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new UserService(
            uow,
            new FakePasswordHasher(),
            user,
            new NoopEmail(),
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(uow)
        );
    }

    private static AuthService BuildAuthService(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenService(),
            user,
            new FakeSettings(),
            new FakeResetTokenService(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static (Guid workspaceA, Guid workspaceB, int roleId) SeedTwoWorkspaces(string db)
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
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
        var role = new Role
        {
            Name = "Developer",
            GrantsAdmin = false,
            IsActive = true,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();
        return (workspaceA, workspaceB, role.Id);
    }

    [Fact]
    public void InWorkspace_TenantB_SeesNothingOfTenantA()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        Guid userAId,
            userBId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var userA = new User
            {
                Email = "a@x.com",
                PasswordHash = "x",
                DisplayName = "A",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
            };
            var userB = new User
            {
                Email = "b@x.com",
                PasswordHash = "x",
                DisplayName = "B",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceB,
                IsActive = true,
            };
            seed.Users.Add(userA);
            seed.Users.Add(userB);
            seed.SaveChanges();
            TestSeed.Join(seed, userA, workspaceA, role);
            TestSeed.Join(seed, userB, workspaceB, role);
            userAId = userA.PublicId;
            userBId = userB.PublicId;
        }

        var uow = new UnitOfWork(Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db));
        var memberships = new MembershipService(uow);

        var membersOfA = memberships.InWorkspace(workspaceA).Select(m => m.User.PublicId).ToList();
        Assert.Single(membersOfA);
        Assert.Equal(userAId, membersOfA[0]);
        Assert.DoesNotContain(userBId, membersOfA);

        // R8.7: a caller scoped to tenant B sees no identity or membership of tenant A.
        var tenantBCaller = new FakeCurrentUser { TenantId = workspaceB };
        using var scopedCtx = Ctx(tenantBCaller, db);
        var visibleUsers = scopedCtx.Set<User>().ToList();
        Assert.Single(visibleUsers);
        Assert.Equal(userBId, visibleUsers[0].PublicId);

        // F5 (DB-11a review): assert the WorkspaceMembership side of R8.7 too, not just User —
        // tenant B's context must not see tenant A's membership row either.
        var visibleMemberships = scopedCtx.Set<WorkspaceMembership>().ToList();
        Assert.Single(visibleMemberships);
        Assert.Equal(workspaceB, visibleMemberships[0].OwnerId);
    }

    [Fact]
    public async Task Login_SameEmailInTwoWorkspaces_IsOneIdentity_HomeWins()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "shared@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Shared",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA, // home = A
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role);
            identityId = identity.PublicId;
        }

        // Exactly one identity row exists.
        using (var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            Assert.Single(check.Users.IgnoreQueryFilters().Where(u => u.Email == "shared@x.com"));
        }

        var memberships = new MembershipService(
            new UnitOfWork(Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        );
        var user = await memberships.FindIdentityByEmailAsync("shared@x.com");
        Assert.NotNull(user);
        var live = await memberships.ListForIdentityAsync(user!.Id);
        Assert.Equal(2, live.Count);
        Assert.Contains(live, m => m.OwnerId == workspaceA);
        Assert.Contains(live, m => m.OwnerId == workspaceB);
    }

    // ── §6.19 / GLM A8 (DB-11a review): a wrong password on a MERGED identity points at "Forgot
    // password" instead of the generic invalid-credentials message; an ordinary wrong password on
    // an identity that never absorbed a merge gets the plain message. ─────────────────────────────
    [Fact]
    public async Task Login_WrongPassword_MergedIdentity_SuggestsForgotPassword()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, _, roleId) = SeedTwoWorkspaces(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var canonical = new User
            {
                Email = "merged@x.com",
                PasswordHash = "hashed:rightpw123",
                DisplayName = "Canonical",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(canonical);
            seed.SaveChanges();
            TestSeed.Join(seed, canonical, workspaceA, role);

            // A row that Migration 2 (2.6) merged INTO the canonical identity: soft-deleted,
            // MergedIntoUserId pointing at it.
            var merged = new User
            {
                Email = "merged@x.com",
                PasswordHash = "hashed:oldpw12345",
                DisplayName = "Merged away",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = false,
                ApprovalStatus = ApprovalStatus.Approved,
                DeletedAt = DateTime.UtcNow,
                MergedIntoUserId = canonical.Id,
            };
            seed.Users.Add(merged);
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();
        var auth = BuildAuthService(anon, Ctx(anon, db));

        var result = await auth.LoginAsync(
            new LoginRequest { Email = "merged@x.com", Password = "totallywrongpw" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.InvalidCredentialsAfterMerge, result.Message);
    }

    [Fact]
    public async Task Login_WrongPassword_UnmergedIdentity_GenericInvalidCredentials()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, _, roleId) = SeedTwoWorkspaces(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var user = new User
            {
                Email = "solo@x.com",
                PasswordHash = "hashed:rightpw123",
                DisplayName = "Solo",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, workspaceA, role);
        }

        var anon = new FakeCurrentUser();
        var auth = BuildAuthService(anon, Ctx(anon, db));

        var result = await auth.LoginAsync(
            new LoginRequest { Email = "solo@x.com", Password = "totallywrongpw" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.InvalidCredentials, result.Message);
    }

    [Fact]
    public async Task UserNameResolver_ResolvesAliasedAuthor()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, _, roleId) = SeedTwoWorkspaces(db);
        var oldAliasId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "merged@x.com",
                PasswordHash = "x",
                DisplayName = "Merged Person",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);

            seed.Set<UserAlias>()
                .Add(
                    new UserAlias
                    {
                        AliasPublicId = oldAliasId,
                        UserId = identity.Id,
                        SourceWorkspaceId = workspaceA,
                        MergedAt = DateTime.UtcNow,
                    }
                );
            seed.SaveChanges();
        }

        var uow = new UnitOfWork(Ctx(new FakeCurrentUser { TenantId = workspaceA }, db));
        var names = await UserNameResolver.ResolveAsync(uow, new[] { oldAliasId });

        Assert.Equal("Merged Person", names[oldAliasId]);
    }

    [Fact]
    public async Task HardDelete_Workspace_KeepsMultiWorkspaceIdentity_EndsOnlyThatMembership()
    {
        using var db = new TestDb();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };

        using (var seed = db.MakeContext(superAdmin))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceA,
                    Name = "Workspace A",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceA,
                }
            );
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceB,
                    Name = "Workspace B",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceB,
                }
            );

            var role = new Role
            {
                Name = "Developer",
                GrantsAdmin = false,
                IsActive = true,
            };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            // Identity X in both A and B
            var userX = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "userx@example.com",
                PasswordHash = "hash",
                DisplayName = "User X",
                RoleId = role.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(userX);

            // Identity Y only in A
            var userY = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "usery@example.com",
                PasswordHash = "hash",
                DisplayName = "User Y",
                RoleId = role.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(userY);
            await seed.SaveChangesAsync();

            TestSeed.Join(seed, userX, workspaceA, role);
            TestSeed.Join(seed, userX, workspaceB, role);
            TestSeed.Join(seed, userY, workspaceA, role);
        }

        using var ctx = db.MakeContext(superAdmin);
        var svc = new TenantService(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(ctx))
        );

        var result = await svc.HardDeleteAsync(workspaceA);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(23, TenantService.HardDeleteOrder.Length);

        using var verify = db.MakeContext(superAdmin);

        // Identity X survives and is re-homed to workspace B
        var survivingX = await verify.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == "userx@example.com");
        Assert.NotNull(survivingX);
        Assert.Equal(workspaceB, survivingX!.OwnerId);

        // Membership in B is untouched
        var xMembershipB = await verify.WorkspaceMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == survivingX.Id && m.OwnerId == workspaceB);
        Assert.NotNull(xMembershipB);
        Assert.Null(xMembershipB!.LeftAt);
        Assert.True(xMembershipB.IsActive);

        // Membership in A is gone
        var xMembershipA = await verify.WorkspaceMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == survivingX.Id && m.OwnerId == workspaceA);
        Assert.Null(xMembershipA);

        // Identity Y only belonged to A, so it is hard-deleted
        var deletedY = await verify.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == "usery@example.com");
        Assert.Null(deletedY);

        // Workspace A is deleted; Workspace B remains
        Assert.Null(await verify.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == workspaceA));
        Assert.NotNull(await verify.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == workspaceB));

        // R8.7: Under tenant B context, identity X is visible
        using var tenantBContext = db.MakeContext(new FakeCurrentUser { TenantId = workspaceB });
        var visibleUsers = await tenantBContext.Users.ToListAsync();
        Assert.Contains(visibleUsers, u => u.Id == survivingX.Id);
    }

    // ── F9 (DB-11a cross-review): before the fix, an identity that is Workspace Admin of MORE
    // THAN ONE workspace made `ResolveCanonicalAdminAsync` return a null membership
    // (`adminMemberships.Count != 1`), so `SetStatusAsync`/`ChangePlanAsync`/hard-delete 404'd for
    // BOTH workspaces — the exact capability this release ships (D13). Keying every one of these on
    // the WORKSPACE id (never the admin's `users.id`) must resolve each workspace independently. ──
    [Fact]
    public async Task TenantService_MultiWorkspaceAdmin_ResolvesEachWorkspaceIndependently()
    {
        using var db = new TestDb();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        int proId;

        using (var seed = db.MakeContext(superAdmin))
        {
            seed.Workspaces.Add(new Workspace { Id = workspaceA, Name = "Workspace A", CreatedAt = DateTime.UtcNow, CreatedBy = workspaceA });
            seed.Workspaces.Add(new Workspace { Id = workspaceB, Name = "Workspace B", CreatedAt = DateTime.UtcNow, CreatedBy = workspaceB });

            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsActive = true };
            seed.Roles.Add(adminRole);
            var proPlan = new Plan
            {
                Name = "Pro",
                Slug = "pro",
                IsActive = true,
                DisplayState = PlanDisplayState.Visible,
                Entitlements = new PlanEntitlements(),
            };
            seed.Plans.Add(proPlan);
            await seed.SaveChangesAsync();
            proId = proPlan.Id;

            var admin = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "multiadmin@example.com",
                PasswordHash = "hash",
                DisplayName = "Multi Admin",
                RoleId = adminRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(admin);
            await seed.SaveChangesAsync();

            TestSeed.Join(seed, admin, workspaceA, adminRole);
            TestSeed.Join(seed, admin, workspaceB, adminRole);
        }

        using var ctx = db.MakeContext(superAdmin);
        var svc = new TenantService(
            new UnitOfWork(ctx),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(ctx))
        );

        // Disabling A's membership must succeed (not 404) and touch ONLY A's membership.
        var disableA = await svc.SetStatusAsync(workspaceA, "disable");
        Assert.True(disableA.IsSuccess, disableA.Message);

        // Changing B's plan must ALSO succeed (not 404) and scope the subscription to B, not A.
        var changePlanB = await svc.ChangePlanAsync(workspaceB, proId);
        Assert.True(changePlanB.IsSuccess, changePlanB.Message);

        using (var verify = db.MakeContext(superAdmin))
        {
            var membershipA = await verify
                .WorkspaceMemberships.IgnoreQueryFilters()
                .Include(m => m.User)
                .FirstAsync(m => m.OwnerId == workspaceA && m.User.Email == "multiadmin@example.com");
            var membershipB = await verify
                .WorkspaceMemberships.IgnoreQueryFilters()
                .Include(m => m.User)
                .FirstAsync(m => m.OwnerId == workspaceB && m.User.Email == "multiadmin@example.com");

            Assert.False(membershipA.IsActive); // disabled by SetStatusAsync(workspaceA, ...)
            Assert.True(membershipB.IsActive); // untouched

            var subA = await verify.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OwnerId == workspaceA);
            var subB = await verify.Subscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OwnerId == workspaceB);
            Assert.Null(subA);
            Assert.NotNull(subB);
            Assert.Equal(proId, subB!.PlanId);
        }

        // Hard-delete of A must also succeed directly off the workspace id (no admin-membership
        // resolution at all) — and the identity survives via its remaining membership in B.
        var deleteA = await svc.HardDeleteAsync(workspaceA);
        Assert.True(deleteA.IsSuccess, deleteA.Message);

        using var verify2 = db.MakeContext(superAdmin);
        Assert.Null(await verify2.Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == workspaceA));
        var survivor = await verify2.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == "multiadmin@example.com");
        Assert.NotNull(survivor);
        Assert.Equal(workspaceB, survivor!.OwnerId);
    }

    [Fact]
    public void StampValidator_RejectsMembershipStampMismatch()
    {
        var tokenStamp = Guid.NewGuid();
        var tokenMstamp = Guid.NewGuid();
        var differentMstamp = Guid.NewGuid();

        // 1. Matches: valid
        var valid = StampValidator.Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant: true,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: tokenMstamp,
            isMembershipLive: true
        );
        Assert.True(valid);

        // 2. Membership stamp mismatch: rejected (DB-RULES R16)
        var mismatch = StampValidator.Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant: true,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: differentMstamp,
            isMembershipLive: true
        );
        Assert.False(mismatch);

        // 3. Inactive membership: rejected
        var inactive = StampValidator.Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant: true,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: tokenMstamp,
            isMembershipLive: false
        );
        Assert.False(inactive);

        // 4. Missing membership in DB: rejected
        var missing = StampValidator.Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant: true,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: null,
            isMembershipLive: false
        );
        Assert.False(missing);

        // 5. Tenant token without mstamp claim: rejected
        var missingMstamp = StampValidator.Validate(
            tokenStamp,
            tokenMstamp: null,
            hasTenant: true,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: tokenMstamp,
            isMembershipLive: true
        );
        Assert.False(missingMstamp);

        // 6. Identity stamp mismatch: rejected
        var identityMismatch = StampValidator.Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant: true,
            currentUserStamp: Guid.NewGuid(),
            currentMembershipStamp: tokenMstamp,
            isMembershipLive: true
        );
        Assert.False(identityMismatch);

        // 7. Super-admin token (no tenant): skips membership check (R16)
        var superAdmin = StampValidator.Validate(
            tokenStamp,
            tokenMstamp: null,
            hasTenant: false,
            currentUserStamp: tokenStamp,
            currentMembershipStamp: null,
            isMembershipLive: false
        );
        Assert.True(superAdmin);
    }

    [Fact]
    public async Task S14_NullTenantNonSuperAdmin_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, _, roleId) = SeedTwoWorkspaces(db);

        // 1. Direct unit test of TenantStamp.TryRequireOwner (S-14 invariant)
        var nullTenantNonSuperAdmin = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            IsSuperAdmin = false,
            TenantId = null,
        };
        Assert.False(TenantStamp.TryRequireOwner(nullTenantNonSuperAdmin, out var owner));
        Assert.Equal(Guid.Empty, owner);

        var validTenantCaller = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            IsSuperAdmin = false,
            TenantId = workspaceA,
        };
        Assert.True(TenantStamp.TryRequireOwner(validTenantCaller, out var resolvedOwner));
        Assert.Equal(workspaceA, resolvedOwner);

        var superAdminCaller = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            IsSuperAdmin = true,
            TenantId = null,
        };
        Assert.False(TenantStamp.TryRequireOwner(superAdminCaller, out _));

        // 2. S-14 at service call site: UserService.CreateAsync refuses a non-super-admin without a tenant claim
        using var ctx = Ctx(nullTenantNonSuperAdmin, db);
        var userService = BuildUserService(nullTenantNonSuperAdmin, ctx);

        var result = await userService.CreateAsync(
            new CreateUserRequest
            {
                Email = "forbidden@x.com",
                Password = "pw",
                DisplayName = "Forbidden",
                RoleId = roleId,
            }
        );

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Common.Forbidden, result.Message);

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        Assert.DoesNotContain(verify.Users.IgnoreQueryFilters(), u => u.Email == "forbidden@x.com");
    }

    [Fact]
    public async Task Delete_EndsMembership_KeepsIdentity()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        int targetUserId;
        Guid adminPublicId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var adminRole = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = workspaceA,
            };
            seed.Roles.Add(adminRole);
            await seed.SaveChangesAsync();

            var adminA = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "adminA@x.com",
                PasswordHash = "hash",
                DisplayName = "Admin A",
                RoleId = adminRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(adminA);

            var devRole = seed.Roles.Single(r => r.Id == roleId);
            var sharedUser = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "target@x.com",
                PasswordHash = "hash",
                DisplayName = "Target User",
                RoleId = devRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(sharedUser);
            await seed.SaveChangesAsync();

            TestSeed.Join(seed, adminA, workspaceA, adminRole);
            TestSeed.Join(seed, sharedUser, workspaceA, devRole);
            TestSeed.Join(seed, sharedUser, workspaceB, devRole);

            targetUserId = sharedUser.Id;
            adminPublicId = adminA.PublicId;
        }

        var caller = new FakeCurrentUser
        {
            Id = adminPublicId,
            TenantId = workspaceA,
            IsAdmin = true,
        };
        using var ctx = Ctx(caller, db);
        var userService = BuildUserService(caller, ctx);

        var result = await userService.DeleteAsync(targetUserId);
        Assert.True(result.IsSuccess, result.Message);

        using var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);

        // Membership in workspace A is ended
        var memA = await verify.WorkspaceMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == targetUserId && m.OwnerId == workspaceA);
        Assert.NotNull(memA);
        Assert.NotNull(memA!.LeftAt);
        Assert.Equal(MembershipEndReason.Removed, memA.LeftReason);
        Assert.False(memA.IsActive);

        // Membership in workspace B is untouched
        var memB = await verify.WorkspaceMemberships.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.UserId == targetUserId && m.OwnerId == workspaceB);
        Assert.NotNull(memB);
        Assert.Null(memB!.LeftAt);
        Assert.True(memB.IsActive);

        // Identity row is intact (never soft-deleted by membership delete)
        var identity = await verify.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == targetUserId);
        Assert.NotNull(identity);
        Assert.Null(identity!.DeletedAt);
    }

    [Fact]
    public async Task UpdateUser_Password_RefusedWhenIdentityHasOtherMemberships()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        int multiMemberUserId;
        int singleMemberUserId;
        Guid adminPublicId;
        const string initialHash = "initial-hash";

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var adminRole = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = workspaceA,
            };
            seed.Roles.Add(adminRole);
            await seed.SaveChangesAsync();

            var adminA = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "adminA@x.com",
                PasswordHash = "hash",
                DisplayName = "Admin A",
                RoleId = adminRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(adminA);

            var devRole = seed.Roles.Single(r => r.Id == roleId);
            var multiUser = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "multi@x.com",
                PasswordHash = initialHash,
                DisplayName = "Multi User",
                RoleId = devRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(multiUser);

            var singleUser = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "single@x.com",
                PasswordHash = initialHash,
                DisplayName = "Single User",
                RoleId = devRole.Id,
                OwnerId = workspaceA,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(singleUser);
            await seed.SaveChangesAsync();

            TestSeed.Join(seed, adminA, workspaceA, adminRole);
            TestSeed.Join(seed, multiUser, workspaceA, devRole);
            TestSeed.Join(seed, multiUser, workspaceB, devRole);
            TestSeed.Join(seed, singleUser, workspaceA, devRole);

            multiMemberUserId = multiUser.Id;
            singleMemberUserId = singleUser.Id;
            adminPublicId = adminA.PublicId;
        }

        var caller = new FakeCurrentUser
        {
            Id = adminPublicId,
            TenantId = workspaceA,
            IsAdmin = true,
        };
        using var ctx = Ctx(caller, db);
        var userService = BuildUserService(caller, ctx);

        // D6: Updating password of a user belonging to multiple workspaces is refused
        var refusedResult = await userService.UpdateAsync(
            multiMemberUserId,
            new UpdateUserRequest { Password = "new-password" }
        );
        Assert.False(refusedResult.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordManagedElsewhere, refusedResult.Message);

        using (var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var multi = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == multiMemberUserId);
            Assert.Equal(initialHash, multi.PasswordHash);
        }

        // Updating password of a single-membership user succeeds
        var allowedResult = await userService.UpdateAsync(
            singleMemberUserId,
            new UpdateUserRequest { Password = "new-password" }
        );
        Assert.True(allowedResult.IsSuccess, allowedResult.Message);

        using (var verify = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var single = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == singleMemberUserId);
            Assert.Equal("hashed:new-password", single.PasswordHash);
        }
    }
}
