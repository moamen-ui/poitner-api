using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11c — remove / disable / leave / erase, and the sole-admin guard (S-13). Fixture mirrors
/// <see cref="UserGovernanceTests"/> (InMemory + <c>TestSeed.Join</c>); erase runs inside
/// <c>ExecuteInTransactionAsync</c>, so every context here ignores the InMemory
/// TransactionIgnoredWarning the same way <see cref="UserGovernanceTests"/> does.
/// </summary>
public class DeletionSemanticsTests
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
        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(Pointer.Application.DTOs.Branding.BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(DefaultBranding());
    }

    /// <summary>Records every send; extracts the token= query param from the first link in the body.</summary>
    private sealed class CapturingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
        {
            Sent.Add((to, subject, htmlBody));
            return Task.FromResult(true);
        }

        public static string ExtractToken(string html)
        {
            var marker = "token=";
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(start >= 0, "no token= found in email body");
            start += marker.Length;
            var end = start;
            while (end < html.Length && html[end] != '"' && html[end] != '&' && html[end] != ' ')
                end++;
            return Uri.UnescapeDataString(html[start..end]);
        }
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");
        public string IssueSelection(User user) => "sel-for-" + user.PublicId.ToString("N");
        public string IssueImpersonation(User user, Guid workspace, long sessionId, DateTime expiresAt) =>
            "imp-for-" + user.PublicId.ToString("N");
    }

    private sealed class RecordingFileStorage : IFileStorage
    {
        public List<string> Deleted { get; } = new();
        public List<string> DeletedOwners { get; } = new();
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) => Task.FromResult("");
        public Task DeleteAsync(string relativePathOrUrl)
        {
            Deleted.Add(relativePathOrUrl);
            return Task.CompletedTask;
        }
        public Task DeleteOwnerFilesAsync(string ownerSegment)
        {
            DeletedOwners.Add(ownerSegment);
            return Task.CompletedTask;
        }
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static IResetTokenService RealResetTokens() =>
        new ResetTokenService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789" })
                .Build());

    private static UserService BuildUserService(ICurrentUser user, AppDbContext ctx, IAuditWriter? audit = null)
    {
        var uow = new UnitOfWork(ctx);
        return new UserService(uow, new IdentityHasher(), user, new NoopEmail(), new PassThroughEntitlements(),
            new NoopBrandingService(), new MembershipService(uow), audit);
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private static IdentityEraseService BuildEraseService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail? email = null,
        IAuditWriter? audit = null
    )
    {
        var uow = new UnitOfWork(ctx);
        return new IdentityEraseService(
            uow,
            new MembershipService(uow),
            user,
            new IdentityHasher(),
            RealResetTokens(),
            email ?? new CapturingEmail(),
            new NoopBrandingService(),
            audit
        );
    }

    private static AuthService BuildAuthService(ICurrentUser user, AppDbContext ctx, CapturingEmail? email = null)
    {
        var uow = new UnitOfWork(ctx);
        return new AuthService(
            uow,
            new IdentityHasher(),
            new FakeTokenService(),
            user,
            new NoopSettings(),
            RealResetTokens(),
            email ?? new CapturingEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );
    }

    private static InviteService BuildInviteService(ICurrentUser user, AppDbContext ctx, IAuditWriter? audit = null)
    {
        var uow = new UnitOfWork(ctx);
        return new InviteService(
            uow,
            user,
            new IdentityHasher(),
            new FakeTokenService(),
            new NoopSettings(),
            new PassThroughEntitlements(),
            new NoopEmail(),
            new NoopBrandingService(),
            new MembershipService(uow),
            audit
        );
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    private sealed class SeededWorkspace
    {
        public Guid OwnerId;
        public int AdminRoleId;
        public int DeputyRoleId;
        public int MemberRoleId;
        public User Admin = null!;
        public User Deputy = null!;
        public User Member = null!;
    }

    /// <summary>One tenant: Admin (sole), Deputy, Member (non-admin). Workspace named "Test Workspace".</summary>
    private static SeededWorkspace SeedWorkspace(AppDbContext seed)
    {
        var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
        var deputyRole = new Role { Name = "Workspace Admin Deputy", GrantsAdmin = true, IsSystem = true, IsActive = true };
        var memberRole = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
        seed.Roles.AddRange(adminRole, deputyRole, memberRole);
        seed.SaveChanges();

        var ownerId = Guid.NewGuid();
        var admin = new User { Email = "admin@t.com", PasswordHash = "h:pw-admin", DisplayName = "Admin", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = adminRole.Id, IsActive = true };
        var deputy = new User { Email = "deputy@t.com", PasswordHash = "h:pw-deputy", DisplayName = "Deputy", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = deputyRole.Id, IsActive = true };
        var member = new User { Email = "member@t.com", PasswordHash = "h:pw-member", DisplayName = "Member", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = memberRole.Id, IsActive = true };
        seed.Users.AddRange(admin, deputy, member);
        seed.SaveChanges();

        TestSeed.Join(seed, admin, ownerId, adminRole);
        TestSeed.Join(seed, deputy, ownerId, deputyRole);
        TestSeed.Join(seed, member, ownerId, memberRole);

        return new SeededWorkspace
        {
            OwnerId = ownerId,
            AdminRoleId = adminRole.Id,
            DeputyRoleId = deputyRole.Id,
            MemberRoleId = memberRole.Id,
            Admin = admin,
            Deputy = deputy,
            Member = member,
        };
    }

    /// <summary>Creates (or reuses) a workspace row named <paramref name="name"/> and joins <paramref name="identity"/> as its live Workspace Admin.</summary>
    private static Guid SeedSoleAdminWorkspace(AppDbContext seed, string name, User identity, Role adminRole)
    {
        var ownerId = Guid.NewGuid();
        seed.Workspaces.Add(new Workspace { Id = ownerId, Name = name, CreatedAt = DateTime.UtcNow, CreatedBy = ownerId });
        seed.SaveChanges();
        TestSeed.Join(seed, identity, ownerId, adminRole);
        return ownerId;
    }

    // ── 1. Remove ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_EndsMembership_RevokesOnlyThatWorkspacesKeysAndLinks()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        User member;

        using (var seed = Ctx(superAdmin, db))
        {
            var engineerRole = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(engineerRole);
            seed.Workspaces.Add(new Workspace { Id = workspaceA, Name = "A", CreatedAt = DateTime.UtcNow, CreatedBy = workspaceA });
            seed.Workspaces.Add(new Workspace { Id = workspaceB, Name = "B", CreatedAt = DateTime.UtcNow, CreatedBy = workspaceB });
            member = new User { Email = "m@t.com", PasswordHash = "h:pw", DisplayName = "M", PublicId = Guid.NewGuid(), OwnerId = workspaceA, RoleId = engineerRole.Id, IsActive = true };
            seed.Users.Add(member);
            seed.SaveChanges();
            TestSeed.Join(seed, member, workspaceA, engineerRole);
            TestSeed.Join(seed, member, workspaceB, engineerRole);
            seed.ApiKeys.Add(new ApiKey { UserId = member.Id, OwnerId = workspaceA, Hash = "hA", Encrypted = "e", Prefix = "p" });
            seed.ApiKeys.Add(new ApiKey { UserId = member.Id, OwnerId = workspaceB, Hash = "hB", Encrypted = "e", Prefix = "p" });
            seed.SaveChanges();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = workspaceA, IsAdmin = true };
        using (var ctx = Ctx(admin, db))
        {
            var result = await BuildUserService(admin, ctx).DeleteAsync(member.Id);
            Assert.True(result.IsSuccess);
        }

        using var check = Ctx(superAdmin, db);
        var keyA = check.ApiKeys.IgnoreQueryFilters().Single(k => k.OwnerId == workspaceA);
        var keyB = check.ApiKeys.IgnoreQueryFilters().Single(k => k.OwnerId == workspaceB);
        Assert.NotNull(keyA.RevokedAt);
        Assert.Null(keyB.RevokedAt);

        var mA = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.OwnerId == workspaceA && m.UserId == member.Id);
        var mB = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.OwnerId == workspaceB && m.UserId == member.Id);
        Assert.NotNull(mA.LeftAt);
        Assert.Equal(MembershipEndReason.Removed, mA.LeftReason);
        Assert.Null(mB.LeftAt);

        var userRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == member.Id);
        Assert.Null(userRow.DeletedAt);
    }

    // ── 2. Sole-admin guard (S-13) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remove_SoleAdmin_Conflict_NamesWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var deputy = new FakeCurrentUser { Id = ws.Deputy.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using var ctx = Ctx(deputy, db);
        var result = await BuildUserService(deputy, ctx).DeleteAsync(ws.Admin.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(string.Format(MessageKeys.User.SoleAdminBlocked, "Test Workspace"), result.Message);
    }

    [Fact]
    public async Task Demote_SoleAdmin_BySuperAdmin_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).UpdateAsync(ws.Admin.Id, new UpdateUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsConflict);

        using var check = Ctx(superAdmin, db);
        var adminRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Admin.Id);
        Assert.Equal(ws.AdminRoleId, adminRow.RoleId);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.OwnerId);
        Assert.Equal(ws.AdminRoleId, membership.RoleId);
    }

    [Fact]
    public async Task Disable_SoleAdmin_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).UpdateAsync(ws.Admin.Id, new UpdateUserRequest { IsActive = false });

        Assert.True(result.IsConflict);
        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.OwnerId);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public async Task Demote_AdminWhenAnotherAdminExists_Allowed()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        User admin2 = null!;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var adminRole = seed.Roles.Single(r => r.Id == ws.AdminRoleId);
            admin2 = new User { Email = "admin2@t.com", PasswordHash = "h:pw2", DisplayName = "Admin2", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = adminRole.Id, IsActive = true };
            seed.Users.Add(admin2);
            seed.SaveChanges();
            TestSeed.Join(seed, admin2, ws.OwnerId, adminRole);
        }

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).UpdateAsync(ws.Admin.Id, new UpdateUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Remove_LastActiveAdmin_WhenOtherAdminDisabled_Conflict()
    {
        // Review finding #4: a disabled admin membership must not count as covering for the last
        // live one — before the fix, SoleAdminWorkspacesAsync's predicate ignored IsActive/
        // ApprovalStatus, so this disabled admin2 made the count 2 and let the only ACTIVE admin be
        // removed, leaving the workspace with no one who could act.
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var adminRole = seed.Roles.Single(r => r.Id == ws.AdminRoleId);
            var admin2 = new User { Email = "admin2@t.com", PasswordHash = "h:pw2", DisplayName = "Admin2", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = adminRole.Id, IsActive = false };
            seed.Users.Add(admin2);
            seed.SaveChanges();
            TestSeed.Join(seed, admin2, ws.OwnerId, adminRole, isActive: false);
        }

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).DeleteAsync(ws.Admin.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(string.Format(MessageKeys.User.SoleAdminBlocked, "Test Workspace"), result.Message);
    }

    [Fact]
    public async Task Remove_LastApprovedAdmin_WhenOtherAdminRejected_Conflict()
    {
        // Same as above but the "other admin" is Rejected rather than disabled — also must not count.
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var adminRole = seed.Roles.Single(r => r.Id == ws.AdminRoleId);
            var admin2 = new User { Email = "admin2@t.com", PasswordHash = "h:pw2", DisplayName = "Admin2", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = adminRole.Id, IsActive = false };
            seed.Users.Add(admin2);
            seed.SaveChanges();
            TestSeed.Join(seed, admin2, ws.OwnerId, adminRole, isActive: false, status: ApprovalStatus.Rejected);
        }

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).DeleteAsync(ws.Admin.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(string.Format(MessageKeys.User.SoleAdminBlocked, "Test Workspace"), result.Message);
    }

    // ── 2b. Deputy cannot remove a non-sole Workspace Admin (review finding #7) ─────────────

    [Fact]
    public async Task Deputy_CannotDelete_NonSoleWorkspaceAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        User admin2 = null!;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var adminRole = seed.Roles.Single(r => r.Id == ws.AdminRoleId);
            admin2 = new User { Email = "admin2@t.com", PasswordHash = "h:pw2", DisplayName = "Admin2", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = adminRole.Id, IsActive = true };
            seed.Users.Add(admin2);
            seed.SaveChanges();
            TestSeed.Join(seed, admin2, ws.OwnerId, adminRole);
        }

        // Before the fix, only Deputy-removes-Deputy was blocked — a Deputy could remove any
        // Workspace Admin as long as that admin wasn't the workspace's ONLY one.
        var deputy = new FakeCurrentUser { Id = ws.Deputy.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using var ctx = Ctx(deputy, db);
        var result = await BuildUserService(deputy, ctx).DeleteAsync(admin2.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.CannotRemoveAdmin, result.Message);

        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == admin2.Id && m.OwnerId == ws.OwnerId);
        Assert.Null(membership.LeftAt);
    }

    // ── 2c. Reject / Approve S-13 guards (review findings #1, #2) ───────────────────────────

    [Fact]
    public async Task Reject_SoleAdmin_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx).RejectAsync(ws.Admin.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(string.Format(MessageKeys.User.SoleAdminBlocked, "Test Workspace"), result.Message);

        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.OwnerId);
        Assert.Equal(ApprovalStatus.Approved, membership.ApprovalStatus);
        Assert.True(membership.IsActive);
    }

    [Fact]
    public async Task Reject_Self_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        User pending = null!;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var role = seed.Roles.Single(r => r.Id == ws.MemberRoleId);
            pending = new User { Email = "pending@t.com", PasswordHash = "h:pw", DisplayName = "Pending", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = role.Id, IsActive = false, ApprovalStatus = ApprovalStatus.Pending };
            seed.Users.Add(pending);
            seed.SaveChanges();
            TestSeed.Join(seed, pending, ws.OwnerId, role, isActive: false, status: ApprovalStatus.Pending);
        }

        var caller = new FakeCurrentUser { Id = pending.PublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(caller, db);
        var result = await BuildUserService(caller, ctx).RejectAsync(pending.Id);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.CannotRejectSelf, result.Message);
    }

    [Fact]
    public async Task Reject_NotPending_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        // ws.Member is already Approved (TestSeed.Join's default) — reject is only meaningful for a
        // still-pending request.
        var result = await BuildUserService(superAdmin, ctx).RejectAsync(ws.Member.Id);

        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.User.NotPending, result.Message);
    }

    [Fact]
    public async Task Approve_SoleAdmin_RoleChangeAway_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx)
            .ApproveAsync(ws.Admin.Id, new ApproveUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsConflict);
        Assert.Equal(string.Format(MessageKeys.User.SoleAdminBlocked, "Test Workspace"), result.Message);

        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Admin.Id && m.OwnerId == ws.OwnerId);
        Assert.Equal(ws.AdminRoleId, membership.RoleId);
    }

    [Fact]
    public async Task Approve_RoleChange_RotatesMembershipStamp()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        Guid beforeStamp;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            beforeStamp = seed.WorkspaceMemberships.IgnoreQueryFilters()
                .Single(m => m.UserId == ws.Member.Id && m.OwnerId == ws.OwnerId).SecurityStamp;
        }

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildUserService(superAdmin, ctx)
            .ApproveAsync(ws.Member.Id, new ApproveUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsSuccess, result.Message);

        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters()
            .Single(m => m.UserId == ws.Member.Id && m.OwnerId == ws.OwnerId);
        Assert.NotEqual(beforeStamp, membership.SecurityStamp);
    }

    // ── 3. Disable keeps keys ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Disable_KeepsKeys_ButLoginWithKeyReturnsDisabled_AndEnableRestores()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        string rawKey;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
        }
        using (var keyCtx = Ctx(superAdmin, db))
        {
            var keySvc = new ApiKeyService(new UnitOfWork(keyCtx), new TestApiKeyProtector());
            var minted = await keySvc.GetOrCreateAsync(ws.Member.PublicId, ws.OwnerId);
            Assert.True(minted.Found);
            rawKey = minted.RawKey!;
        }

        var admin = new FakeCurrentUser { Id = ws.Admin.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using (var ctx = Ctx(admin, db))
        {
            var disableResult = await BuildUserService(admin, ctx).UpdateAsync(ws.Member.Id, new UpdateUserRequest { IsActive = false });
            Assert.True(disableResult.IsSuccess);
        }

        using (var check = Ctx(superAdmin, db))
        {
            var key = check.ApiKeys.IgnoreQueryFilters().Single(k => k.UserId == ws.Member.Id);
            Assert.Null(key.RevokedAt);
        }

        using (var loginCtx = Ctx(new FakeCurrentUser(), db))
        {
            var login = await BuildAuthService(new FakeCurrentUser(), loginCtx).LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = rawKey });
            Assert.Equal("disabled", login.Data!.Status);
        }

        using (var ctx = Ctx(admin, db))
        {
            var enableResult = await BuildUserService(admin, ctx).UpdateAsync(ws.Member.Id, new UpdateUserRequest { IsActive = true });
            Assert.True(enableResult.IsSuccess);
        }

        using (var loginCtx = Ctx(new FakeCurrentUser(), db))
        {
            var login = await BuildAuthService(new FakeCurrentUser(), loginCtx).LoginWithApiKeyAsync(new LoginWithApiKeyRequest { ApiKey = rawKey });
            Assert.Equal("ok", login.Data!.Status);
        }
    }

    // ── 4. Leave ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_Works_ForNonAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(member, db);
        var result = await BuildUserService(member, ctx).LeaveWorkspaceAsync();

        Assert.True(result.IsSuccess);
        using var check = Ctx(superAdmin, db);
        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Member.Id && m.OwnerId == ws.OwnerId);
        Assert.NotNull(membership.LeftAt);
        Assert.Equal(MembershipEndReason.Left, membership.LeftReason);
    }

    [Fact]
    public async Task Leave_SoleAdmin_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var admin = new FakeCurrentUser { Id = ws.Admin.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using var ctx = Ctx(admin, db);
        var result = await BuildUserService(admin, ctx).LeaveWorkspaceAsync();

        Assert.True(result.IsConflict);
    }

    [Fact]
    public async Task Leave_ThenLogin_ReturnsNoWorkspace_WhenLastMembership()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildUserService(member, ctx).LeaveWorkspaceAsync();
            Assert.True(result.IsSuccess);
        }

        using var loginCtx = Ctx(new FakeCurrentUser(), db);
        var login = await BuildAuthService(new FakeCurrentUser(), loginCtx)
            .LoginAsync(new LoginRequest { Email = "member@t.com", Password = "pw-member" });

        Assert.NotEqual("ok", login.Data?.Status);
        Assert.Equal("no-workspace", login.Data?.Status);
    }

    // ── 5. Erase self ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Erase_Self_Tombstone_KeepsComments_ResolvesDeletedUser()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int commentId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            seed.Projects.Add(new Project { Id = 1, Key = "proj", Name = "Proj", OwnerId = ws.OwnerId });
            seed.SaveChanges();

            var comment = new Comment { ProjectId = 1, OwnerId = ws.OwnerId, AuthorId = ws.Member.PublicId, Body = "hi", Element = new() };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
            seed.Replies.Add(new Reply { CommentId = commentId, OwnerId = ws.OwnerId, AuthorId = ws.Member.PublicId, Body = "reply" });

            seed.ApiKeys.Add(new ApiKey { UserId = ws.Member.Id, OwnerId = ws.OwnerId, Hash = "h1", Encrypted = "e", Prefix = "p" });
            seed.DeviceLogins.Add(new DeviceLogin { DeviceCodeHash = "d1", UserCode = "ABCD-1234", ClientName = "cli", ExpiresAt = DateTime.UtcNow.AddHours(1), Status = DeviceLoginStatus.Approved, UserId = ws.Member.PublicId, OwnerId = ws.OwnerId });
            seed.QuickAccessLinks.Add(new QuickAccessLink { OwnerId = ws.OwnerId, UserId = ws.Member.PublicId, ProjectId = 1, InviteId = 0, TokenHash = "t1", ExpiresAt = DateTime.UtcNow.AddDays(1) });
            seed.Notifications.Add(new Notification { OwnerId = ws.OwnerId, UserId = ws.Member.PublicId, Type = NotificationType.ReplyAdded, ProjectId = 1 });
            seed.AiRules.Add(new AiRule { OwnerId = ws.OwnerId, UserId = ws.Member.PublicId, Title = "rule", Prompt = "do x" });
            seed.SaveChanges();
        }

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var userRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
        Assert.NotNull(userRow.ErasedAt);
        Assert.NotNull(userRow.DeletedAt);
        Assert.StartsWith("erased+", userRow.Email);
        Assert.Equal("Deleted user", userRow.DisplayName);
        Assert.Equal(ws.Member.PublicId, userRow.PublicId);

        Assert.Empty(check.ApiKeys.IgnoreQueryFilters().Where(k => k.UserId == ws.Member.Id));
        Assert.Empty(check.DeviceLogins.IgnoreQueryFilters().Where(d => d.UserId == ws.Member.PublicId));
        Assert.Empty(check.QuickAccessLinks.IgnoreQueryFilters().Where(l => l.UserId == ws.Member.PublicId));
        Assert.Empty(check.Notifications.IgnoreQueryFilters().Where(n => n.UserId == ws.Member.PublicId));
        Assert.Empty(check.AiRules.IgnoreQueryFilters().Where(r => r.UserId == ws.Member.PublicId));

        var commentRow = check.Comments.IgnoreQueryFilters().Single(c => c.Id == commentId);
        Assert.Equal(ws.Member.PublicId, commentRow.AuthorId);
        var replyRow = check.Replies.IgnoreQueryFilters().Single(r => r.CommentId == commentId);
        Assert.Equal(ws.Member.PublicId, replyRow.AuthorId);

        var names = await UserNameResolver.ResolveAsync(new UnitOfWork(check), new[] { ws.Member.PublicId }, ignoreQueryFilters: true);
        Assert.Equal("Deleted user", names[ws.Member.PublicId]);

        var membership = check.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == ws.Member.Id && m.OwnerId == ws.OwnerId);
        Assert.Equal(MembershipEndReason.AccountErased, membership.LeftReason);
    }

    // ── 6. Erase guards ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Erase_BlockedWhileSoleAdminAnywhere_ListsWorkspaces()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        User identity = null!;
        using (var seed = Ctx(superAdmin, db))
        {
            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();
            identity = new User { Email = "multi@t.com", PasswordHash = "h:pw", DisplayName = "Multi", PublicId = Guid.NewGuid(), RoleId = adminRole.Id, IsActive = true };
            seed.Users.Add(identity);
            seed.SaveChanges();
            SeedSoleAdminWorkspace(seed, "WS-One", identity, adminRole);
            SeedSoleAdminWorkspace(seed, "WS-Two", identity, adminRole);
        }

        var caller = new FakeCurrentUser { Id = identity.PublicId };
        using var ctx = Ctx(caller, db);
        var result = await BuildEraseService(caller, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw" });

        Assert.True(result.IsConflict);
        Assert.Contains("WS-One", result.Message);
        Assert.Contains("WS-Two", result.Message);
    }

    [Fact]
    public async Task Erase_WrongPassword_Fails_NothingChanged()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "wrong" });
            Assert.False(result.IsSuccess);
            Assert.Equal(MessageKeys.User.CurrentPasswordIncorrect, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var userRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
        Assert.Null(userRow.ErasedAt);
        Assert.Equal("member@t.com", userRow.Email);
    }

    [Fact]
    public async Task Erase_Passwordless_ByPassword_Fails_PointsToEmailRail()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var m = seed.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
            m.PasswordlessOnly = true;
            seed.SaveChanges();
        }

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(member, db);
        var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EraseNeedsEmailConfirmation, result.Message);

        using var check = Ctx(superAdmin, db);
        Assert.Null(check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id).ErasedAt);
    }

    [Fact]
    public async Task Erase_BySuperAdmin_OnSuperAdmin_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid targetPublicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role { Name = "Super Admin", IsSystem = true, IsSuperAdmin = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var target = new User { Email = "root@t.com", PasswordHash = "h:pw", DisplayName = "Root", PublicId = Guid.NewGuid(), RoleId = role.Id, IsActive = true };
            seed.Users.Add(target);
            seed.SaveChanges();
            targetPublicId = target.PublicId;
        }

        using var ctx = Ctx(superAdmin, db);
        var result = await BuildEraseService(superAdmin, ctx).EraseByPublicIdAsync(targetPublicId);

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Erase_TenantB_CannotRemoveAsMember()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var tenantBAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), IsAdmin = true };
        using var ctx = Ctx(tenantBAdmin, db);
        var result = await BuildUserService(tenantBAdmin, ctx).DeleteAsync(ws.Member.Id);

        Assert.True(result.IsNotFound);
    }

    // ── 7. Erase side-effects on auth ───────────────────────────────────────────────────────

    [Fact]
    public async Task Login_AfterErase_InvalidCredentials()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var erase = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(erase.IsSuccess);
        }

        using var loginCtx = Ctx(new FakeCurrentUser(), db);
        var login = await BuildAuthService(new FakeCurrentUser(), loginCtx)
            .LoginAsync(new LoginRequest { Email = "member@t.com", Password = "pw-member" });

        Assert.False(login.IsSuccess);
        Assert.Equal(MessageKeys.Auth.InvalidCredentials, login.Message);
    }

    [Fact]
    public async Task PasswordReset_AfterErase_NoEmail()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var erase = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(erase.IsSuccess);
        }

        var email = new CapturingEmail();
        using var resetCtx = Ctx(new FakeCurrentUser(), db);
        var reset = await BuildAuthService(new FakeCurrentUser(), resetCtx, email)
            .RequestPasswordResetAsync(new ForgotPasswordRequest { Email = "member@t.com" });

        Assert.True(reset.IsSuccess);
        Assert.Empty(email.Sent);
    }

    // ── 8. Revoke invite ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeInvite_Accepted_ReturnsInvitees()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int inviteId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var invite = new Invite { OwnerId = ws.OwnerId, Code = "accepted-code", Email = "invitee@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), Uses = 1, CreatedAt = DateTime.UtcNow };
            seed.Invites.Add(invite);
            seed.SaveChanges();
            inviteId = invite.Id;

            var invitee = new User { Email = "invitee@t.com", PasswordHash = "h:pw", DisplayName = "Invitee", PublicId = Guid.NewGuid(), OwnerId = ws.OwnerId, RoleId = ws.MemberRoleId, IsActive = true };
            seed.Users.Add(invitee);
            seed.SaveChanges();
            var memberRole = seed.Roles.Single(r => r.Id == ws.MemberRoleId);
            var membership = TestSeed.Join(seed, invitee, ws.OwnerId, memberRole);
            membership.InviteId = inviteId;
            seed.SaveChanges();
        }

        var admin = new FakeCurrentUser { Id = ws.Admin.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using var ctx = Ctx(admin, db);
        var result = await BuildInviteService(admin, ctx).RevokeAsync(inviteId);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Data!.Invitees);
        Assert.Equal("invitee@t.com", result.Data!.Invitees[0].Email);
    }

    [Fact]
    public async Task RevokeInvite_NeverAccepted_EmptyList()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int inviteId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var invite = new Invite { OwnerId = ws.OwnerId, Code = "open-code", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow };
            seed.Invites.Add(invite);
            seed.SaveChanges();
            inviteId = invite.Id;
        }

        var admin = new FakeCurrentUser { Id = ws.Admin.PublicId, TenantId = ws.OwnerId, IsAdmin = true };
        using var ctx = Ctx(admin, db);
        var result = await BuildInviteService(admin, ctx).RevokeAsync(inviteId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Data!.Invitees);
    }

    // ── 9. Tenant suspension exempt (D10) ────────────────────────────────────────────────────

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string o, string p, Stream c, string e) => Task.FromResult("");
        public Task DeleteAsync(string x) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string o) => Task.CompletedTask;
    }

    [Fact]
    public async Task TenantSetStatus_Disable_SoleAdmin_Allowed()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        using var ctx = Ctx(superAdmin, db);
        var uow = new UnitOfWork(ctx);
        var tenantSvc = new TenantService(uow, new IdentityHasher(), new NoopFileStorage(), new NoopSettings(), new NoopBillingProvider(), new MembershipService(uow));

        var result = await tenantSvc.SetStatusAsync(ws.OwnerId, "disable");

        Assert.True(result.IsSuccess);
    }

    // ── 11. Invite scrub (GLM A2) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Erase_ScrubsInviteEmail_KeepsInviteLocked()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int acceptedInviteId, openInviteId, otherInviteId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var accepted = new Invite { OwnerId = ws.OwnerId, Code = "code-accepted", Email = "member@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), Uses = 1, MaxUses = 1, CreatedAt = DateTime.UtcNow };
            var open = new Invite { OwnerId = ws.OwnerId, Code = "code-open", Email = "member@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), Uses = 0, CreatedAt = DateTime.UtcNow };
            var other = new Invite { OwnerId = ws.OwnerId, Code = "code-other", Email = "someone-else@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), Uses = 0, CreatedAt = DateTime.UtcNow };
            seed.Invites.AddRange(accepted, open, other);
            seed.SaveChanges();
            acceptedInviteId = accepted.Id;
            openInviteId = open.Id;
            otherInviteId = other.Id;
        }

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var acceptedRow = check.Invites.IgnoreQueryFilters().Single(i => i.Id == acceptedInviteId);
        var openRow = check.Invites.IgnoreQueryFilters().Single(i => i.Id == openInviteId);
        var otherRow = check.Invites.IgnoreQueryFilters().Single(i => i.Id == otherInviteId);

        Assert.StartsWith("erased+", acceptedRow.Email);
        Assert.Equal(1, acceptedRow.Uses);
        Assert.Null(acceptedRow.RevokedAt);
        Assert.StartsWith("erased+", openRow.Email);
        Assert.Null(openRow.RevokedAt);
        Assert.Equal("someone-else@t.com", otherRow.Email);

        // The open invite's original e-mail lock no longer matches — accept fails.
        var anon = new FakeCurrentUser();
        using var acceptCtx = Ctx(anon, db);
        var acceptResult = await BuildInviteService(anon, acceptCtx).AcceptAsync(new Pointer.Application.DTOs.Invite.AcceptInviteRequest
        {
            Code = "code-open",
            Email = "member@t.com",
            Password = "newpassword1",
            DisplayName = "New Member",
        });
        Assert.False(acceptResult.IsSuccess);
    }

    [Fact]
    public async Task Erase_ScrubsInviteEmail_CaseInsensitive()
    {
        // Review finding #6: the scrub compared invites.email to the identity's e-mail with an
        // ordinal (case-sensitive) equality — a mixed-case invite row (e.g. entered by an admin
        // as "Member@T.com") survived erase untouched.
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int inviteId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var invite = new Invite { OwnerId = ws.OwnerId, Code = "code-mixed-case", Email = "Member@T.com", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow };
            seed.Invites.Add(invite);
            seed.SaveChanges();
            inviteId = invite.Id;
        }

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var inviteRow = check.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId);
        Assert.StartsWith("erased+", inviteRow.Email);
    }

    [Fact]
    public async Task Erase_LeavesNoEmailBehind()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int mergedPredecessorId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var m = seed.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
            m.RecipientEmail = "member@t.com";
            seed.Invites.Add(new Invite { OwnerId = ws.OwnerId, Code = "code-x", Email = "member@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow });

            // Review finding #3: a row the DB-11a same-e-mail merge folded into ws.Member — soft-deleted,
            // MergedIntoUserId pointing at the canonical row, but (before the fix) still carrying the
            // original e-mail/name forever even after the canonical identity was erased.
            var mergedPredecessor = new User
            {
                Email = "member@t.com",
                PasswordHash = "h:oldpw12345",
                DisplayName = "Old Duplicate",
                RecipientEmail = "member@t.com",
                PublicId = Guid.NewGuid(),
                RoleId = ws.MemberRoleId,
                OwnerId = ws.OwnerId,
                IsActive = false,
                ApprovalStatus = ApprovalStatus.Approved,
                DeletedAt = DateTime.UtcNow,
                MergedIntoUserId = ws.Member.Id,
            };
            seed.Users.Add(mergedPredecessor);
            seed.SaveChanges();
            mergedPredecessorId = mergedPredecessor.Id;
        }

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(result.IsSuccess, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        const string original = "member@t.com";
        Assert.False(check.Users.IgnoreQueryFilters().Any(u => u.Email == original || u.RecipientEmail == original));
        Assert.False(check.Invites.IgnoreQueryFilters().Any(i => i.Email == original));

        var predecessorRow = check.Users.IgnoreQueryFilters().Single(u => u.Id == mergedPredecessorId);
        Assert.NotEqual(original, predecessorRow.Email);
        Assert.StartsWith("erased+", predecessorRow.Email);
        Assert.Equal("Deleted user", predecessorRow.DisplayName);
        Assert.Null(predecessorRow.RecipientEmail);
        Assert.NotNull(predecessorRow.ErasedAt);
    }

    // ── 12. Screenshots kept (F5) ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Erase_KeepsScreenshotFile()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        int commentId;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            seed.Projects.Add(new Project { Id = 1, Key = "proj", Name = "Proj", OwnerId = ws.OwnerId });
            seed.SaveChanges();
            var comment = new Comment
            {
                ProjectId = 1,
                OwnerId = ws.OwnerId,
                AuthorId = ws.Member.PublicId,
                Body = "look at this",
                Element = new() { ScreenshotUrl = "uploads/x/y/z.png" },
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
        }

        // Never passed to IdentityEraseService — its constructor has no IFileStorage parameter at
        // all, so this recorder proves (by construction) that erase cannot touch it.
        var recorder = new RecordingFileStorage();

        var member = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using (var ctx = Ctx(member, db))
        {
            var result = await BuildEraseService(member, ctx).EraseSelfAsync(new DeleteMyAccountRequest { Password = "pw-member" });
            Assert.True(result.IsSuccess, result.Message);
        }

        Assert.Empty(recorder.Deleted);
        Assert.Empty(recorder.DeletedOwners);

        using var check = Ctx(superAdmin, db);
        var commentRow = check.Comments.IgnoreQueryFilters().Single(c => c.Id == commentId);
        Assert.Equal("uploads/x/y/z.png", commentRow.Element.ScreenshotUrl);
        // DB-16: erase must not touch the DB-16 marker either — the comment isn't even soft-deleted
        // by erase (F5), so screenshot_purged_at stays null regardless of grace period.
        Assert.Null(commentRow.ScreenshotPurgedAt);
    }

    // ── 14. Scoped erase tokens end-to-end (GLM A3) ──────────────────────────────────────────

    private static (SeededWorkspace Ws, Guid PublicId) SeedPasswordlessMember(string db, FakeCurrentUser superAdmin)
    {
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
        {
            ws = SeedWorkspace(seed);
            var m = seed.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
            m.PasswordlessOnly = true;
            seed.SaveChanges();
        }
        return (ws, ws.Member.PublicId);
    }

    [Fact]
    public async Task RequestErase_Passwordless_SendsScopedToken()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var (ws, memberPublicId) = SeedPasswordlessMember(db, superAdmin);

        var email = new CapturingEmail();
        var caller = new FakeCurrentUser { Id = memberPublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(caller, db);
        var result = await BuildEraseService(caller, ctx, email).RequestEraseLinkAsync();

        Assert.True(result.IsSuccess, result.Message);
        Assert.Single(email.Sent);
        var token = CapturingEmail.ExtractToken(email.Sent[0].Html);

        var resetTokens = RealResetTokens();
        Assert.True(resetTokens.TryValidateScoped(token, TokenPurposes.Erase, out _, out _, out _));
        Assert.False(resetTokens.TryValidate(token, out _, out _));
    }

    [Fact]
    public async Task RequestErase_PasswordAccount_Failure_NoEmail()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var email = new CapturingEmail();
        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(caller, db);
        var result = await BuildEraseService(caller, ctx, email).RequestEraseLinkAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EraseUsePassword, result.Message);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task RequestErase_SuperAdmin_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid rootPublicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role { Name = "Super Admin", IsSystem = true, IsSuperAdmin = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var root = new User { Email = "root2@t.com", PasswordHash = "h:pw", DisplayName = "Root2", PublicId = Guid.NewGuid(), RoleId = role.Id, IsActive = true };
            seed.Users.Add(root);
            seed.SaveChanges();
            rootPublicId = root.PublicId;
        }

        var caller = new FakeCurrentUser { Id = rootPublicId, IsSuperAdmin = true };
        using var ctx = Ctx(caller, db);
        var result = await BuildEraseService(caller, ctx).RequestEraseLinkAsync();

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task ConfirmErase_ValidToken_Tombstones_ScrubsInvites()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var (ws, memberPublicId) = SeedPasswordlessMember(db, superAdmin);
        using (var seed = Ctx(superAdmin, db))
        {
            seed.Invites.Add(new Invite { OwnerId = ws.OwnerId, Code = "px", Email = "member@t.com", ExpiresAt = DateTime.UtcNow.AddDays(7), CreatedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }

        var email = new CapturingEmail();
        var caller = new FakeCurrentUser { Id = memberPublicId, TenantId = ws.OwnerId };
        string token;
        using (var ctx = Ctx(caller, db))
        {
            var request = await BuildEraseService(caller, ctx, email).RequestEraseLinkAsync();
            Assert.True(request.IsSuccess);
            token = CapturingEmail.ExtractToken(email.Sent[0].Html);
        }

        using (var anonCtx = Ctx(new FakeCurrentUser(), db))
        {
            var confirm = await BuildEraseService(new FakeCurrentUser(), anonCtx).EraseByTokenAsync(token);
            Assert.True(confirm.IsSuccess, confirm.Message);
        }

        using var check = Ctx(superAdmin, db);
        var userRow = check.Users.IgnoreQueryFilters().Single(u => u.PublicId == memberPublicId);
        Assert.NotNull(userRow.ErasedAt);
        Assert.StartsWith("erased+", userRow.Email);
        var inviteRow = check.Invites.IgnoreQueryFilters().Single(i => i.Code == "px");
        Assert.StartsWith("erased+", inviteRow.Email);
    }

    [Fact]
    public async Task ConfirmErase_ReusedToken_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var (ws, memberPublicId) = SeedPasswordlessMember(db, superAdmin);

        var email = new CapturingEmail();
        var caller = new FakeCurrentUser { Id = memberPublicId, TenantId = ws.OwnerId };
        string token;
        using (var ctx = Ctx(caller, db))
        {
            var request = await BuildEraseService(caller, ctx, email).RequestEraseLinkAsync();
            Assert.True(request.IsSuccess);
            token = CapturingEmail.ExtractToken(email.Sent[0].Html);
        }

        using (var ctx = Ctx(new FakeCurrentUser(), db))
        {
            var first = await BuildEraseService(new FakeCurrentUser(), ctx).EraseByTokenAsync(token);
            Assert.True(first.IsSuccess);
        }

        using (var ctx = Ctx(new FakeCurrentUser(), db))
        {
            var second = await BuildEraseService(new FakeCurrentUser(), ctx).EraseByTokenAsync(token);
            Assert.False(second.IsSuccess);
            Assert.Equal(MessageKeys.User.EraseLinkInvalid, second.Message);
        }
    }

    [Fact]
    public async Task ConfirmErase_ResetTokenRejected()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var (ws, memberPublicId) = SeedPasswordlessMember(db, superAdmin);

        Guid stamp;
        using (var check = Ctx(superAdmin, db))
            stamp = check.Users.IgnoreQueryFilters().Single(u => u.PublicId == memberPublicId).SecurityStamp;

        var resetToken = RealResetTokens().Create(memberPublicId, stamp);

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var result = await BuildEraseService(new FakeCurrentUser(), ctx).EraseByTokenAsync(resetToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EraseLinkInvalid, result.Message);

        using var check2 = Ctx(superAdmin, db);
        Assert.Null(check2.Users.IgnoreQueryFilters().Single(u => u.PublicId == memberPublicId).ErasedAt);
    }

    [Fact]
    public async Task ConfirmErase_PasswordAccountToken_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed); // Member is a normal password account (not passwordless).

        var token = RealResetTokens().CreateScoped(ws.Member.PublicId, ws.Member.SecurityStamp, TokenPurposes.Erase);

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var result = await BuildEraseService(new FakeCurrentUser(), ctx).EraseByTokenAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EraseLinkInvalid, result.Message);
    }

    [Fact]
    public async Task ConfirmErase_SoleAdmin_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid ownerId;
        Guid publicId;
        Guid stamp;
        using (var seed = Ctx(superAdmin, db))
        {
            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();
            var identity = new User { Email = "solepasswordless@t.com", PasswordHash = "h:pw", DisplayName = "Sole", PublicId = Guid.NewGuid(), RoleId = adminRole.Id, IsActive = true, PasswordlessOnly = true };
            seed.Users.Add(identity);
            seed.SaveChanges();
            ownerId = SeedSoleAdminWorkspace(seed, "Sole WS", identity, adminRole);
            publicId = identity.PublicId;
            stamp = identity.SecurityStamp;
        }

        var token = RealResetTokens().CreateScoped(publicId, stamp, TokenPurposes.Erase);

        using var ctx = Ctx(new FakeCurrentUser(), db);
        var result = await BuildEraseService(new FakeCurrentUser(), ctx).EraseByTokenAsync(token);

        Assert.True(result.IsConflict);
    }
}
