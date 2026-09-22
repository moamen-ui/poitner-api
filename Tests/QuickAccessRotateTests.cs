using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Branding;
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
/// Revoking and rotating a quick-access magic link.
///
/// The link — not the Invite row — is the credential: LoginWithInviteAsync resolves a
/// QuickAccessLink by token hash and checks only that row's RevokedAt, never the invite's. So
/// "revoke the invite" and "the link stops working" are two different statements, and until these
/// tests existed only the first one was true. A leaked link had no kill switch at all.
/// </summary>
public class QuickAccessRotateTests
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

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) => "token-for-" + user.PublicId.ToString("N");
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

    private sealed class NullEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeBranding : IBrandingService
    {
        private static BrandingResponse R() => new() { ProductName = "Pointer" };
        public Task<Result<BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(R()));
        public Task<Result<BrandingResponse>> UpdateAsync(BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(R()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(R());
    }

    private static AppDbContext Ctx(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static InviteService Svc(ICurrentUser user, AppDbContext db) =>
        new(new UnitOfWork(db), user, new FakePasswordHasher(), new FakeTokenService(), new FakeSettings(),
            new PassThroughEntitlements(), new NullEmail(), new FakeBranding(), new MembershipService(new UnitOfWork(db)));

    /// <summary>
    /// Seeds a tenant with a quick-access ("Client") invite that has already issued one link, and
    /// returns everything the tests need to drive revoke/rotate and then try to redeem.
    /// </summary>
    private static (Guid tenant, int inviteId, string rawToken) Seed(string dbName)
    {
        var tenant = Guid.NewGuid();
        using var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var clientRole = new Role { Name = "Client", GrantsAdmin = false, IsActive = true, QuickAccess = true, OwnerId = tenant };
        db.Roles.Add(clientRole);
        db.SaveChanges();

        var project = new Project
        {
            Key = "acme", Name = "Acme", OwnerId = tenant, AppUrl = "https://acme.example.com",
        };
        db.Projects.Add(project);
        db.SaveChanges();

        var user = new User
        {
            Email = "client@acme.example.com",
            PasswordHash = "hashed:unusable",
            PasswordlessOnly = true,
            DisplayName = "client",
            RoleId = clientRole.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            OwnerId = tenant,
        };
        db.Users.Add(user);

        var invite = new Invite
        {
            OwnerId = tenant,
            Code = "CODE123",
            RoleId = clientRole.Id,
            Email = user.Email,
            ProjectId = project.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            MaxUses = 1,
            Uses = 1,
        };
        db.Invites.Add(invite);
        db.SaveChanges();

        var raw = QuickAccessTokenGenerator.NewToken();
        db.Set<QuickAccessLink>().Add(new QuickAccessLink
        {
            OwnerId = tenant,
            UserId = user.PublicId,
            ProjectId = project.Id,
            InviteId = invite.Id,
            TokenHash = QuickAccessTokenGenerator.Hash(raw),
            ExpiresAt = DateTime.UtcNow.AddDays(14),
            MaxUses = 0,
        });
        db.SaveChanges();

        return (tenant, invite.Id, raw);
    }

    private static bool LinkUsable(AppDbContext db, string rawToken)
    {
        var hash = QuickAccessTokenGenerator.Hash(rawToken);
        var link = db.Set<QuickAccessLink>().IgnoreQueryFilters()
            .FirstOrDefault(l => l.TokenHash == hash && l.DeletedAt == null);
        // The same three conditions LoginWithInviteAsync applies before it will issue a JWT.
        return link is not null && link.RevokedAt is null && link.ExpiresAt > DateTime.UtcNow;
    }

    [Fact]
    public async Task Revoke_AlsoKillsTheMagicLink()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, inviteId, raw) = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using var db = Ctx(admin, dbName);
        Assert.True(LinkUsable(db, raw)); // precondition: the link works

        var result = await Svc(admin, db).RevokeAsync(inviteId);

        Assert.True(result.IsSuccess);
        // The whole point: revoking the audit row must take the credential with it.
        Assert.False(LinkUsable(db, raw));
    }

    [Fact]
    public async Task Rotate_IssuesANewLinkAndInvalidatesTheOld()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, inviteId, oldRaw) = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using var db = Ctx(admin, dbName);
        var result = await Svc(admin, db).RotateQuickLinkAsync(inviteId);

        Assert.True(result.IsSuccess);
        var magicLink = result.Data!.MagicLink;
        Assert.False(string.IsNullOrWhiteSpace(magicLink));

        var newRaw = Uri.UnescapeDataString(magicLink!.Split("pointer_invite=")[1]);
        Assert.NotEqual(oldRaw, newRaw);

        Assert.True(LinkUsable(db, newRaw), "the rotated link must work");
        Assert.False(LinkUsable(db, oldRaw), "the leaked link must stop working");
    }

    [Fact]
    public async Task Rotate_Twice_LeavesOnlyTheNewestUsable()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, inviteId, original) = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using var db = Ctx(admin, dbName);
        var svc = Svc(admin, db);

        var first = await svc.RotateQuickLinkAsync(inviteId);
        var firstRaw = Uri.UnescapeDataString(first.Data!.MagicLink!.Split("pointer_invite=")[1]);
        var second = await svc.RotateQuickLinkAsync(inviteId);
        var secondRaw = Uri.UnescapeDataString(second.Data!.MagicLink!.Split("pointer_invite=")[1]);

        // A second rotation must not leave the first rotation's token behind — otherwise rotating
        // after a second leak would silently widen the set of working credentials.
        Assert.False(LinkUsable(db, original));
        Assert.False(LinkUsable(db, firstRaw));
        Assert.True(LinkUsable(db, secondRaw));
    }

    [Fact]
    public async Task Rotate_RefusesARevokedInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, inviteId, _) = Seed(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        using var db = Ctx(admin, dbName);
        var svc = Svc(admin, db);
        await svc.RevokeAsync(inviteId);

        var result = await svc.RotateQuickLinkAsync(inviteId);

        // Rotation hands out a working credential. Doing that for an invite an admin deliberately
        // revoked would undo the revoke.
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Rotate_IsUnreachableFromAnotherTenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, inviteId, raw) = Seed(dbName);
        var intruder = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = Guid.NewGuid() };

        using var db = Ctx(intruder, dbName);
        var result = await Svc(intruder, db).RotateQuickLinkAsync(inviteId);

        Assert.True(result.IsNotFound);
        Assert.True(LinkUsable(db, raw), "a foreign admin must not be able to disturb the link either");
    }

    [Fact]
    public async Task Rotate_RefusesAnInviteThatNeverIssuedALink()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int plainInviteId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var role = new Role { Name = "Developer", IsActive = true, OwnerId = tenant };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var invite = new Invite
            {
                OwnerId = tenant, Code = "PLAIN1", RoleId = role.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(7), Uses = 0,
            };
            seed.Invites.Add(invite);
            seed.SaveChanges();
            plainInviteId = invite.Id;
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        using var db = Ctx(admin, dbName);
        var result = await Svc(admin, db).RotateQuickLinkAsync(plainInviteId);

        // A normal staff invite has no magic link. Minting one here would create a passwordless
        // credential for an account that was never provisioned as quick-access.
        Assert.False(result.IsSuccess);
    }
}
