using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Self-service change-password (any role): wrong current password rejected, success rotates the
/// security stamp (forces re-login) and best-effort emails a notification.
/// </summary>
public class ChangePasswordTests
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

    private sealed class FakeToken : ITokenService { public string Issue(User u) => "t"; }
    private sealed class FakeReset : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";
        public bool TryValidate(string token, out Guid id, out Guid stamp) { id = Guid.Empty; stamp = Guid.Empty; return false; }
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

    private sealed class SpyEmailService : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent.Add((to, subject, htmlBody));
            return Task.FromResult(true);
        }
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

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options, u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static AuthService Auth(AppDbContext db, ICurrentUser user, SpyEmailService? email = null) =>
        new(new UnitOfWork(db), new IdentityHasher(), new FakeToken(), user, new FakeSettings(),
            new FakeReset(), email ?? new SpyEmailService(), new NoopBrandingService());

    // Seeds one active user with password "OldPass123" and returns (publicId, ownerId, originalStamp).
    private static (Guid publicId, Guid ownerId, Guid stamp) SeedUser(string db)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var role = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
        seed.Roles.Add(role);
        seed.SaveChanges();

        var publicId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var user = new User
        {
            Email = "user@t.com", PasswordHash = "h:OldPass123", DisplayName = "User",
            PublicId = publicId, OwnerId = ownerId, RoleId = role.Id, IsActive = true
        };
        seed.Users.Add(user);
        seed.SaveChanges();
        return (publicId, ownerId, user.SecurityStamp);
    }

    [Fact]
    public async Task WrongCurrentPassword_Rejected_StampUnchanged()
    {
        var db = Guid.NewGuid().ToString();
        var (publicId, ownerId, stamp) = SeedUser(db);
        var caller = new FakeCurrentUser { Id = publicId, TenantId = ownerId };
        var ctx = Ctx(caller, db);

        var result = await Auth(ctx, caller).ChangePasswordAsync(new ChangePasswordRequest
        { CurrentPassword = "WrongPass", NewPassword = "NewPass123" });

        Assert.False(result.IsSuccess);
        var user = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal("h:OldPass123", user.PasswordHash);
    }

    [Fact]
    public async Task CorrectCurrentPassword_Succeeds_RotatesStamp_SendsEmail()
    {
        var db = Guid.NewGuid().ToString();
        var (publicId, ownerId, stamp) = SeedUser(db);
        var caller = new FakeCurrentUser { Id = publicId, TenantId = ownerId };
        var ctx = Ctx(caller, db);
        var spy = new SpyEmailService();

        var result = await Auth(ctx, caller, spy).ChangePasswordAsync(new ChangePasswordRequest
        { CurrentPassword = "OldPass123", NewPassword = "NewPass123" });

        Assert.True(result.IsSuccess);
        var user = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        Assert.Equal("h:NewPass123", user.PasswordHash);
        Assert.NotEqual(stamp, user.SecurityStamp); // forces logout everywhere, including this session

        Assert.Single(spy.Sent);
        Assert.Equal("user@t.com", spy.Sent[0].To);
    }

    [Fact]
    public async Task Unauthenticated_Rejected()
    {
        var db = Guid.NewGuid().ToString();
        SeedUser(db);
        var anon = new FakeCurrentUser { };
        var ctx = Ctx(anon, db);

        var result = await Auth(ctx, anon).ChangePasswordAsync(new ChangePasswordRequest
        { CurrentPassword = "OldPass123", NewPassword = "NewPass123" });

        Assert.False(result.IsSuccess);
    }
}
