using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Demo;
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
/// Unit tests for DemoService.UpgradeAsync — in-place demo-to-permanent conversion.
/// Uses the in-memory EF provider (same pattern as TenantQueryFilterTests) with the real
/// UnitOfWork/Repository; password hasher + token service are lightweight fakes.
/// </summary>
public class DemoUpgradeTests
{
    // ---------------------------------------------------------------------------
    // Fakes
    // ---------------------------------------------------------------------------

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true; // super-admin so query filters never hide seeded rows
        public Guid? TenantId { get; set; }
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;
        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    /// <summary>Records the last user a token was issued for, so tests can assert post-upgrade state.</summary>
    private sealed class RecordingTokenService : ITokenService
    {
        public User? IssuedFor { get; private set; }
        public string Issue(User user)
        {
            IssuedFor = user;
            return "token-for-" + user.Email;
        }
    }

    // -----------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------

    private static (DemoService svc, AppDbContext db, RecordingTokenService tokens) Build(string dbName)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new AppDbContext(opts, new FakeCurrentUser());
        var uow = new UnitOfWork(db);
        var tokens = new RecordingTokenService();
        // EmailService + SettingsService are unused by UpgradeAsync — pass no-op stubs.
        var email = new NoopEmailService();
        var settings = new NoopSettingsService();
        var svc = new DemoService(uow, new FakePasswordHasher(), tokens, email, settings);
        return (svc, db, tokens);
    }

    private sealed class NoopEmailService : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class NoopSettingsService : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static User SeedDemoUser(AppDbContext db, Guid? publicId = null, DateTime? expiresAt = null)
    {
        var pid = publicId ?? Guid.NewGuid();
        var role = new Role { Id = 2, Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        var user = new User
        {
            PublicId = pid,
            Email = $"demo-{pid.ToString("N")[..8]}@demo.pointer",
            PasswordHash = "hash:olddemo",
            DisplayName = "Demo User",
            RoleId = role.Id,
            Role = role,
            OwnerId = pid,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(24),
            RecipientEmail = "real@user.com",
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static UpgradeDemoRequest ValidRequest(string? email = null) => new()
    {
        Email = email ?? "newperm@user.com",
        Password = "supersecret",
        DisplayName = "Real Name",
    };

    // -----------------------------------------------------------------
    // 1. Happy path
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_demo_user_with_valid_request_succeeds_and_flips_isDemo()
    {
        var (svc, db, tokens) = Build(nameof(Upgrade_demo_user_with_valid_request_succeeds_and_flips_isDemo));
        var demo = SeedDemoUser(db);

        var result = await svc.UpgradeAsync(demo.PublicId, ValidRequest("permanent@user.com"));

        Assert.True(result.IsSuccess);
        Assert.Equal(MessageKeys.Demo.UpgradeSuccess, result.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("token-for-permanent@user.com", result.Data.Token);
        Assert.Equal("permanent@user.com", result.Data.User.Email);

        // Entity mutated in place.
        var updated = db.Users.Single(u => u.PublicId == demo.PublicId);
        Assert.False(updated.IsDemo);
        Assert.Null(updated.ExpiresAt);
        Assert.False(updated.DemoExtended);
        Assert.Null(updated.DemoCommentCapOverride);
        Assert.Null(updated.DemoTtlHoursOverride);
        Assert.Equal("permanent@user.com", updated.Email);
        Assert.Equal("hash:supersecret", updated.PasswordHash);
        Assert.Equal("Real Name", updated.DisplayName);
        Assert.Null(updated.RecipientEmail);

        // Token issued with the real (post-upgrade) email.
        Assert.NotNull(tokens.IssuedFor);
        Assert.Equal("permanent@user.com", tokens.IssuedFor!.Email);
        Assert.False(tokens.IssuedFor.IsDemo);
    }

    // -----------------------------------------------------------------
    // 2. Caller is not a demo user → Forbidden
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_non_demo_user_returns_forbidden()
    {
        var (svc, db, _) = Build(nameof(Upgrade_non_demo_user_returns_forbidden));
        var pid = Guid.NewGuid();
        var role = new Role { Id = 1, Name = "Workspace Admin", OwnerId = null };
        db.Roles.Add(role);
        db.Users.Add(new User
        {
            PublicId = pid,
            Email = "normal@user.com",
            PasswordHash = "x",
            DisplayName = "Normal",
            RoleId = role.Id,
            Role = role,
            OwnerId = pid,
            IsDemo = false,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        });
        db.SaveChanges();

        var result = await svc.UpgradeAsync(pid, ValidRequest());

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Demo.NotDemoUser, result.Message);
    }

    // -----------------------------------------------------------------
    // 3. Expired demo → Failure with DemoExpired
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_expired_demo_returns_failure_demo_expired()
    {
        var (svc, db, _) = Build(nameof(Upgrade_expired_demo_returns_failure_demo_expired));
        var demo = SeedDemoUser(db, expiresAt: DateTime.UtcNow.AddHours(-1));

        var result = await svc.UpgradeAsync(demo.PublicId, ValidRequest());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsForbidden);
        Assert.Equal(MessageKeys.Demo.DemoExpired, result.Message);
    }

    // -----------------------------------------------------------------
    // 4. Email taken by another user → Conflict
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_with_email_taken_by_another_user_returns_conflict()
    {
        var (svc, db, _) = Build(nameof(Upgrade_with_email_taken_by_another_user_returns_conflict));
        var demo = SeedDemoUser(db);
        var otherOwner = Guid.NewGuid();
        var role = db.Roles.First();
        db.Users.Add(new User
        {
            PublicId = otherOwner,
            Email = "shared@user.com",
            PasswordHash = "x",
            DisplayName = "Other",
            RoleId = role.Id,
            Role = role,
            OwnerId = otherOwner,
            IsDemo = false,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        });
        db.SaveChanges();

        var result = await svc.UpgradeAsync(demo.PublicId, ValidRequest("shared@user.com"));

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.Demo.EmailTaken, result.Message);
    }

    // -----------------------------------------------------------------
    // 5. Password too short → validator failure
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_with_short_password_returns_validator_failure()
    {
        var (svc, db, _) = Build(nameof(Upgrade_with_short_password_returns_validator_failure));
        var demo = SeedDemoUser(db);

        var request = ValidRequest();
        request.Password = "short";

        var result = await svc.UpgradeAsync(demo.PublicId, request);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsForbidden);
        Assert.False(result.IsConflict);
        Assert.Equal(MessageKeys.User.PasswordWeak, result.Message);
    }

    // -----------------------------------------------------------------
    // 6. Empty email → validator failure
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_with_empty_email_returns_validator_failure()
    {
        var (svc, db, _) = Build(nameof(Upgrade_with_empty_email_returns_validator_failure));
        var demo = SeedDemoUser(db);

        var request = ValidRequest();
        request.Email = "";

        var result = await svc.UpgradeAsync(demo.PublicId, request);

        Assert.False(result.IsSuccess);
        Assert.False(result.IsForbidden);
        Assert.False(result.IsConflict);
    }

    // -----------------------------------------------------------------
    // Bonus: caller not found → NotFound
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upgrade_with_unknown_caller_returns_not_found()
    {
        var (svc, _, _) = Build(nameof(Upgrade_with_unknown_caller_returns_not_found));

        var result = await svc.UpgradeAsync(Guid.NewGuid(), ValidRequest());

        Assert.False(result.IsSuccess);
        Assert.True(result.IsNotFound);
        Assert.Equal(MessageKeys.User.NotFound, result.Message);
    }
}
