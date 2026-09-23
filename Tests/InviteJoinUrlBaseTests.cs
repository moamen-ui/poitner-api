using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The base URL invitation join links are built from. Before this, <c>app_base_url</c> was read by
/// <see cref="InviteService"/> but had no writer anywhere in the codebase (not in the settings DTO,
/// not in the controller, not seeded) — so every self-hosted instance mailed join links pointing at
/// the compiled SaaS default, where the code does not exist and the invite is dead. The resolution
/// order is now app_base_url (explicit override) → brand_url_app (what white-label installs already
/// set) → the compiled default.
/// </summary>
public class InviteJoinUrlBaseTests
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
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;
        public bool Verify(string password, string hash) => hash == "hashed:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) => "token-for-" + user.PublicId.ToString("N");
        public string IssueSelection(User user) => "selection-for-" + user.PublicId.ToString("N");
    }

    /// <summary>Settings backed by a dictionary, so the fallback chain is actually exercised.</summary>
    private sealed class DictSettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new();

        public DictSettings Set(string key, string value)
        {
            _values[key] = value;
            return this;
        }

        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(_values.TryGetValue(key, out var v) ? v : fallback);
        public Task SetStringAsync(string key, string value) { _values[key] = value; return Task.CompletedTask; }
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

    private sealed class FakeBrandingService : IBrandingService
    {
        private static BrandingResponse Response() => new() { ProductName = "Pointer" };
        public Task<Result<BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(Response()));
        public Task<Result<BrandingResponse>> UpdateAsync(BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(Response()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Response());
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    /// <summary>Seeds a tenant + its admin and returns (tenantId, memberRoleId).</summary>
    private static (Guid tenantId, int roleId) SeedTenant(string dbName)
    {
        var tenant = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsActive = true, OwnerId = tenant };
        var memberRole = new Role { Name = "Developer", GrantsAdmin = false, IsActive = true, OwnerId = tenant };
        seed.Roles.Add(adminRole);
        seed.Roles.Add(memberRole);
        seed.SaveChanges();

        seed.Users.Add(new User
        {
            Email = "admin@acme.test",
            PasswordHash = "x",
            DisplayName = "Acme Inc",
            RoleId = adminRole.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = tenant
        });
        seed.SaveChanges();

        return (tenant, memberRole.Id);
    }

    private static async Task<string> CreateInviteUrlAsync(string dbName, DictSettings settings)
    {
        var (tenant, roleId) = SeedTenant(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using var db = BuildContext(admin, dbName);
        var service = new InviteService(new UnitOfWork(db), admin, new FakePasswordHasher(), new FakeTokenService(),
            settings, new PassThroughEntitlements(), new SpyEmailService(), new FakeBrandingService(), new MembershipService(new UnitOfWork(db)));

        var result = await service.CreateAsync(new CreateInviteRequest { RoleId = roleId, Email = "invitee@acme.test" });

        Assert.True(result.IsSuccess, result.Message);
        return result.Data!.Url;
    }

    [Fact]
    public async Task Uses_BrandUrlApp_When_AppBaseUrl_Is_Unset()
    {
        // The regression: a white-labelled install sets only urls.app (brand_url_app) via
        // PUT /api/admin/branding — app_base_url has no UI at all before this change.
        var settings = new DictSettings().Set(ISettingsService.BrandUrlApp, "https://app.acme.test");

        var url = await CreateInviteUrlAsync(nameof(Uses_BrandUrlApp_When_AppBaseUrl_Is_Unset), settings);

        Assert.StartsWith("https://app.acme.test/join?code=", url);
        Assert.DoesNotContain("pointer.moamen.work", url);
    }

    [Fact]
    public async Task AppBaseUrl_Overrides_BrandUrlApp()
    {
        var settings = new DictSettings()
            .Set(ISettingsService.BrandUrlApp, "https://app.acme.test")
            .Set(ISettingsService.AppBaseUrl, "https://join.acme.test");

        var url = await CreateInviteUrlAsync(nameof(AppBaseUrl_Overrides_BrandUrlApp), settings);

        Assert.StartsWith("https://join.acme.test/join?code=", url);
    }

    [Fact]
    public async Task Falls_Back_To_Compiled_Default_When_Nothing_Is_Configured()
    {
        var url = await CreateInviteUrlAsync(nameof(Falls_Back_To_Compiled_Default_When_Nothing_Is_Configured),
            new DictSettings());

        Assert.StartsWith("https://app.pointer.moamen.work/join?code=", url);
    }

    [Fact]
    public async Task Trailing_Slash_Is_Trimmed()
    {
        var settings = new DictSettings().Set(ISettingsService.BrandUrlApp, "https://app.acme.test/");

        var url = await CreateInviteUrlAsync(nameof(Trailing_Slash_Is_Trimmed), settings);

        Assert.StartsWith("https://app.acme.test/join?code=", url);
        Assert.DoesNotContain("//join", url.Replace("https://", ""));
    }

    [Fact]
    public async Task Whitespace_Only_Override_Falls_Through_To_Branding()
    {
        var settings = new DictSettings()
            .Set(ISettingsService.AppBaseUrl, "   ")
            .Set(ISettingsService.BrandUrlApp, "https://app.acme.test");

        var url = await CreateInviteUrlAsync(nameof(Whitespace_Only_Override_Falls_Through_To_Branding), settings);

        Assert.StartsWith("https://app.acme.test/join?code=", url);
    }
}
