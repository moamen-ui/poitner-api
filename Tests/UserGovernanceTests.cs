using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.User;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DeleteAsync's authorization matrix and TransferOwnershipAsync's succession mechanic — the two
/// new user-governance actions layered on top of the "Workspace Admin Deputy" role.
/// </summary>
public class UserGovernanceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
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

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
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

    // InMemory provider throws on BeginTransactionAsync unless the transaction warning is ignored —
    // required because TransferOwnershipAsync's role swap runs inside ExecuteInTransactionAsync.
    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(db)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options, u, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static UserService Svc(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new UserService(uow, new IdentityHasher(), user, new NoopEmail(),
            new EntitlementService(uow, user, new FakeSettings()), new NoopBrandingService());
    }

    private sealed class Workspace
    {
        public int AdminRoleId;
        public int DeputyRoleId;
        public int MemberRoleId;
        public Guid OwnerId;
        public Guid AdminPublicId;
        public Guid DeputyPublicId;
        public Guid Deputy2PublicId;
        public Guid MemberPublicId;
        public int MemberRowId;
        public int DeputyRowId;
        public int Deputy2RowId;
    }

    // Seeds one tenant with an admin, two deputies, and one regular member.
    private static Workspace SeedWorkspace(string db)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
        var deputyRole = new Role { Name = "Workspace Admin Deputy", GrantsAdmin = true, IsSystem = true, IsActive = true };
        var memberRole = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
        seed.Roles.AddRange(adminRole, deputyRole, memberRole);
        seed.SaveChanges();

        var ownerId = Guid.NewGuid();
        var admin = new User { Email = "admin@t.com", PasswordHash = "h", DisplayName = "Admin", PublicId = ownerId, OwnerId = ownerId, RoleId = adminRole.Id, IsActive = true };
        var deputy = new User { Email = "deputy@t.com", PasswordHash = "h", DisplayName = "Deputy", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = deputyRole.Id, IsActive = true };
        var deputy2 = new User { Email = "deputy2@t.com", PasswordHash = "h", DisplayName = "Deputy2", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = deputyRole.Id, IsActive = true };
        var member = new User { Email = "member@t.com", PasswordHash = "h", DisplayName = "Member", PublicId = Guid.NewGuid(), OwnerId = ownerId, RoleId = memberRole.Id, IsActive = true };
        seed.Users.AddRange(admin, deputy, deputy2, member);
        seed.SaveChanges();

        return new Workspace
        {
            AdminRoleId = adminRole.Id, DeputyRoleId = deputyRole.Id, MemberRoleId = memberRole.Id,
            OwnerId = ownerId, AdminPublicId = admin.PublicId, DeputyPublicId = deputy.PublicId,
            Deputy2PublicId = deputy2.PublicId, MemberPublicId = member.PublicId,
            MemberRowId = member.Id, DeputyRowId = deputy.Id, Deputy2RowId = deputy2.Id,
        };
    }

    // ── DeleteAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SuperAdmin_CanDelete_RegularMember()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var result = await Svc(superAdmin, Ctx(superAdmin, db)).DeleteAsync(ws.MemberRowId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_CanDelete_Deputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var result = await Svc(superAdmin, Ctx(superAdmin, db)).DeleteAsync(ws.DeputyRowId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_CannotDelete_CurrentWorkspaceAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var ctx = Ctx(superAdmin, db);
        var adminRowId = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.AdminPublicId).Id;

        var result = await Svc(superAdmin, ctx).DeleteAsync(adminRowId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_CannotDelete_Self()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var superAdmin = new FakeCurrentUser { Id = ws.MemberPublicId, IsSuperAdmin = true };
        var result = await Svc(superAdmin, Ctx(superAdmin, db)).DeleteAsync(ws.MemberRowId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task WorkspaceAdmin_CanDelete_RegularMember_InOwnTenant()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(admin, Ctx(admin, db)).DeleteAsync(ws.MemberRowId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task WorkspaceAdmin_CanDelete_Deputy_InOwnTenant()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(admin, Ctx(admin, db)).DeleteAsync(ws.DeputyRowId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task WorkspaceAdmin_CannotDelete_Self()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var adminRowId = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.AdminPublicId).Id;

        var result = await Svc(admin, ctx).DeleteAsync(adminRowId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task WorkspaceAdmin_CannotDelete_UserInAnotherTenant()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        // A different tenant entirely — the standard EF query filter must make this row unreachable.
        var otherAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), IsAdmin = true };
        var result = await Svc(otherAdmin, Ctx(otherAdmin, db)).DeleteAsync(ws.MemberRowId);
        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task Deputy_CanDelete_RegularMember()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var deputy = new FakeCurrentUser { Id = ws.DeputyPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(deputy, Ctx(deputy, db)).DeleteAsync(ws.MemberRowId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Deputy_CannotDelete_AnotherDeputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var deputy = new FakeCurrentUser { Id = ws.DeputyPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(deputy, Ctx(deputy, db)).DeleteAsync(ws.Deputy2RowId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Deputy_CannotDelete_WorkspaceAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var deputy = new FakeCurrentUser { Id = ws.DeputyPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(deputy, db);
        var adminRowId = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.AdminPublicId).Id;

        var result = await Svc(deputy, ctx).DeleteAsync(adminRowId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Deputy_CannotDelete_Self()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var deputy = new FakeCurrentUser { Id = ws.DeputyPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(deputy, Ctx(deputy, db)).DeleteAsync(ws.DeputyRowId);
        Assert.False(result.IsSuccess);
    }

    // ── TransferOwnershipAsync ──────────────────────────────────────────────

    [Fact]
    public async Task Admin_CanPromote_OwnDeputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);

        var result = await Svc(admin, ctx).TransferOwnershipAsync(ws.DeputyPublicId);
        Assert.True(result.IsSuccess);

        var newAdmin = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.DeputyPublicId);
        var oldAdmin = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.AdminPublicId);
        Assert.Equal(ws.AdminRoleId, newAdmin.RoleId);
        Assert.Equal(ws.DeputyRoleId, oldAdmin.RoleId);
        // OwnerId is untouched for both — the tenant identifier never moves.
        Assert.Equal(ws.OwnerId, newAdmin.OwnerId);
        Assert.Equal(ws.OwnerId, oldAdmin.OwnerId);
    }

    [Fact]
    public async Task SuperAdmin_CanPromote_AnyDeputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var result = await Svc(superAdmin, Ctx(superAdmin, db)).TransferOwnershipAsync(ws.DeputyPublicId);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Deputy_CannotPromote_AnotherDeputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var deputy = new FakeCurrentUser { Id = ws.DeputyPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(deputy, Ctx(deputy, db)).TransferOwnershipAsync(ws.Deputy2PublicId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Admin_CannotPromote_RegularMember()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(admin, Ctx(admin, db)).TransferOwnershipAsync(ws.MemberPublicId);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Admin_CannotPromote_AnotherTenantsDeputy()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        // A second, unrelated workspace with its own deputy.
        var otherOwnerId = Guid.NewGuid();
        int deputyRoleId;
        Guid otherDeputyPublicId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            deputyRoleId = seed.Roles.Single(r => r.Name == "Workspace Admin Deputy").Id;
            var adminRoleId = seed.Roles.Single(r => r.Name == "Workspace Admin").Id;
            var otherAdmin = new User { Email = "other-admin@t.com", PasswordHash = "h", DisplayName = "OtherAdmin", PublicId = otherOwnerId, OwnerId = otherOwnerId, RoleId = adminRoleId, IsActive = true };
            var otherDeputy = new User { Email = "other-deputy@t.com", PasswordHash = "h", DisplayName = "OtherDeputy", PublicId = Guid.NewGuid(), OwnerId = otherOwnerId, RoleId = deputyRoleId, IsActive = true };
            seed.Users.AddRange(otherAdmin, otherDeputy);
            seed.SaveChanges();
            otherDeputyPublicId = otherDeputy.PublicId;
        }

        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var result = await Svc(admin, Ctx(admin, db)).TransferOwnershipAsync(otherDeputyPublicId);
        Assert.False(result.IsSuccess);
    }

    // ── TenantService.ListAsync stays correct across succession ────────────

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string o, string p, Stream c, string e) => Task.FromResult("");
        public Task DeleteAsync(string x) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string o) => Task.CompletedTask;
    }

    [Fact]
    public async Task TenantList_AfterPromotion_OwnerIdStaysStable_PublicIdTracksNewAdmin()
    {
        // Regression guard for the gap TenantResponse.OwnerId was added to close: before that fix,
        // the super-admin workspace picker had only PublicId to send back as TargetOwnerId — which
        // is the CURRENT admin's own row id, not the tenant's stable identifier. After a promotion
        // those two diverge, so a picker built on PublicId would silently target the wrong tenant.
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var transfer = await Svc(admin, Ctx(admin, db)).TransferOwnershipAsync(ws.DeputyPublicId);
        Assert.True(transfer.IsSuccess);

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var tenantSvc = new TenantService(new UnitOfWork(Ctx(superAdmin, db)), new IdentityHasher(),
            new NoopFileStorage(), new FakeSettings(), new NoopBillingProvider());

        var list = await tenantSvc.ListAsync();
        Assert.True(list.IsSuccess);
        var row = list.Data!.Single(t => t.OwnerId == ws.OwnerId);

        // OwnerId never moves; PublicId now identifies the newly promoted admin (the former deputy).
        Assert.Equal(ws.OwnerId, row.OwnerId);
        Assert.Equal(ws.DeputyPublicId, row.PublicId);
        Assert.NotEqual(ws.AdminPublicId, row.PublicId);
    }

    // ── UpdateAsync: self-demotion guard + role-change stamp rotation ──────────

    [Fact]
    public async Task Admin_CannotChangeOwnRole_AwayFromWorkspaceAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var adminRowId = ctx.Users.Single(u => u.PublicId == ws.AdminPublicId).Id;

        var result = await Svc(admin, ctx).UpdateAsync(adminRowId, new UpdateUserRequest { RoleId = ws.MemberRoleId });

        Assert.False(result.IsSuccess);
        var stillAdmin = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == ws.AdminPublicId);
        Assert.Equal(ws.AdminRoleId, stillAdmin.RoleId);
    }

    [Fact]
    public async Task Admin_CannotChangeOwnRole_ToDeputy_MustUseTransferOwnershipInstead()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var adminRowId = ctx.Users.Single(u => u.PublicId == ws.AdminPublicId).Id;

        var result = await Svc(admin, ctx).UpdateAsync(adminRowId, new UpdateUserRequest { RoleId = ws.DeputyRoleId });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Admin_CanChangeAnotherUsersRole_UnaffectedBySelfDemotionGuard()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);

        var result = await Svc(admin, ctx).UpdateAsync(ws.MemberRowId, new UpdateUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task UpdateAsync_RoleChange_RotatesSecurityStamp()
    {
        // A role change alters is_admin/is_super_admin/is_quick_access baked into the JWT — the
        // stamp must rotate so a live session can't keep acting under the old role.
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var before = ctx.Users.IgnoreQueryFilters().Single(u => u.Id == ws.MemberRowId).SecurityStamp;

        var result = await Svc(admin, ctx).UpdateAsync(ws.MemberRowId, new UpdateUserRequest { RoleId = ws.DeputyRoleId });

        Assert.True(result.IsSuccess);
        var after = ctx.Users.IgnoreQueryFilters().Single(u => u.Id == ws.MemberRowId).SecurityStamp;
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task UpdateAsync_NoRoleChange_DoesNotRotateSecurityStamp()
    {
        var db = Guid.NewGuid().ToString();
        var ws = SeedWorkspace(db);
        var admin = new FakeCurrentUser { Id = ws.AdminPublicId, TenantId = ws.OwnerId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var before = ctx.Users.IgnoreQueryFilters().Single(u => u.Id == ws.MemberRowId).SecurityStamp;

        // Same role the member already has — no-op role assignment, nothing else in the request.
        var result = await Svc(admin, ctx).UpdateAsync(ws.MemberRowId, new UpdateUserRequest { RoleId = ws.MemberRoleId });

        Assert.True(result.IsSuccess);
        var after = ctx.Users.IgnoreQueryFilters().Single(u => u.Id == ws.MemberRowId).SecurityStamp;
        Assert.Equal(before, after);
    }
}
