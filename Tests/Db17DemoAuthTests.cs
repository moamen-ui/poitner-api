using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-17 §3.3 (Opus #2): an expired demo must never mint a fresh token — at login, at
/// switch-workspace, at quick-access redemption or at API-key login. Fixture mirrors
/// <see cref="WorkspaceSwitchTests"/>; uses a lightweight fake token service (claim shape is not
/// under test here — that's WorkspaceSwitchTests' job).
/// </summary>
public class Db17DemoAuthTests
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

    /// <summary>Seeds a "Workspace Admin" role, an identity and its live membership in a fresh
    /// workspace with the given demo state.</summary>
    private static (User User, Guid WorkspaceId, Role Role) SeedDemoAdmin(
        AppDbContext db,
        DateTime? demoExpiresAt,
        string password = "secret123"
    )
    {
        var workspaceId = Guid.NewGuid();
        var role = db.Roles.FirstOrDefault(r => r.Name == "Workspace Admin");
        if (role == null)
        {
            role = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsActive = true,
                OwnerId = null,
            };
            db.Roles.Add(role);
            db.SaveChanges();
        }

        var user = new User
        {
            PublicId = Guid.NewGuid(),
            Email = $"demo-{Guid.NewGuid():N}@demo.pointer",
            PasswordHash = "hashed:" + password,
            DisplayName = "Demo Admin",
            RoleId = role.Id,
            Role = role,
            IsActive = true,
            IsDemo = true,
        };
        db.Users.Add(user);
        db.SaveChanges();

        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = demoExpiresAt,
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

        return (user, workspaceId, role);
    }

    // ── (a) LoginAsync ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_ExpiredDemo_Refused_DemoExpired()
    {
        var db = Guid.NewGuid().ToString();
        Guid identityPublicId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, _, _) = SeedDemoAdmin(seed, DateTime.UtcNow.AddHours(-1));
            identityPublicId = user.PublicId;
        }

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var svc = BuildAuthService(new FakeCurrentUser(), ctx);
        var user2 = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == identityPublicId);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = user2.Email, Password = "secret123" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }

    [Fact]
    public async Task Login_LiveDemo_Ok_MeCarriesDemoExpiresAt_AndCanExtend()
    {
        var db = Guid.NewGuid().ToString();
        var expiresAt = DateTime.UtcNow.AddHours(20);
        string email;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, _, _) = SeedDemoAdmin(seed, expiresAt);
            email = user.Email;
        }

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var svc = BuildAuthService(new FakeCurrentUser(), ctx);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = email, Password = "secret123" }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
        Assert.NotNull(result.Data.User!.DemoExpiresAt);
        Assert.True(result.Data.User.DemoCanExtend);
    }

    [Fact]
    public async Task Login_ConvertedWorkspace_Ok_NoDemoFields()
    {
        var db = Guid.NewGuid().ToString();
        string email;
        Guid workspaceId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, wsId, _) = SeedDemoAdmin(seed, null);
            user.IsDemo = false;
            email = user.Email;
            workspaceId = wsId;
            var ws = seed.Workspaces.Single(w => w.Id == wsId);
            ws.DemoConvertedAt = DateTime.UtcNow;
            seed.SaveChanges();
        }

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var svc = BuildAuthService(new FakeCurrentUser(), ctx);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = email, Password = "secret123" }
        );

        Assert.True(result.IsSuccess, result.Message);
        Assert.Null(result.Data!.User!.DemoExpiresAt);
        Assert.False(result.Data.User.DemoCanExtend);
        _ = workspaceId;
    }

    // ── (b) SwitchWorkspaceAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Switch_ExpiredDemo_Refused_ButOtherWorkspaceStillWorks()
    {
        var db = Guid.NewGuid().ToString();
        Guid publicId;
        Guid workspaceA;
        Guid workspaceB;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, wsB, role) = SeedDemoAdmin(seed, DateTime.UtcNow.AddHours(-1));
            publicId = user.PublicId;
            workspaceB = wsB;

            workspaceA = Guid.NewGuid();
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = workspaceA,
                    Name = "Real Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = workspaceA,
                }
            );
            seed.WorkspaceMemberships.Add(
                new WorkspaceMembership
                {
                    UserId = user.Id,
                    OwnerId = workspaceA,
                    RoleId = role.Id,
                    Role = role,
                    IsActive = true,
                    ApprovalStatus = ApprovalStatus.Approved,
                    JoinedAt = DateTime.UtcNow,
                }
            );
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = publicId };
        using var ctx = Ctx(caller, db);
        var svc = BuildAuthService(caller, ctx);

        var refused = await svc.SwitchWorkspaceAsync(workspaceB);
        Assert.False(refused.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, refused.Message);

        var ok = await svc.SwitchWorkspaceAsync(workspaceA);
        Assert.True(ok.IsSuccess, ok.Message);
        Assert.Equal("ok", ok.Data!.Status);
    }

    // ── (c) LoginWithInviteAsync (quick-access) ─────────────────────────────────────────────

    [Fact]
    public async Task QuickAccessLogin_ExpiredDemo_Refused()
    {
        var db = Guid.NewGuid().ToString();
        var token = QuickAccessTokenGenerator.NewToken();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, workspaceId, role) = SeedDemoAdmin(seed, DateTime.UtcNow.AddHours(-1));
            var qaRole = new Role
            {
                Name = "Quick Access",
                GrantsAdmin = false,
                QuickAccess = true,
                IsActive = true,
                OwnerId = null,
            };
            seed.Roles.Add(qaRole);
            seed.SaveChanges();

            var membership = seed.WorkspaceMemberships.Single(m =>
                m.UserId == user.Id && m.OwnerId == workspaceId
            );
            membership.RoleId = qaRole.Id;
            membership.Role = qaRole;
            seed.SaveChanges();

            seed.Set<QuickAccessLink>()
                .Add(
                    new QuickAccessLink
                    {
                        OwnerId = workspaceId,
                        UserId = user.PublicId,
                        ProjectId = 0,
                        InviteId = 0,
                        TokenHash = QuickAccessTokenGenerator.Hash(token),
                        ExpiresAt = DateTime.UtcNow.AddDays(1),
                        MaxUses = 0,
                    }
                );
            seed.SaveChanges();
        }

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var svc = BuildAuthService(new FakeCurrentUser(), ctx);

        var result = await svc.LoginWithInviteAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }

    // ── (d) LoginWithApiKeyAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyLogin_ExpiredDemo_Refused()
    {
        var db = Guid.NewGuid().ToString();
        string rawKey;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var (user, workspaceId, _) = SeedDemoAdmin(seed, DateTime.UtcNow.AddHours(-1));
            var keys = new ApiKeyService(new UnitOfWork(seed), new TestApiKeyProtector());
            var minted = await keys.GetOrCreateAsync(user.PublicId, workspaceId);
            Assert.True(minted.Found);
            rawKey = minted.RawKey!;
        }

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var svc = BuildAuthService(new FakeCurrentUser(), ctx);

        var result = await svc.LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = rawKey });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }
}
