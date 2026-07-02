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
        return new InviteService(uow, user, new FakePasswordHasher(), new FakeTokenService(), new FakeSettings(), new PassThroughEntitlements());
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

    // ── H1: atomic MaxUses ────────────────────────────────────────────────────────

    [Fact]
    public async Task Accept_SingleUseInvite_AcceptedOnce_SecondReject()
    {
        // A MaxUses=1 invite: first accept succeeds and exhausts it; second accept is rejected
        // (the atomic increment sees Uses=1 >= MaxUses=1 and returns claimed=0).
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.MaxUses = 1; });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        // First accept succeeds.
        var first = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "first@user.com", Password = "password123", DisplayName = "First" });
        Assert.True(first.IsSuccess);

        var invite = db.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId);
        Assert.Equal(1, invite.Uses); // slot consumed

        // Second accept (different email, same code) must be rejected — slot exhausted.
        var second = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "second@user.com", Password = "password123", DisplayName = "Second" });
        Assert.False(second.IsSuccess);
        Assert.True(second.IsNotFound); // exhausted → same NotFound as invalid code
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "second@user.com"));
    }

    // ── M2: validation / null guards ─────────────────────────────────────────────

    [Fact]
    public async Task Accept_NullPassword_ReturnsFailure_Not500()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        // Simulates a JSON body with "password": null overriding the default string.Empty.
        var req = new AcceptInviteRequest { Code = code, Email = "x@x.com", DisplayName = "X" };
        req.GetType().GetProperty(nameof(AcceptInviteRequest.Password))!.SetValue(req, null!);

        var result = await svc.AcceptAsync(req);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsNotFound); // must be a validation failure (400), not 404/500
    }

    [Fact]
    public async Task Accept_ShortPassword_ReturnsFailure()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "x@x.com", Password = "short", DisplayName = "X" });

        Assert.False(result.IsSuccess);
        Assert.False(result.IsNotFound);
        Assert.Empty(db.Users.IgnoreQueryFilters().Where(u => u.Email == "x@x.com"));
    }

    [Fact]
    public async Task Accept_NullEmail_ReturnsFailure_Not500()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var req = new AcceptInviteRequest { Code = code, Password = "password123", DisplayName = "X" };
        req.GetType().GetProperty(nameof(AcceptInviteRequest.Email))!.SetValue(req, null!);

        var result = await svc.AcceptAsync(req);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsNotFound);
    }

    [Fact]
    public async Task Accept_NullDisplayName_ReturnsFailure_Not500()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => i.RoleId = roleId);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var req = new AcceptInviteRequest { Code = code, Email = "x@x.com", Password = "password123" };
        req.GetType().GetProperty(nameof(AcceptInviteRequest.DisplayName))!.SetValue(req, null!);

        var result = await svc.AcceptAsync(req);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsNotFound);
    }

    // ── M1: same-email under a different tenant is allowed ───────────────────────

    [Fact]
    public async Task Accept_SameEmail_DifferentTenant_IsAllowed_NotAccountExists()
    {
        // A user already exists in tenantA with email "shared@email.com". A completely separate
        // tenantB invite should allow the same email to register as a tenantB user (separate row
        // with the same email but different OwnerId). Cross-tenant existence must NOT leak as 409.
        var dbName = Guid.NewGuid().ToString();
        var (tenantA, roleIdA) = SeedTenant(dbName);

        // Register a user in tenantA with the shared email.
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Users.Add(new User
            {
                Email = "shared@email.com",
                PasswordHash = "x",
                DisplayName = "TenantA User",
                RoleId = roleIdA,
                PublicId = Guid.NewGuid(),
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
                OwnerId = tenantA
            });
            seed.SaveChanges();
        }

        // Set up tenantB with its own role and invite.
        var tenantB = Guid.NewGuid();
        int roleIdB;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var roleB = new Role { Name = "Member-B", GrantsAdmin = false, IsActive = true, OwnerId = tenantB };
            seed.Roles.Add(roleB);
            // Also add a tenantB admin user so workspace name lookup works.
            var adminRoleB = new Role { Name = "Admin-B", GrantsAdmin = true, IsActive = true, OwnerId = tenantB };
            seed.Roles.Add(adminRoleB);
            seed.SaveChanges();
            roleIdB = roleB.Id;
            seed.Users.Add(new User
            {
                Email = "adminb@b.com", PasswordHash = "x", DisplayName = "TenantB Workspace",
                RoleId = adminRoleB.Id, PublicId = Guid.NewGuid(),
                ApprovalStatus = ApprovalStatus.Approved, IsActive = true, OwnerId = tenantB
            });
            seed.SaveChanges();
        }

        var inviteId = SeedInvite(dbName, tenantB, i => i.RoleId = roleIdB);
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        // Must succeed — same email, different tenant → not a conflict.
        var result = await svc.AcceptAsync(new AcceptInviteRequest
        { Code = code, Email = "shared@email.com", Password = "password123", DisplayName = "TenantB User" });

        Assert.True(result.IsSuccess);
        // Two rows exist: one per tenant, same email.
        var rows = db.Users.IgnoreQueryFilters().Where(u => u.Email == "shared@email.com").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, u => u.OwnerId == tenantA);
        Assert.Contains(rows, u => u.OwnerId == tenantB);
    }

    // ── M3: super-admin can revoke any invite ─────────────────────────────────────

    [Fact]
    public async Task SuperAdmin_CanRevoke_AnyTenantInvite()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenantA, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenantA, i => i.RoleId = roleId);

        // Super-admin has no TenantId — they must still be able to revoke the invite.
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = BuildContext(superAdmin, dbName);
        var svc = BuildService(superAdmin, db);

        var revoke = await svc.RevokeAsync(inviteId);
        Assert.True(revoke.IsSuccess);
        Assert.NotNull(db.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId).RevokedAt);
    }

    // ── L1: preview does not leak the locked email ────────────────────────────────

    [Fact]
    public async Task Preview_DoesNotLeak_LockedEmail()
    {
        var dbName = Guid.NewGuid().ToString();
        var (tenant, roleId) = SeedTenant(dbName);
        var inviteId = SeedInvite(dbName, tenant, i => { i.RoleId = roleId; i.Email = "secret@locked.com"; });
        var code = CodeOf(dbName, inviteId);

        var anon = new FakeCurrentUser { };
        using var db = BuildContext(anon, dbName);
        var svc = BuildService(anon, db);

        var result = await svc.GetPreviewAsync(code);

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.EmailLocked);
        // The raw email must NOT be present in the response DTO.
        Assert.Null(result.Data.GetType().GetProperty("Email")?.GetValue(result.Data));
    }
}
