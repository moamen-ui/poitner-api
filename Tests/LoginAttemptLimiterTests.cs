using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Pointer.API.Controllers;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class LoginAttemptLimiterTests
{
    private sealed class TestTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
    }

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string p) => "h:" + p;

        public bool Verify(string p, string h) => h == "h:" + p;
    }

    private sealed class FakeToken : ITokenService
    {
        public string Issue(User u, WorkspaceMembership? membership, int? keyScopes = null) =>
            "jwt-for-" + u.Email;
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
    }

    private sealed class NoopSettings : ISettingsService
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
            string html,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static BrandingResponse DefaultBranding() =>
            new()
            {
                ProductName = "Pointer",
                Tagline = string.Empty,
                PrimaryColor = "#2563eb",
                Urls = new BrandingUrlsResponse { App = "https://app.pointer.moamen.work" },
                Assets = new BrandingAssetsResponse(),
            };

        public Task<Result<BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Result<BrandingResponse>.Success(DefaultBranding()));

        public Task<Result<BrandingResponse>> UpdateAsync(
            BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(Result<BrandingResponse>.Success(DefaultBranding()));

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(DefaultBranding());
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public async Task TenFailures_LocksAccount()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var email = "user@example.com";

        for (var i = 1; i <= 9; i++)
        {
            await limiter.RecordFailureAsync(email);
            Assert.False(await limiter.IsLockedAsync(email));
            Assert.Equal(0, await limiter.GetRetryAfterSecondsAsync(email));
        }

        // 10th failure reaches the threshold -> locked
        await limiter.RecordFailureAsync(email);
        Assert.True(await limiter.IsLockedAsync(email));

        var retryAfter = await limiter.GetRetryAfterSecondsAsync(email);
        Assert.True(retryAfter > 0 && retryAfter <= 900);
    }

    [Fact]
    public async Task Success_ResetsCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var email = "user@example.com";

        for (var i = 0; i < 9; i++)
        {
            await limiter.RecordFailureAsync(email);
        }
        Assert.False(await limiter.IsLockedAsync(email));

        // Reset clears failure history
        await limiter.ResetAsync(email);
        Assert.False(await limiter.IsLockedAsync(email));

        // Another 9 failures still do not lock
        for (var i = 0; i < 9; i++)
        {
            await limiter.RecordFailureAsync(email);
        }
        Assert.False(await limiter.IsLockedAsync(email));
    }

    [Fact]
    public async Task WindowExpiry_UnlocksAccount()
    {
        var initialTime = new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(initialTime);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(
            cache,
            Options.Create(new LoginLockoutOptions { Threshold = 10, WindowMinutes = 15 }),
            timeProvider
        );
        var email = "user@example.com";

        for (var i = 0; i < 10; i++)
        {
            await limiter.RecordFailureAsync(email);
        }
        Assert.True(await limiter.IsLockedAsync(email));
        Assert.Equal(900, await limiter.GetRetryAfterSecondsAsync(email));

        // Advance 5 minutes -> 600s left, still locked
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await limiter.IsLockedAsync(email));
        Assert.Equal(600, await limiter.GetRetryAfterSecondsAsync(email));

        // Advance past the 15-minute window (10 more minutes + 1 second) -> unlocked
        timeProvider.Advance(TimeSpan.FromMinutes(10).Add(TimeSpan.FromSeconds(1)));
        Assert.False(await limiter.IsLockedAsync(email));
        Assert.Equal(0, await limiter.GetRetryAfterSecondsAsync(email));
    }

    [Fact]
    public async Task EmailNormalization_SharesCounter()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);

        // 5 failures on uppercase with trailing spaces
        for (var i = 0; i < 5; i++)
        {
            await limiter.RecordFailureAsync("  User@Example.Com ");
        }

        // 5 failures on canonical lowercase
        for (var i = 0; i < 5; i++)
        {
            await limiter.RecordFailureAsync("user@example.com");
        }

        // Total 10 -> locked regardless of case or whitespace
        Assert.True(await limiter.IsLockedAsync("USER@EXAMPLE.COM"));
        Assert.True(await limiter.IsLockedAsync(" user@example.com "));
    }

    /// <summary>
    /// GLM review M1/F2 — the cache key must be a fixed-size SHA-256 hex digest of the normalised
    /// e-mail, both so two normalisation-equivalent inputs collide onto the same entry (this was
    /// already true via string equality, and must remain true after hashing) and so the entry's
    /// key size can no longer grow with an attacker-supplied e-mail's length.
    /// </summary>
    private static string InvokeCacheKey(string? email)
    {
        var method = typeof(LoginAttemptLimiter).GetMethod(
            "CacheKey",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
        );
        Assert.NotNull(method);
        return (string)method!.Invoke(null, new object?[] { email })!;
    }

    [Fact]
    public void CacheKey_NormalisationVariants_MapToSameKey()
    {
        var canonical = InvokeCacheKey("user@example.com");
        var upperWithWhitespace = InvokeCacheKey("  User@Example.Com ");

        Assert.Equal(canonical, upperWithWhitespace);
    }

    [Theory]
    [InlineData("a@b.com")]
    [InlineData("user@example.com")]
    [InlineData("ghost-account-does-not-exist@pointer.work")]
    public void CacheKey_LengthIsConstant_RegardlessOfInputLength(string shortEmail)
    {
        var longEmail = new string('a', 10_000) + "@example.com";

        var shortKey = InvokeCacheKey(shortEmail);
        var longKey = InvokeCacheKey(longEmail);

        // "login-fail:" prefix + 64 hex chars (SHA-256 digest) regardless of input length.
        Assert.Equal("login-fail:".Length + 64, shortKey.Length);
        Assert.Equal(shortKey.Length, longKey.Length);
        Assert.NotEqual(shortKey, longKey);
    }

    [Fact]
    public async Task UnknownEmail_CountsFailuresAndLocks()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var unknown = "ghost-account-does-not-exist@pointer.work";

        for (var i = 0; i < 10; i++)
        {
            await limiter.RecordFailureAsync(unknown);
        }

        Assert.True(await limiter.IsLockedAsync(unknown));
    }

    [Fact]
    public async Task DifferentEmail_IsNotLocked()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);

        for (var i = 0; i < 10; i++)
        {
            await limiter.RecordFailureAsync("alice@example.com");
        }

        Assert.True(await limiter.IsLockedAsync("alice@example.com"));
        Assert.False(await limiter.IsLockedAsync("bob@example.com"));
    }

    [Fact]
    public async Task AuthService_TenWrongPasswords_11thReturns429WithLockedStatusAndRetryAfter()
    {
        var dbName = Guid.NewGuid().ToString();
        var caller = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = BuildContext(caller, dbName))
        {
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            seed.Users.Add(
                new User
                {
                    PublicId = Guid.NewGuid(),
                    Email = "alice@example.com",
                    PasswordHash = "h:Secret123",
                    ApprovalStatus = ApprovalStatus.Approved,
                    IsActive = true,
                    SecurityStamp = Guid.NewGuid(),
                    RoleId = role.Id,
                }
            );
            seed.Users.Add(
                new User
                {
                    PublicId = Guid.NewGuid(),
                    Email = "bob@example.com",
                    PasswordHash = "h:Secret456",
                    ApprovalStatus = ApprovalStatus.Approved,
                    IsActive = true,
                    SecurityStamp = Guid.NewGuid(),
                    RoleId = role.Id,
                }
            );
            seed.SaveChanges();
        }

        using var db = BuildContext(new FakeCurrentUser(), dbName);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var authService = new AuthService(
            new UnitOfWork(db),
            new IdentityHasher(),
            new FakeToken(),
            new FakeCurrentUser(),
            new NoopSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            limiter,
            new MembershipService(new UnitOfWork(db))
        );

        // Attempts 1 to 10 with wrong password return 400 InvalidCredentials
        for (var i = 1; i <= 10; i++)
        {
            var res = await authService.LoginAsync(
                new LoginRequest { Email = "alice@example.com", Password = "WrongPassword" }
            );
            Assert.False(res.IsSuccess);
            Assert.False(res.IsLocked);
            Assert.Equal(MessageKeys.Auth.InvalidCredentials, res.Message);
        }

        // 11th attempt returns 429 with Status = "locked" and RetryAfter
        var lockedRes = await authService.LoginAsync(
            new LoginRequest { Email = "alice@example.com", Password = "WrongPassword" }
        );
        Assert.False(lockedRes.IsSuccess);
        Assert.True(lockedRes.IsLocked);
        Assert.Equal(MessageKeys.Auth.TooManyAttempts, lockedRes.Message);
        Assert.NotNull(lockedRes.Data);
        Assert.Equal("locked", lockedRes.Data!.Status);
        Assert.NotNull(lockedRes.RetryAfterSeconds);
        Assert.True(lockedRes.RetryAfterSeconds > 0);

        // Bob from the same IP with a wrong password still gets 400 (not locked)
        var bobRes = await authService.LoginAsync(
            new LoginRequest { Email = "bob@example.com", Password = "WrongPassword" }
        );
        Assert.False(bobRes.IsSuccess);
        Assert.False(bobRes.IsLocked);
        Assert.Equal(MessageKeys.Auth.InvalidCredentials, bobRes.Message);
    }

    [Fact]
    public async Task AuthService_SuccessAfterNineFailures_ResetsCounter()
    {
        var dbName = Guid.NewGuid().ToString();
        var caller = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = BuildContext(caller, dbName))
        {
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var alice = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "alice@example.com",
                PasswordHash = "h:CorrectPassword",
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
                SecurityStamp = Guid.NewGuid(),
                RoleId = role.Id,
            };
            seed.Users.Add(alice);
            seed.SaveChanges();

            // DB-11a: login now resolves the workspace via a live WorkspaceMembership row — a bare
            // identity with no membership has "no workspace" and can never succeed.
            TestSeed.Join(seed, alice, Guid.NewGuid(), role);
        }

        using var db = BuildContext(new FakeCurrentUser(), dbName);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var authService = new AuthService(
            new UnitOfWork(db),
            new IdentityHasher(),
            new FakeToken(),
            new FakeCurrentUser(),
            new NoopSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            limiter,
            new MembershipService(new UnitOfWork(db))
        );

        // 9 wrong passwords
        for (var i = 1; i <= 9; i++)
        {
            var res = await authService.LoginAsync(
                new LoginRequest { Email = "alice@example.com", Password = "WrongPassword" }
            );
            Assert.False(res.IsSuccess);
            Assert.False(res.IsLocked);
        }

        // 10th attempt with correct password succeeds and resets the counter
        var successRes = await authService.LoginAsync(
            new LoginRequest { Email = "alice@example.com", Password = "CorrectPassword" }
        );
        Assert.True(successRes.IsSuccess);
        Assert.Equal("ok", successRes.Data!.Status);

        // Counter is reset: 9 more wrong passwords do not lock
        for (var i = 1; i <= 9; i++)
        {
            var res = await authService.LoginAsync(
                new LoginRequest { Email = "alice@example.com", Password = "WrongPassword" }
            );
            Assert.False(res.IsSuccess);
            Assert.False(res.IsLocked);
        }
    }

    [Fact]
    public async Task AuthService_UnknownEmail_CountsFailuresAndLocks()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = BuildContext(new FakeCurrentUser(), dbName);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var limiter = new LoginAttemptLimiter(cache);
        var authService = new AuthService(
            new UnitOfWork(db),
            new IdentityHasher(),
            new FakeToken(),
            new FakeCurrentUser(),
            new NoopSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            limiter,
            new MembershipService(new UnitOfWork(db))
        );

        // 10 attempts on an unknown email
        for (var i = 1; i <= 10; i++)
        {
            var res = await authService.LoginAsync(
                new LoginRequest { Email = "nobody@example.com", Password = "AnyPassword" }
            );
            Assert.False(res.IsSuccess);
            Assert.False(res.IsLocked);
            Assert.Equal(MessageKeys.Auth.InvalidCredentials, res.Message);
        }

        // 11th attempt is locked
        var lockedRes = await authService.LoginAsync(
            new LoginRequest { Email = "nobody@example.com", Password = "AnyPassword" }
        );
        Assert.False(lockedRes.IsSuccess);
        Assert.True(lockedRes.IsLocked);
        Assert.Equal(MessageKeys.Auth.TooManyAttempts, lockedRes.Message);
        Assert.Equal("locked", lockedRes.Data!.Status);
    }

    [Fact]
    public async Task AuthController_LockedResult_Returns429WithRetryAfterHeader()
    {
        var stubAuth = Substitute.For<IAuthService>();
        stubAuth
            .LoginAsync(Arg.Any<LoginRequest>())
            .Returns(
                Task.FromResult(
                    Result<LoginResponse>.Locked(
                        MessageKeys.Auth.TooManyAttempts,
                        new LoginResponse { Status = "locked" },
                        450
                    )
                )
            );

        var controller = new AuthController(
            stubAuth,
            new NoopSettings(),
            Substitute.For<IInviteService>(),
            Substitute.For<IDeviceLoginService>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var actionResult = await controller.Login(
            new LoginRequest { Email = "locked@example.com", Password = "password" }
        );

        var objectResult = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status429TooManyRequests, objectResult.StatusCode);

        var resultEnvelope = Assert.IsType<Result<LoginResponse>>(objectResult.Value);
        Assert.True(resultEnvelope.IsLocked);
        Assert.Equal("locked", resultEnvelope.Data!.Status);
        Assert.Equal(MessageKeys.Auth.TooManyAttempts, resultEnvelope.Message);

        var retryAfterHeader = controller.Response.Headers.RetryAfter.ToString();
        Assert.Equal("450", retryAfterHeader);
    }
}
