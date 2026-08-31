using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Long-lived personal API key: get-or-create is idempotent (same key on repeat calls),
/// regenerate replaces and immediately invalidates the old one, and LoginWithApiKeyAsync exchanges
/// a valid key for a normal JWT with the exact same response shape as password login.
/// </summary>
public class ApiKeyAuthTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
    }

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string p) => "h:" + p;
        public bool Verify(string p, string h) => h == "h:" + p;
    }

    private sealed class FakeToken : ITokenService { public string Issue(User u) => "jwt-for-" + u.Email; }

    private sealed class FakeReset : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";
        public bool TryValidate(string token, out Guid id, out Guid stamp) { id = Guid.Empty; stamp = Guid.Empty; return false; }
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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string html, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse DefaultBranding() => new()
        {
            ProductName = "Pointer",
            Tagline = string.Empty,
            PrimaryColor = "#2563eb",
            Urls = new Pointer.Application.DTOs.Branding.BrandingUrlsResponse { App = "https://app.pointer.moamen.work" },
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static AuthService BuildAuthService(AppDbContext db, ICurrentUser user) =>
        new(new UnitOfWork(db), new IdentityHasher(), new FakeToken(), user, new NoopSettings(), new FakeReset(), new NoopEmail(), new NoopBrandingService());

    private static ProfileService BuildProfileService(AppDbContext db) => new(new UnitOfWork(db));

    private static Guid SeedUser(string dbName, out Guid tenant)
    {
        tenant = Guid.NewGuid();
        var publicId = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var role = new Role { Name = "Developer", IsActive = true, OwnerId = null };
        seed.Roles.Add(role);
        seed.SaveChanges();
        seed.Users.Add(new User
        {
            PublicId = publicId,
            Email = "dev@example.com",
            PasswordHash = "h:pw",
            DisplayName = "Dev",
            RoleId = role.Id,
            OwnerId = tenant,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        });
        seed.SaveChanges();
        return publicId;
    }

    [Fact]
    public async Task GetOrCreateApiKey_FirstCall_GeneratesAndPersists()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var svc = BuildProfileService(BuildContext(new FakeCurrentUser { Id = publicId, TenantId = tenant }, db));

        var result = await svc.GetOrCreateApiKeyAsync(publicId);

        Assert.True(result.IsSuccess);
        Assert.StartsWith("ptr_", result.Data!.ApiKey);
    }

    [Fact]
    public async Task GetOrCreateApiKey_SecondCall_ReturnsSameKey()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var svc1 = BuildProfileService(BuildContext(new FakeCurrentUser { Id = publicId, TenantId = tenant }, db));
        var first = await svc1.GetOrCreateApiKeyAsync(publicId);

        var svc2 = BuildProfileService(BuildContext(new FakeCurrentUser { Id = publicId, TenantId = tenant }, db));
        var second = await svc2.GetOrCreateApiKeyAsync(publicId);

        Assert.Equal(first.Data!.ApiKey, second.Data!.ApiKey);
    }

    [Fact]
    public async Task RegenerateApiKey_ReplacesAndInvalidatesTheOldOne()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var user = new FakeCurrentUser { Id = publicId, TenantId = tenant };

        var first = await BuildProfileService(BuildContext(user, db)).GetOrCreateApiKeyAsync(publicId);
        var regenerated = await BuildProfileService(BuildContext(user, db)).RegenerateApiKeyAsync(publicId);

        Assert.NotEqual(first.Data!.ApiKey, regenerated.Data!.ApiKey);

        // The old key must no longer authenticate.
        var loginWithOld = await BuildAuthService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = first.Data!.ApiKey });
        Assert.False(loginWithOld.IsSuccess);
    }

    [Fact]
    public async Task LoginWithApiKey_ValidKey_ReturnsJwtAndUser()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var key = await BuildProfileService(BuildContext(new FakeCurrentUser { Id = publicId, TenantId = tenant }, db))
            .GetOrCreateApiKeyAsync(publicId);

        var result = await BuildAuthService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = key.Data!.ApiKey });

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Data!.Status);
        Assert.False(string.IsNullOrEmpty(result.Data!.Token));
        Assert.Equal("dev@example.com", result.Data!.User!.Email);
    }

    [Fact]
    public async Task LoginWithApiKey_InvalidKey_Fails()
    {
        var db = Guid.NewGuid().ToString();
        SeedUser(db, out _);

        var result = await BuildAuthService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = "ptr_does_not_exist" });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task LoginWithApiKey_DisabledUser_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var key = await BuildProfileService(BuildContext(new FakeCurrentUser { Id = publicId, TenantId = tenant }, db))
            .GetOrCreateApiKeyAsync(publicId);

        using (var ctx = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var user = ctx.Users.First(u => u.PublicId == publicId);
            user.IsActive = false;
            ctx.SaveChanges();
        }

        var result = await BuildAuthService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = key.Data!.ApiKey });

        Assert.False(result.IsSuccess);
    }
}
