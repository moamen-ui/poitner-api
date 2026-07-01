using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Tenant-invite feature. Covers accept (creates an Approved + active tenant-scoped user),
/// the negative paths (expired / revoked / used-up / email-mismatch / duplicate email), and the
/// tenant-isolation bar (a tenant-B admin cannot list or revoke tenant-A's invite).
/// </summary>
public class InviteServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    // Identity hasher/verifier — good enough for service-level assertions.
    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hashed:" + password;
        public bool Verify(string password, string hash) => hash == "hashed:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user) => "token-for-" + user.PublicId.ToString("N");
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user);

    private static InviteService BuildService(ICurrentUser user, AppDbContext db)
    {
        var uow = new UnitOfWork(db);
        return new InviteService(uow, user, new FakePasswordHasher(), new FakeTokenService(), new FakeSettings());
    }

    // Seeds a tenant with an admin user (for the workspace-name preview) and a non-admin role,
    // returns the tenant id + the non-admin role id.
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
            Email = "admin@a.com",
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

    // Creates an invite directly in the store (super-admin context) with the given knobs.
    private static int SeedInvite(string dbName, Guid owner, Action<Invite>? tweak = null)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var invite = new Invite
        {
            OwnerId = owner,
            Code = "code-" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Uses = 0
        };
        tweak?.Invoke(invite);
        seed.Invites.Add(invite);
        seed.SaveChanges();
        return invite.Id;
    }

    private static string CodeOf(string dbName, int inviteId)
    {
        using var s = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        return s.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId).Code;
    }

    // ── Create ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StampsNonNullOwner_AndBuildsJoinUrl()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.CreateAsync(new CreateInviteRequest { RoleId = roleId });

        Assert.True(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Data!.Code));
        Assert.Contains("/join?code=", result.Data.Url);
        Assert.Equal(roleId, result.Data.RoleId);

        var stored = db.Invites.IgnoreQueryFilters().Single();
        Assert.Equal(tenant, stored.OwnerId); // non-null tenant boundary
    }

    [Fact]
    public async Task Create_RejectsAdminRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, _) = SeedTenant(dbName);
        var adminRoleId = 0;
        using (var s = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
            adminRoleId = s.Roles.IgnoreQueryFilters().Single(r => r.GrantsAdmin && r.OwnerId == tenant).Id;

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        var result = await svc.CreateAsync(new CreateInviteRequest { RoleId = adminRoleId });
        Assert.False(result.IsSuccess);
    }

    // ── Accept (happy path) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_CreatesApprovedActiveTenantScopedUser_AndReturnsToken()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        // Anonymous accept → no tenant claim.
        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        {
            Code = code, Email = "New@User.com", Password = "password123", DisplayName = "New User"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("ok", result.Data!.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Data.Token));

        var created = db.Users.IgnoreQueryFilters().Single(u => u.Email == "new@user.com");
        Assert.Equal(ApprovalStatus.Approved, created.ApprovalStatus); // skips the pending queue
        Assert.True(created.IsActive);
        Assert.Equal(tenant, created.OwnerId);                          // pre-scoped to the tenant
        Assert.Equal(roleId, created.RoleId);

        var invite = db.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId);
        Assert.Equal(1, invite.Uses);                                   // incremented
    }

    [Fact]
    public async Task Accept_WithChosenNonAdminRole_WhenInviteDoesNotPin()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant); // no pinned role
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        {
            Code = code, Email = "pick@role.com", Password = "password123", DisplayName = "Picker", RoleId = roleId
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(roleId, db.Users.IgnoreQueryFilters().Single(u => u.Email == "pick@role.com").RoleId);
    }

    // ── Accept (negative paths) ─────────────────────────────────────────────────

    [Fact]
    public async Task Accept_Rejects_ExpiredInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.ExpiresAt = DateTime.UtcNow.AddDays(-1); });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "x@x.com", Password = "password123", DisplayName = "X" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsNotFound);
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "x@x.com"));
    }

    [Fact]
    public async Task Accept_Rejects_RevokedInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.RevokedAt = DateTime.UtcNow; });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "y@y.com", Password = "password123", DisplayName = "Y" });

        Assert.False(result.IsSuccess);
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "y@y.com"));
    }

    [Fact]
    public async Task Accept_Rejects_UsedUpInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.MaxUses = 1; i.Uses = 1; });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "z@z.com", Password = "password123", DisplayName = "Z" });

        Assert.False(result.IsSuccess);
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "z@z.com"));
    }

    [Fact]
    public async Task Accept_Rejects_EmailMismatch_WhenLocked()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.Email = "locked@a.com"; });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "someone@else.com", Password = "password123", DisplayName = "Other" });

        Assert.False(result.IsSuccess);
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "someone@else.com"));

        // The matching email is accepted (case-insensitive).
        var ok = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "Locked@A.com", Password = "password123", DisplayName = "Owner" });
        Assert.True(ok.IsSuccess);
    }

    [Fact]
    public async Task Accept_Rejects_DuplicateEmail()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        // admin@a.com already exists from SeedTenant.
        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "admin@a.com", Password = "password123", DisplayName = "Dup" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsConflict);
    }

    // ── Preview ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_ReturnsSafeWorkspaceAndRole_NoTenantGuid()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.GetPreviewAsync(code);

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Inc", result.Data!.WorkspaceName);
        Assert.Equal("Developer", result.Data.RoleName);
        // The tenant GUID must never appear in the safe preview.
        Assert.DoesNotContain(tenant.ToString(), result.Data.WorkspaceName);
    }

    [Fact]
    public async Task Preview_NotFound_ForInvalidCode()
    {
        var dbName = Guid.NewGuid().ToString();
        SeedTenant(dbName);
        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.GetPreviewAsync("does-not-exist");
        Assert.True(result.IsNotFound);
    }

    // ── Tenant isolation (the review bar) ─────────────────────────────────────────

    [Fact]
    public async Task TenantB_Admin_CannotRevoke_TenantA_Invite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenantA, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenantA, i => i.RoleId = roleId);

        // A DIFFERENT tenant's admin attempts to revoke tenant A's invite by id.
        var tenantB = Guid.NewGuid();
        var attacker = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantB, IsAdmin = true };
        using var db = BuildContext(attacker, dbName);
        var svc = BuildService(attacker, db);

        var revoke = await svc.RevokeAsync(inviteId);
        Assert.True(revoke.IsNotFound); // never reachable cross-tenant

        // The invite remains un-revoked.
        var invite = db.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId);
        Assert.Null(invite.RevokedAt);
    }

    [Fact]
    public async Task TenantB_Admin_List_DoesNotSee_TenantA_Invite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenantA, roleId) = SeedTenant(dbName);
        SeedInvite(dbName, tenantA, i => i.RoleId = roleId);

        var tenantB = Guid.NewGuid();
        var other = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantB, IsAdmin = true };
        using var db = BuildContext(other, dbName);
        var svc = BuildService(other, db);

        var list = await svc.ListAsync();
        Assert.True(list.IsSuccess);
        Assert.Empty(list.Data!); // strict-own query filter hides tenant A's invite
    }

    [Fact]
    public async Task Revoke_OwnInvite_Succeeds_AndDropsFromActiveList()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant, IsAdmin = true };
        using var db = BuildContext(admin, dbName);
        var svc = BuildService(admin, db);

        Assert.Single((await svc.ListAsync()).Data!);

        var revoke = await svc.RevokeAsync(inviteId);
        Assert.True(revoke.IsSuccess);
        Assert.NotNull(db.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId).RevokedAt);

        // Revoked invites are excluded from the active list.
        Assert.Empty((await svc.ListAsync()).Data!);
    }
}
