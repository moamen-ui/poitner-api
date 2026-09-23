using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pointer.API.Extensions;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Validators;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11b: login workspace picker, <c>POST /api/auth/switch-workspace</c>, <c>/me</c> memberships,
/// projectKey auto-routing (D11). Fixture mirrors <see cref="WorkspaceMembershipTests"/> (DB-11a).
/// Uses the REAL <see cref="JwtTokenService"/> (not a fake) wherever a test needs to inspect actual
/// JWT claims — the selection token's shape (no tenant/role claims) and the full token's tenant/mstamp.
/// </summary>
public class WorkspaceSwitchTests
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
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
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

    /// <summary>Counts ResetAsync calls so tests can assert the lockout counter is (or isn't) reset —
    /// per the task's requirement that "choose-workspace" must NOT reset it until a full token is
    /// actually issued (mirrors the existing pending/rejected/disabled behaviour).</summary>
    private sealed class SpyLoginAttemptLimiter : ILoginAttemptLimiter
    {
        public int ResetCalls { get; private set; }

        public Task<bool> IsLockedAsync(string email) => Task.FromResult(false);

        public Task<int> GetRetryAfterSecondsAsync(string email) => Task.FromResult(0);

        public Task RecordFailureAsync(string email) => Task.CompletedTask;

        public Task ResetAsync(string email)
        {
            ResetCalls++;
            return Task.CompletedTask;
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
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
        ILoginAttemptLimiter? limiter = null,
        ITokenService? tokenService = null
    )
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new FakePasswordHasher(),
            tokenService ?? RealTokenService(),
            user,
            new FakeSettings(),
            new FakeResetTokenService(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            limiter ?? new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );
    }

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

    // ── 1. Two active memberships → choose-workspace + selection token ─────────────────────────

    [Fact]
    public async Task Login_TwoActiveMemberships_ReturnsChooseWorkspace_WithSelectionToken()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "picker@x.com",
                PasswordHash = "hashed:pw12345",
                DisplayName = "Picker",
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
        }

        var anon = new FakeCurrentUser();
        var auth = BuildAuthService(anon, Ctx(anon, db));

        var result = await auth.LoginAsync(
            new LoginRequest { Email = "picker@x.com", Password = "pw12345" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.ChooseWorkspace, result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("choose-workspace", result.Data!.Status);
        Assert.Null(result.Data.User);
        Assert.NotNull(result.Data.Workspaces);
        Assert.Equal(2, result.Data.Workspaces!.Count);
        Assert.Single(result.Data.Workspaces, w => w.IsHome);
        Assert.Contains(result.Data.Workspaces, w => w.WorkspaceId == workspaceA && w.IsHome);
        Assert.Contains(result.Data.Workspaces, w => w.WorkspaceId == workspaceB && !w.IsHome);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Data.Token);
        Assert.Equal("select_workspace", jwt.Claims.First(c => c.Type == "scope").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenant");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role_id");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "is_admin");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "is_super_admin");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "is_quick_access");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "mstamp");
    }

    // ── 2. projectKey routing ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_TwoMemberships_ProjectKeyPicksTheOwner()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "routed@x.com",
                PasswordHash = "hashed:pw12345",
                DisplayName = "Routed",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role);

            seed.Set<Project>()
                .Add(
                    new Project
                    {
                        Key = "proj-b",
                        Name = "Project B",
                        OwnerId = workspaceB,
                        CreatedAt = DateTime.UtcNow,
                    }
                );
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();

        // Known key that belongs to workspace B's project → "ok", tenant claim == B, no picker.
        var okResult = await BuildAuthService(anon, Ctx(anon, db))
            .LoginAsync(
                new LoginRequest
                {
                    Email = "routed@x.com",
                    Password = "pw12345",
                    ProjectKey = "proj-b",
                }
            );

        Assert.True(okResult.IsSuccess, okResult.Message);
        Assert.Equal("ok", okResult.Data!.Status);
        Assert.NotNull(okResult.Data.User);
        Assert.Equal(workspaceB, okResult.Data.User!.WorkspaceId);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(okResult.Data.Token);
        Assert.Equal(workspaceB.ToString(), jwt.Claims.First(c => c.Type == "tenant").Value);

        // Unknown key → falls through to the picker, same as an absent key.
        var pickerResult = await BuildAuthService(anon, Ctx(anon, db))
            .LoginAsync(
                new LoginRequest
                {
                    Email = "routed@x.com",
                    Password = "pw12345",
                    ProjectKey = "no-such-key",
                }
            );

        Assert.False(pickerResult.IsSuccess);
        Assert.Equal("choose-workspace", pickerResult.Data!.Status);
    }

    // ── 3. switch-workspace ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_NonMember_Forbidden_NoToken()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "onlya@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Only A",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            identityPublicId = identity.PublicId;
        }

        var caller = new FakeCurrentUser { Id = identityPublicId, TenantId = workspaceA };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.SwitchWorkspaceAsync(workspaceB);

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Auth.NotAMember, result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Switch_InactiveMembership_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "disabledinb@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Disabled In B",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role, isActive: false);
            identityPublicId = identity.PublicId;
        }

        var caller = new FakeCurrentUser { Id = identityPublicId, TenantId = workspaceA };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.SwitchWorkspaceAsync(workspaceB);

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Auth.NotAMember, result.Message);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task Switch_Member_Ok_TenantAndMstampMatch()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityPublicId;
        Guid membershipBStamp;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "switcher@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Switcher",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            var membershipB = TestSeed.Join(seed, identity, workspaceB, role);
            identityPublicId = identity.PublicId;
            membershipBStamp = membershipB.SecurityStamp;
        }

        var caller = new FakeCurrentUser { Id = identityPublicId, TenantId = workspaceA };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.SwitchWorkspaceAsync(workspaceB);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
        Assert.NotNull(result.Data.User);
        Assert.Equal(workspaceB, result.Data.User!.WorkspaceId);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Data.Token);
        Assert.Equal(workspaceB.ToString(), jwt.Claims.First(c => c.Type == "tenant").Value);
        Assert.Equal(membershipBStamp.ToString(), jwt.Claims.First(c => c.Type == "mstamp").Value);
    }

    [Fact]
    public async Task SwitchWorkspace_SuperAdmin_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        Guid superAdminPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var superRole = new Role
            {
                Name = "Super Admin",
                GrantsAdmin = true,
                IsSuperAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(superRole);
            seed.SaveChanges();

            var identity = new User
            {
                Email = "super@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Super",
                PublicId = Guid.NewGuid(),
                RoleId = superRole.Id,
                OwnerId = null,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            superAdminPublicId = identity.PublicId;
        }

        var caller = new FakeCurrentUser { Id = superAdminPublicId, IsSuperAdmin = true };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.SwitchWorkspaceAsync(Guid.NewGuid());

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Common.Forbidden, result.Message);
    }

    // ── 4. No live memberships at all ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_NoLiveMemberships_ReturnsNoWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        var (_, _, roleId) = SeedTwoWorkspaces(db);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            // Identity with NO WorkspaceMembership row at all (e.g. every membership was hard-ended).
            var identity = new User
            {
                Email = "nowhere@x.com",
                PasswordHash = "hashed:pw12345",
                DisplayName = "Nowhere",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = null,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();
        var auth = BuildAuthService(anon, Ctx(anon, db));

        var result = await auth.LoginAsync(
            new LoginRequest { Email = "nowhere@x.com", Password = "pw12345" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.NoWorkspace, result.Message);
        Assert.Equal("no-workspace", result.Data!.Status);
        Assert.Null(result.Data.Token);
        Assert.Null(result.Data.User);
    }

    // ── 5. Selection-token scope fence (GLM A6): exact path, no prefix/sub-route match ──────────

    [Theory]
    [InlineData("/api/auth/switch-workspace", true)]
    [InlineData("/API/Auth/Switch-Workspace", true)]
    [InlineData("/api/auth/switch-workspace/", false)]
    [InlineData("/api/auth/switch-workspace/anything", false)]
    [InlineData("/api/auth/switch-workspacex", false)]
    [InlineData("/api/comments", false)]
    [InlineData("", false)]
    public void SelectionToken_RejectedOutsideSwitchEndpoint(string path, bool expected)
    {
        Assert.Equal(
            expected,
            SelectionScopeFence.Allows(
                new Microsoft.AspNetCore.Http.PathString(path == "" ? null : path)
            )
        );
    }

    // ── 6. /me lists memberships and current workspace id ──────────────────────────────────────

    [Fact]
    public async Task Me_ListsMemberships_AndCurrentWorkspaceId()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "me@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Me",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role);
            identityPublicId = identity.PublicId;
        }

        var caller = new FakeCurrentUser { Id = identityPublicId, TenantId = workspaceA };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.MeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(workspaceA, result.Data!.WorkspaceId);
        Assert.Equal(2, result.Data.Workspaces.Count);
        Assert.Contains(result.Data.Workspaces, w => w.WorkspaceId == workspaceA && w.IsHome);
        Assert.Contains(result.Data.Workspaces, w => w.WorkspaceId == workspaceB && !w.IsHome);
    }

    [Fact]
    public async Task Me_SuperAdmin_HasNullWorkspaceId_AndEmptyWorkspaces()
    {
        var db = Guid.NewGuid().ToString();
        Guid superAdminPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var superRole = new Role
            {
                Name = "Super Admin",
                GrantsAdmin = true,
                IsSuperAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(superRole);
            seed.SaveChanges();

            var identity = new User
            {
                Email = "super2@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Super2",
                PublicId = Guid.NewGuid(),
                RoleId = superRole.Id,
                OwnerId = null,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            superAdminPublicId = identity.PublicId;
        }

        var caller = new FakeCurrentUser { Id = superAdminPublicId, IsSuperAdmin = true };
        var auth = BuildAuthService(caller, Ctx(caller, db));

        var result = await auth.MeAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(result.Data!.WorkspaceId);
        Assert.Empty(result.Data.Workspaces);
    }

    // ── 7. Validators ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchWorkspaceValidator_RejectsEmptyGuid()
    {
        var validator = new SwitchWorkspaceValidator();
        var result = validator.Validate(new SwitchWorkspaceRequest { WorkspaceId = Guid.Empty });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void SwitchWorkspaceValidator_AcceptsNonEmptyGuid()
    {
        var validator = new SwitchWorkspaceValidator();
        var result = validator.Validate(
            new SwitchWorkspaceRequest { WorkspaceId = Guid.NewGuid() }
        );
        Assert.True(result.IsValid);
    }

    // ── Lockout interplay (task directive, not explicitly in the doc's §6 list): the picker must
    // NOT reset the per-e-mail lockout counter — only a full token issuance does. ────────────────

    [Fact]
    public async Task Login_ChooseWorkspace_DoesNotResetLockout_SwitchWorkspace_DoesReset()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "lockout@x.com",
                PasswordHash = "hashed:pw12345",
                DisplayName = "Lockout",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role);
            identityPublicId = identity.PublicId;
        }

        var spy = new SpyLoginAttemptLimiter();

        var anon = new FakeCurrentUser();
        var loginResult = await BuildAuthService(anon, Ctx(anon, db), limiter: spy)
            .LoginAsync(new LoginRequest { Email = "lockout@x.com", Password = "pw12345" });

        Assert.Equal("choose-workspace", loginResult.Data!.Status);
        Assert.Equal(0, spy.ResetCalls);

        var caller = new FakeCurrentUser { Id = identityPublicId, TenantId = workspaceA };
        var switchResult = await BuildAuthService(caller, Ctx(caller, db), limiter: spy)
            .SwitchWorkspaceAsync(workspaceB);

        Assert.True(switchResult.IsSuccess, switchResult.Message);
        Assert.Equal(1, spy.ResetCalls);
    }
}
