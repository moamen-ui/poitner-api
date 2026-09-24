using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pointer.API.Auth;
using Pointer.API.Controllers;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Production AUDIT GAP fix: POST /api/auth/login's choose-workspace (DB-11b picker — several live
/// memberships) and mfa_required (R5-61 — super admin with TOTP enrolled) branches return a 200
/// success envelope with NO session yet (the eventual auth.login.succeeded-equivalent row is
/// written later: SwitchWorkspaceAsync's own auth.workspace_switched, or VerifyMfaLoginAsync's
/// auth.login.succeeded). Before the fix, neither branch wrote ANY audit row, so
/// AuditCoverageFilter's "AUDIT GAP" fired for the 200 POST /api/auth/login response — and with
/// Audit:StrictCoverage=true (Development's default) the result was replaced with a 500. Fixed by
/// writing auth.login.credentials_verified in both branches. Fixture mirrors WorkspaceSwitchTests
/// (DB-11b) and MfaServiceTests (R5-61).
/// </summary>
public class AuthLoginIntermediateAuditTests
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
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;

        public bool Verify(string password, string hash) => hash == "h:" + password;
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
                    App = "https://app.pointer.test",
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

    /// <summary>A TimeProvider frozen at a fixed instant, for deterministic TOTP windows (same shape
    /// as MfaServiceTests' own).</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
            new ConfigurationBuilder().Build()
        );

    private const string SigningKey = "test-audit-gap-key-0123456789abcdef01234567";

    private static JwtTokenService RealTokenService() =>
        new(
            Options.Create(
                new JwtOptions
                {
                    SigningKey = SigningKey,
                    Issuer = "pointer-api",
                    LifetimeHours = 12,
                    SelectionLifetimeMinutes = 5,
                }
            )
        );

    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        IAuditWriter audit,
        IMfaService? mfa = null,
        ILoginAttemptLimiter? limiter = null
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
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            limiter ?? new FakeLoginAttemptLimiter(),
            new MembershipService(uow),
            audit,
            mfa: mfa
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

    private static Guid SeedMultiMembershipIdentity(
        string db,
        Guid workspaceA,
        Guid workspaceB,
        int roleId,
        string email,
        string password
    )
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var role = seed.Roles.Single(r => r.Id == roleId);
        var identity = new User
        {
            Email = email,
            PasswordHash = "h:" + password,
            DisplayName = "Picker",
            PublicId = Guid.NewGuid(),
            RoleId = role.Id,
            IsActive = true,
        };
        seed.Users.Add(identity);
        seed.SaveChanges();
        TestSeed.Join(seed, identity, workspaceA, role);
        TestSeed.Join(seed, identity, workspaceB, role);
        return identity.PublicId;
    }

    private static Guid SeedMfaEnrolledSuperAdmin(
        string db,
        string email,
        string password,
        string? totpSecretEncrypted = null
    )
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var role = new Role
        {
            Name = "Super Admin",
            IsSystem = true,
            IsSuperAdmin = true,
            GrantsAdmin = true,
            IsActive = true,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();
        var user = new User
        {
            Email = email,
            PasswordHash = "h:" + password,
            DisplayName = "Root",
            PublicId = Guid.NewGuid(),
            RoleId = role.Id,
            IsActive = true,
            TotpEnabledAt = DateTime.UtcNow,
            TotpSecret = totpSecretEncrypted ?? "blob",
        };
        seed.Users.Add(user);
        seed.SaveChanges();
        return user.PublicId;
    }

    /// <summary>Independently reproduces RFC 6238 (same helper as MfaServiceTests) so the code used
    /// to complete step 2 isn't just calling back into the production TOTP implementation.</summary>
    private static string ComputeExpectedCode(byte[] key, long counter)
    {
        var counterBytes = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);
        var hash = HMACSHA1.HashData(key, counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary =
            ((hash[offset] & 0x7f) << 24)
            | ((hash[offset + 1] & 0xff) << 16)
            | ((hash[offset + 2] & 0xff) << 8)
            | (hash[offset + 3] & 0xff);
        return ((uint)binary % 1_000_000).ToString("D6");
    }

    // ── 1. choose-workspace writes credentials_verified, not succeeded ──────────────────────────

    [Fact]
    public async Task Login_MultiMembershipPicker_WritesCredentialsVerified_NotSucceeded()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        var identityPublicId = SeedMultiMembershipIdentity(
            db,
            workspaceA,
            workspaceB,
            roleId,
            "picker-gap@x.com",
            "pw12345"
        );

        var anon = new FakeCurrentUser();
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx, audit)
            .LoginAsync(new LoginRequest { Email = "picker-gap@x.com", Password = "pw12345" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("choose-workspace", result.Data!.Status);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthLoginCredentialsVerified, entry.Action);
        Assert.Equal(AuditTargets.User, entry.TargetType);
        Assert.Equal(identityPublicId.ToString(), entry.TargetId);
        Assert.Null(entry.OwnerId);
        Assert.Equal(identityPublicId, entry.ActorUserIdOverride);
        Assert.Equal(AuditActorKind.User, entry.ActorKindOverride);
        Assert.Equal("password", entry.After?["source"]);
        Assert.Equal("choose-workspace", entry.After?["next"]);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.AuthLoginSucceeded);
    }

    // ── 2. mfa_required writes credentials_verified, not succeeded ──────────────────────────────

    [Fact]
    public async Task Login_SuperAdminMfaEnrolled_WritesCredentialsVerified_NotSucceeded()
    {
        var db = Guid.NewGuid().ToString();
        var identityPublicId = SeedMfaEnrolledSuperAdmin(db, "root-gap@t.com", "pw-root-gap");

        var anon = new FakeCurrentUser();
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx, audit)
            .LoginAsync(new LoginRequest { Email = "root-gap@t.com", Password = "pw-root-gap" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("mfa_required", result.Data!.Status);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthLoginCredentialsVerified, entry.Action);
        Assert.Equal(AuditTargets.User, entry.TargetType);
        Assert.Equal(identityPublicId.ToString(), entry.TargetId);
        Assert.Null(entry.OwnerId);
        Assert.Equal(identityPublicId, entry.ActorUserIdOverride);
        Assert.Equal(AuditActorKind.SuperAdmin, entry.ActorKindOverride);
        Assert.Equal("password", entry.After?["source"]);
        Assert.Equal("mfa", entry.After?["next"]);
        Assert.DoesNotContain(audit.Entries, e => e.Action == AuditActions.AuthLoginSucceeded);
    }

    // ── 3a. switch-workspace after the picker still writes its own final row ────────────────────

    [Fact]
    public async Task SwitchWorkspaceAsync_AfterPicker_StillWritesWorkspaceSwitched()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        var identityPublicId = SeedMultiMembershipIdentity(
            db,
            workspaceA,
            workspaceB,
            roleId,
            "picker-gap2@x.com",
            "pw12345"
        );

        var anon = new FakeCurrentUser();
        var loginAudit = new FakeAuditWriter();
        using (var loginCtx = Ctx(anon, db))
        {
            var loginResult = await BuildAuthService(anon, loginCtx, loginAudit)
                .LoginAsync(new LoginRequest { Email = "picker-gap2@x.com", Password = "pw12345" });
            Assert.Equal("choose-workspace", loginResult.Data!.Status);
        }
        // Exactly one row from the login itself, and it is NOT auth.login.succeeded (test 1 already
        // covers its shape in detail) — the picker step alone must never mint a session row.
        Assert.DoesNotContain(loginAudit.Entries, e => e.Action == AuditActions.AuthLoginSucceeded);

        var selectionCaller = new FakeCurrentUser
        {
            Id = identityPublicId,
            Scope = "select_workspace",
        };
        var switchAudit = new FakeAuditWriter();
        using var switchCtx = Ctx(selectionCaller, db);
        var switchResult = await BuildAuthService(selectionCaller, switchCtx, switchAudit)
            .SwitchWorkspaceAsync(workspaceA);

        Assert.True(switchResult.IsSuccess, switchResult.Message);
        Assert.Equal("ok", switchResult.Data!.Status);
        var entry = Assert.Single(switchAudit.Entries);
        Assert.Equal(AuditActions.AuthWorkspaceSwitched, entry.Action);
    }

    // ── 3b. mfa/verify after mfa_required still writes auth.login.succeeded ─────────────────────

    [Fact]
    public async Task VerifyMfaLoginAsync_AfterMfaRequired_StillWritesLoginSucceeded()
    {
        var db = Guid.NewGuid().ToString();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var totp = new TotpService(new FixedTimeProvider(now));
        var protector = new TestApiKeyProtector();
        var secret = totp.GenerateSecret();
        SeedMfaEnrolledSuperAdmin(db, "root-gap2@t.com", "pw-root-gap2", protector.Encrypt(secret));

        // Step 1: password login → mfa_required (its own credentials_verified row is proved by test 2).
        string scopedToken;
        using (var loginCtx = Ctx(new FakeCurrentUser(), db))
        {
            var login = await BuildAuthService(
                    new FakeCurrentUser(),
                    loginCtx,
                    new FakeAuditWriter()
                )
                .LoginAsync(
                    new LoginRequest { Email = "root-gap2@t.com", Password = "pw-root-gap2" }
                );
            Assert.Equal("mfa_required", login.Data!.Status);
            scopedToken = login.Data.Token!;
        }

        var sub = Guid.Parse(
            new JwtSecurityTokenHandler()
                .ReadJwtToken(scopedToken)
                .Claims.Single(c => c.Type == "sub")
                .Value
        );
        var mfaCaller = new FakeCurrentUser { Id = sub, Scope = "mfa_pending" };
        var code = ComputeExpectedCode(
            TotpService.Base32Decode(secret),
            now.ToUnixTimeSeconds() / 30
        );

        using var verifyCtx = Ctx(mfaCaller, db);
        var mfaService = new MfaService(
            new UnitOfWork(verifyCtx),
            mfaCaller,
            new TestApiKeyProtector(),
            totp,
            new FakeLoginAttemptLimiter(),
            new FakePasswordHasher()
        );
        var verifyAudit = new FakeAuditWriter();
        var authService = BuildAuthService(mfaCaller, verifyCtx, verifyAudit, mfaService);
        var result = await authService.VerifyMfaLoginAsync(new MfaCodeRequest { Code = code });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
        var entry = Assert.Single(verifyAudit.Entries);
        Assert.Equal(AuditActions.AuthLoginSucceeded, entry.Action);
    }

    // ── 4. controller/filter-level: StrictCoverage no longer 500s the picker response ───────────

    [Fact]
    public async Task AuditCoverageFilter_StrictMode_NoLongerReturns500ForChooseWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        SeedMultiMembershipIdentity(
            db,
            workspaceA,
            workspaceB,
            roleId,
            "picker-gap3@x.com",
            "pw12345"
        );

        // A real HttpContext shared between the REAL AuditWriter (stamps WrittenItemKey after a
        // successful write) and the REAL AuditCoverageFilter (reads it) — exactly like production's
        // single per-request HttpContext, so this proves the actual wiring, not a mocked shortcut.
        var http = new DefaultHttpContext();
        var accessor = new FakeHttpContextAccessor { HttpContext = http };
        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var realAudit = new Pointer.Infrastructure.Audit.AuditWriter(
            ctx,
            anon,
            accessor,
            new ConfigurationBuilder().Build(),
            NullLogger<Pointer.Infrastructure.Audit.AuditWriter>.Instance
        );

        var loginResult = await BuildAuthService(anon, ctx, realAudit)
            .LoginAsync(new LoginRequest { Email = "picker-gap3@x.com", Password = "pw12345" });
        Assert.True(loginResult.IsSuccess, loginResult.Message);
        Assert.Equal("choose-workspace", loginResult.Data!.Status);

        var method =
            typeof(AuthController).GetMethod(nameof(AuthController.Login))
            ?? throw new InvalidOperationException("AuthController.Login not found");
        var actionCtx = new ActionContext(
            http,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new ControllerActionDescriptor { MethodInfo = method }
        );
        var executingCtx = new ActionExecutingContext(
            actionCtx,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object()
        );
        var executedCtx = new ActionExecutedContext(
            executingCtx,
            executingCtx.Filters,
            new object()
        )
        {
            Result = new OkObjectResult(loginResult),
        };

        var strictConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Audit:StrictCoverage"] = "true" }
            )
            .Build();
        var filter = new AuditCoverageFilter(
            NullLogger<AuditCoverageFilter>.Instance,
            strictConfig
        );

        await filter.OnActionExecutionAsync(executingCtx, () => Task.FromResult(executedCtx));

        Assert.False(http.Items.ContainsKey("audit.gap"));
        Assert.IsType<OkObjectResult>(executedCtx.Result); // NOT replaced with a 500
    }
}
