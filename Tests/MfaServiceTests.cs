using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// R5-61 §6 — operator TOTP MFA: <see cref="TotpService"/> (RFC 6238, including the Appendix B
/// SHA-1 test vector), <see cref="MfaService"/> (enrol/verify/disable/recovery codes), and the
/// login-flow change in <see cref="AuthService"/> (mfa_required → POST /api/auth/mfa/verify).
/// Fixture mirrors <see cref="ChangeEmailTests"/> (InMemory + hand-rolled fakes; a REAL
/// <see cref="JwtTokenService"/> so the scoped mfa_pending token round-trips for real).
/// </summary>
public class MfaServiceTests
{
    // ── Shared fakes (same shapes as ChangeEmailTests) ──────────────────────────────────────────

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

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class NoopSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse DefaultBranding() => new()
        {
            ProductName = "Pointer",
            Tagline = string.Empty,
            PrimaryColor = "#2563eb",
            Urls = new Pointer.Application.DTOs.Branding.BrandingUrlsResponse { App = "https://app.pointer.test" },
            Assets = new Pointer.Application.DTOs.Branding.BrandingAssetsResponse(),
        };
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(Pointer.Application.DTOs.Branding.BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(DefaultBranding());
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    /// <summary>Records RecordFailureAsync/ResetAsync calls so lockout-on-failed-code behavior is
    /// directly assertable (AGENT-TASK deltas: "the lockout counter also counts failed TOTP codes").</summary>
    private sealed class CountingLoginAttemptLimiter : ILoginAttemptLimiter
    {
        public int Failures;
        public int Resets;
        private bool _locked;
        public void SetLocked(bool locked) => _locked = locked;
        public Task<bool> IsLockedAsync(string email) => Task.FromResult(_locked);
        public Task<int> GetRetryAfterSecondsAsync(string email) => Task.FromResult(_locked ? 60 : 0);
        public Task RecordFailureAsync(string email) { Failures++; return Task.CompletedTask; }
        public Task ResetAsync(string email) { Resets++; return Task.CompletedTask; }
    }

    /// <summary>A TimeProvider frozen at a fixed instant, for deterministic TOTP windows.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private const string SigningKey = "test-mfa-key-0123456789abcdef0123456789";

    private static JwtTokenService RealTokenService() =>
        new(Options.Create(new JwtOptions { SigningKey = SigningKey, Issuer = "pointer-api", LifetimeHours = 12, SelectionLifetimeMinutes = 5 }));

    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        IMfaService? mfa = null,
        ILoginAttemptLimiter? limiter = null
    )
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new IdentityHasher(),
            RealTokenService(),
            user,
            new NoopSettings(),
            new ResetTokenService(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:SigningKey"] = SigningKey })
                    .Build()),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            limiter ?? new FakeLoginAttemptLimiter(),
            new MembershipService(uow),
            new FakeAuditWriter(),
            mfa
        );
    }

    private static MfaService BuildMfaService(ICurrentUser user, AppDbContext ctx, ITotpService totp, IAuditWriter? audit = null) =>
        new(new UnitOfWork(ctx), user, new TestApiKeyProtector(), totp, audit);

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    private static (Role role, User user) SeedSuperAdmin(
        AppDbContext seed,
        string email = "root@t.com",
        string password = "pw-root",
        DateTime? totpEnabledAt = null,
        string? totpSecretEncrypted = null
    )
    {
        var role = new Role { Name = "Super Admin", IsSystem = true, IsSuperAdmin = true, GrantsAdmin = true, IsActive = true };
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
            TotpEnabledAt = totpEnabledAt,
            TotpSecret = totpSecretEncrypted,
        };
        seed.Users.Add(user);
        seed.SaveChanges();

        return (role, user);
    }

    // ── RFC 6238 Appendix B (SHA-1) + independent verification ──────────────────────────────────

    /// <summary>Independently reproduces the RFC 6238 pseudocode (not a call into TotpService) so the
    /// production implementation is checked against the spec text, not against itself.</summary>
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

    [Fact]
    public void TotpService_RFC6238AppendixB_TestVector()
    {
        // RFC 6238 Appendix B, SHA-1 row: secret = ASCII "12345678901234567890", Time = 59s ⇒
        // T = 0000000000000001 ⇒ the published (8-digit) TOTP is "94287082"; mod 1,000,000 (our
        // 6-digit truncation) that is "287082".
        var secretBase32 = TotpService.Base32Encode(Encoding.ASCII.GetBytes("12345678901234567890"));
        var svc = new TotpService(new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(59)));

        Assert.True(svc.ValidateCode(secretBase32, "287082"));
        Assert.False(svc.ValidateCode(secretBase32, "287083"));
    }

    [Fact]
    public void TotpService_GenerateAndValidate()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var svc = new TotpService(new FixedTimeProvider(now));

        var secret = svc.GenerateSecret();
        var key = TotpService.Base32Decode(secret);
        var expected = ComputeExpectedCode(key, now.ToUnixTimeSeconds() / 30);

        Assert.True(svc.ValidateCode(secret, expected));
    }

    [Fact]
    public void TotpService_RejectsWrongCode()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var svc = new TotpService(new FixedTimeProvider(now));
        var secret = svc.GenerateSecret();
        var key = TotpService.Base32Decode(secret);
        var correct = ComputeExpectedCode(key, now.ToUnixTimeSeconds() / 30);
        var wrong = correct == "000000" ? "111111" : "000000";

        Assert.False(svc.ValidateCode(secret, wrong));
    }

    [Fact]
    public void TotpService_AcceptsAdjacentTimeStep()
    {
        var mintedAt = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var mintSvc = new TotpService(new FixedTimeProvider(mintedAt));
        var secret = mintSvc.GenerateSecret();
        var key = TotpService.Base32Decode(secret);
        var codeAtMint = ComputeExpectedCode(key, mintedAt.ToUnixTimeSeconds() / 30);

        // One 30s step later — still inside the ±1 tolerance window.
        var oneStepLater = mintedAt.AddSeconds(30);
        var laterSvc = new TotpService(new FixedTimeProvider(oneStepLater));
        Assert.True(laterSvc.ValidateCode(secret, codeAtMint));

        // Three steps later — outside the window.
        var threeStepsLater = mintedAt.AddSeconds(90);
        var tooLateSvc = new TotpService(new FixedTimeProvider(threeStepsLater));
        Assert.False(tooLateSvc.ValidateCode(secret, codeAtMint));
    }

    [Fact]
    public void GenerateOtpAuthUrl_HasExpectedShape()
    {
        var svc = new TotpService();
        var url = svc.GenerateOtpAuthUrl("root@t.com", "ABCDEFGH");

        Assert.StartsWith("otpauth://totp/", url);
        Assert.Contains("secret=ABCDEFGH", url);
        Assert.Contains("issuer=Pointer", url);
        Assert.Contains("algorithm=SHA1", url);
        Assert.Contains("digits=6", url);
        Assert.Contains("period=30", url);
    }

    // ── MfaService: enrol / verify / disable / recovery codes ──────────────────────────────────

    [Fact]
    public async Task Enrol_Forbidden_ForNonSuperAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role { Name = "Engineer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var u = new User { Email = "dev@t.com", PasswordHash = "h:pw", DisplayName = "Dev", PublicId = Guid.NewGuid(), RoleId = role.Id, IsActive = true };
            seed.Users.Add(u);
            seed.SaveChanges();
            publicId = u.PublicId;
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = false };
        using var ctx = Ctx(caller, db);
        var result = await BuildMfaService(caller, ctx, new TotpService()).EnrolAsync();

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Common.Forbidden, result.Message);
    }

    [Fact]
    public async Task Enrol_Conflict_WhenAlreadyEnabled()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed, totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: "blob");
            publicId = u.PublicId;
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };
        using var ctx = Ctx(caller, db);
        var result = await BuildMfaService(caller, ctx, new TotpService()).EnrolAsync();

        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.Mfa.AlreadyEnabled, result.Message);
    }

    [Fact]
    public async Task Verify_EnablesMfa_ReturnsRecoveryCodes()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed);
            publicId = u.PublicId;
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var totp = new TotpService(new FixedTimeProvider(now));
        var audit = new FakeAuditWriter();

        string secret;
        using (var ctx = Ctx(caller, db))
        {
            var enrol = await BuildMfaService(caller, ctx, totp, audit).EnrolAsync();
            Assert.True(enrol.IsSuccess, enrol.Message);
            secret = enrol.Data!.Secret;
        }

        var code = ComputeExpectedCode(TotpService.Base32Decode(secret), now.ToUnixTimeSeconds() / 30);

        using (var ctx = Ctx(caller, db))
        {
            var verify = await BuildMfaService(caller, ctx, totp, audit).VerifyAsync(new MfaCodeRequest { Code = code });
            Assert.True(verify.IsSuccess, verify.Message);
            Assert.Equal(8, verify.Data!.RecoveryCodes.Count);
            Assert.Equal(8, verify.Data.RecoveryCodes.Distinct().Count());
        }

        using var check = Ctx(superAdmin, db);
        var row = check.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        Assert.NotNull(row.TotpEnabledAt);
        Assert.Equal(8, check.UserRecoveryCodes.IgnoreQueryFilters().Count(c => c.UserId == row.Id));
        Assert.Single(audit.Entries, e => e.Action == AuditActions.AuthMfaEnrolled);
    }

    [Fact]
    public async Task Verify_WrongCode_Fails_WritesChallengeFailed()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed);
            publicId = u.PublicId;
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };
        var totp = new TotpService(new FixedTimeProvider(DateTimeOffset.UtcNow));
        var audit = new FakeAuditWriter();

        using (var ctx = Ctx(caller, db))
            await BuildMfaService(caller, ctx, totp, audit).EnrolAsync();

        using var verifyCtx = Ctx(caller, db);
        var result = await BuildMfaService(caller, verifyCtx, totp, audit).VerifyAsync(new MfaCodeRequest { Code = "000000" });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Mfa.InvalidCode, result.Message);
        Assert.Single(audit.Entries, e => e.Action == AuditActions.AuthMfaChallengeFailed);

        using var check = Ctx(superAdmin, db);
        Assert.Null(check.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId).TotpEnabledAt);
    }

    [Fact]
    public async Task RecoveryCode_WorksOnce()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var protector = new TestApiKeyProtector();
        const string plainRecoveryCode = "ABCD1234";
        Guid publicId;
        int userId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed, totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: protector.Encrypt("IRRELEVANTSECRET"));
            publicId = u.PublicId;
            userId = u.Id;
            seed.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = userId, CodeHash = protector.Hash(plainRecoveryCode) });
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };

        User FetchUser(AppDbContext c) => c.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);

        using (var ctx = Ctx(caller, db))
        {
            var mfa = BuildMfaService(caller, ctx, new TotpService());
            var first = await mfa.ValidateCodeOrRecoveryAsync(FetchUser(ctx), plainRecoveryCode);
            Assert.True(first);
        }

        using (var ctx = Ctx(caller, db))
        {
            var mfa = BuildMfaService(caller, ctx, new TotpService());
            var second = await mfa.ValidateCodeOrRecoveryAsync(FetchUser(ctx), plainRecoveryCode);
            Assert.False(second);
        }

        using var check = Ctx(superAdmin, db);
        var code = check.UserRecoveryCodes.IgnoreQueryFilters().Single(c => c.UserId == userId);
        Assert.NotNull(code.UsedAt);
    }

    [Fact]
    public async Task Disable_ClearsMfa()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var totp = new TotpService(new FixedTimeProvider(now));
        var protector = new TestApiKeyProtector();
        var secret = totp.GenerateSecret();
        Guid publicId;
        int userId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed, totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: protector.Encrypt(secret));
            publicId = u.PublicId;
            userId = u.Id;
            seed.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = userId, CodeHash = protector.Hash("SOMECODE1") });
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };
        var code = ComputeExpectedCode(TotpService.Base32Decode(secret), now.ToUnixTimeSeconds() / 30);
        var audit = new FakeAuditWriter();

        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildMfaService(caller, ctx, totp, audit).DisableAsync(new MfaCodeRequest { Code = code });
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var row = check.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        Assert.Null(row.TotpSecret);
        Assert.Null(row.TotpEnabledAt);
        Assert.Empty(check.UserRecoveryCodes.IgnoreQueryFilters().Where(c => c.UserId == userId));
        Assert.Single(audit.Entries, e => e.Action == AuditActions.AuthMfaDisabled);
    }

    // ── Login flow: mfa_required / completes / unaffected paths ─────────────────────────────────

    [Fact]
    public async Task Login_SuperAdmin_WithoutMfa_NormalLogin()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = Ctx(superAdmin, db))
            SeedSuperAdmin(seed, email: "root2@t.com", password: "pw-root2");

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx).LoginAsync(new LoginRequest { Email = "root2@t.com", Password = "pw-root2" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
        Assert.NotNull(result.Data.Token);
        Assert.NotNull(result.Data.User);
        Assert.False(result.Data.User!.MfaEnabled);
    }

    [Fact]
    public async Task Login_SuperAdmin_WithMfa_ReturnsMfaRequired()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = Ctx(superAdmin, db))
            SeedSuperAdmin(seed, email: "root3@t.com", password: "pw-root3", totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: "blob");

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx).LoginAsync(new LoginRequest { Email = "root3@t.com", Password = "pw-root3" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("mfa_required", result.Data!.Status);
        Assert.NotNull(result.Data.Token);
        Assert.Null(result.Data.User);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Data.Token);
        Assert.Equal("mfa_pending", jwt.Claims.Single(c => c.Type == "scope").Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "tenant");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "role");
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "is_super_admin");
    }

    [Fact]
    public async Task Login_NonSuperAdmin_CompletelyUnaffected()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid ownerId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role { Name = "Engineer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            ownerId = Guid.NewGuid();
            var u = new User { Email = "member@t.com", PasswordHash = "h:pw-member", DisplayName = "Member", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = role.Id, IsActive = true };
            seed.Users.Add(u);
            seed.SaveChanges();
            TestSeed.Join(seed, u, ownerId, role);
        }

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx).LoginAsync(new LoginRequest { Email = "member@t.com", Password = "pw-member" });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
    }

    [Fact]
    public async Task MfaEndpoint_CompletesLogin_WithValidCode()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var totp = new TotpService(new FixedTimeProvider(now));
        var protector = new TestApiKeyProtector();
        var secret = totp.GenerateSecret();
        using (var seed = Ctx(superAdmin, db))
            SeedSuperAdmin(seed, email: "root4@t.com", password: "pw-root4", totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: protector.Encrypt(secret));

        // Step 1: password login → mfa_required + scoped token.
        string scopedToken;
        using (var ctx = Ctx(new FakeCurrentUser(), db))
        {
            var login = await BuildAuthService(new FakeCurrentUser(), ctx).LoginAsync(new LoginRequest { Email = "root4@t.com", Password = "pw-root4" });
            Assert.Equal("mfa_required", login.Data!.Status);
            scopedToken = login.Data.Token!;
        }

        var sub = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(scopedToken).Claims.Single(c => c.Type == "sub").Value);

        // Step 2: caller now presents the scoped token — simulated by a current-user whose claims
        // are exactly what HttpCurrentUser would extract from it (sub, scope=mfa_pending).
        var mfaCaller = new FakeCurrentUser { Id = sub, Scope = "mfa_pending" };
        var code = ComputeExpectedCode(TotpService.Base32Decode(secret), now.ToUnixTimeSeconds() / 30);
        var limiter = new CountingLoginAttemptLimiter();

        using var verifyCtx = Ctx(mfaCaller, db);
        var mfaService = BuildMfaService(mfaCaller, verifyCtx, totp);
        var authService = BuildAuthService(mfaCaller, verifyCtx, mfaService, limiter);
        var result = await authService.VerifyMfaLoginAsync(new MfaCodeRequest { Code = code });

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("ok", result.Data!.Status);
        Assert.NotNull(result.Data.Token);
        Assert.NotEqual(scopedToken, result.Data.Token);
        Assert.Equal("root4@t.com", result.Data.User!.Email);
        Assert.True(result.Data.User.IsSuperAdmin);
        Assert.Equal(1, limiter.Resets);

        var fullJwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Data.Token);
        Assert.Equal("true", fullJwt.Claims.Single(c => c.Type == "is_super_admin").Value);
        Assert.DoesNotContain(fullJwt.Claims, c => c.Type == "scope");
    }

    [Fact]
    public async Task MfaEndpoint_WrongCode_Fails_RecordsLockoutFailure()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var totp = new TotpService(new FixedTimeProvider(DateTimeOffset.UtcNow));
        var protector = new TestApiKeyProtector();
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed, email: "root5@t.com", password: "pw-root5", totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: protector.Encrypt(totp.GenerateSecret()));
            publicId = u.PublicId;
        }

        var mfaCaller = new FakeCurrentUser { Id = publicId, Scope = "mfa_pending" };
        var limiter = new CountingLoginAttemptLimiter();
        using var ctx = Ctx(mfaCaller, db);
        var mfaService = BuildMfaService(mfaCaller, ctx, totp);
        var authService = BuildAuthService(mfaCaller, ctx, mfaService, limiter);

        var result = await authService.VerifyMfaLoginAsync(new MfaCodeRequest { Code = "000000" });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Mfa.InvalidCode, result.Message);
        Assert.Equal(1, limiter.Failures);
        Assert.Equal(0, limiter.Resets);
    }

    [Fact]
    public async Task MfaEndpoint_WrongScope_Rejected()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var (_, u) = SeedSuperAdmin(seed, totpEnabledAt: DateTime.UtcNow, totpSecretEncrypted: "blob");
            publicId = u.PublicId;
        }

        // Holds a full/ordinary session (no scope claim at all) — must not be treated as mid-MFA.
        var ordinaryCaller = new FakeCurrentUser { Id = publicId, IsSuperAdmin = true };
        using var ctx = Ctx(ordinaryCaller, db);
        var result = await BuildAuthService(ordinaryCaller, ctx, BuildMfaService(ordinaryCaller, ctx, new TotpService()))
            .VerifyMfaLoginAsync(new MfaCodeRequest { Code = "123456" });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Mfa.InvalidPendingToken, result.Message);
    }

    /// <summary>§6 test 9 — an expired scope=mfa_pending token never reaches the controller: the
    /// ASP.NET JWT-bearer pipeline rejects it at signature/lifetime validation, before
    /// OnTokenValidated (and therefore before any controller/service code) ever runs.</summary>
    [Fact]
    public void MfaEndpoint_RejectsExpiredScopedToken()
    {
        var key = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)) { KeyId = "k0" };
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(key, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new System.Security.Claims.Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("stamp", Guid.NewGuid().ToString()),
            new System.Security.Claims.Claim("scope", "mfa_pending"),
        };
        var expired = new JwtSecurityToken(
            "pointer-api",
            "pointer-api",
            claims,
            expires: DateTime.UtcNow.AddMinutes(-10),
            signingCredentials: creds
        );
        var token = new JwtSecurityTokenHandler().WriteToken(expired);

        var tvp = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "pointer-api",
            ValidateAudience = true,
            ValidAudience = "pointer-api",
            ValidateLifetime = true,
            // The default 5-minute ClockSkew tolerance would otherwise let a token expired only a
            // minute ago still validate — zeroed here so this test proves REAL expiry is rejected
            // (mirrors the exact TokenValidationParameters shape AuthenticationExtensions.AddJwtAuth
            // configures, plus this one override).
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = new[] { Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256 },
            IssuerSigningKeys = new[] { key },
            ValidateIssuerSigningKey = true,
        };

        Assert.Throws<Microsoft.IdentityModel.Tokens.SecurityTokenExpiredException>(
            () => new JwtSecurityTokenHandler().ValidateToken(token, tvp, out _)
        );
    }
}
