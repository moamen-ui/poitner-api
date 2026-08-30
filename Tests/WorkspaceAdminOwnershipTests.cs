using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.User;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// UserService.CreateAsync's ownership rules, post-redesign: a super admin can no longer self-own
/// a workspace via this endpoint (or assign any role other than "Workspace Admin Deputy") — they
/// must pick an EXISTING workspace (TargetOwnerId) and the new user is always forced to Deputy,
/// regardless of the requested RoleId. A Workspace Admin adding someone to their own tenant is
/// unchanged, except they can now also delegate the Deputy role (previously blocked by the
/// escalation guard like any other GrantsAdmin role).
/// </summary>
public class WorkspaceAdminOwnershipTests
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

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options, u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static UserService Svc(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new UserService(uow, new IdentityHasher(), user, new NoopEmail(),
            new EntitlementService(uow, user, new FakeSettings()), new NoopBrandingService());
    }

    // Seeds the two global admin-tier roles plus an existing self-owned workspace (its "Workspace
    // Admin" row) — the workspace a super admin will target in the tests below.
    private static (int workspaceAdminRoleId, int deputyRoleId, Guid existingWorkspaceOwnerId) SeedWorkspace(string db)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
        var deputyRole = new Role { Name = "Workspace Admin Deputy", GrantsAdmin = true, IsSystem = true, IsActive = true };
        seed.Roles.AddRange(adminRole, deputyRole);
        seed.SaveChanges();

        var ownerId = Guid.NewGuid();
        seed.Users.Add(new User
        {
            Email = "founder@tuwaiq.edu.sa", PasswordHash = "h", DisplayName = "Founder",
            PublicId = ownerId, OwnerId = ownerId, RoleId = adminRole.Id, IsActive = true
        });
        seed.SaveChanges();

        return (adminRole.Id, deputyRole.Id, ownerId);
    }

    [Fact]
    public async Task SuperAdmin_DirectAdd_RequiresTargetWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        var (adminRoleId, _, _) = SeedWorkspace(db);

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var svc = Svc(superAdmin, Ctx(superAdmin, db));

        var result = await svc.CreateAsync(new CreateUserRequest
        { Email = "x@tuwaiq.edu.sa", Password = "password123", DisplayName = "X", RoleId = adminRoleId });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_DirectAdd_RejectsUnknownWorkspace()
    {
        var db = Guid.NewGuid().ToString();
        var (adminRoleId, _, _) = SeedWorkspace(db);

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var svc = Svc(superAdmin, Ctx(superAdmin, db));

        var result = await svc.CreateAsync(new CreateUserRequest
        {
            Email = "x@tuwaiq.edu.sa", Password = "password123", DisplayName = "X", RoleId = adminRoleId,
            TargetOwnerId = Guid.NewGuid() // no such workspace
        });

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task SuperAdmin_DirectAdd_AssignsDeputyToExistingWorkspace_IgnoringRequestedRole()
    {
        var db = Guid.NewGuid().ToString();
        var (adminRoleId, deputyRoleId, existingWorkspaceOwnerId) = SeedWorkspace(db);

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var ctx = Ctx(superAdmin, db);
        var svc = Svc(superAdmin, ctx);

        // Requests the primary "Workspace Admin" role explicitly — must be ignored and forced to
        // Deputy regardless, proving super admins can never mint/co-own a primary admin via this path.
        var result = await svc.CreateAsync(new CreateUserRequest
        {
            Email = "deputy@tuwaiq.edu.sa", Password = "password123", DisplayName = "New Deputy",
            RoleId = adminRoleId, TargetOwnerId = existingWorkspaceOwnerId
        });

        Assert.True(result.IsSuccess);
        var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "deputy@tuwaiq.edu.sa");
        Assert.Equal(deputyRoleId, created.RoleId);
        Assert.Equal(existingWorkspaceOwnerId, created.OwnerId);
    }

    [Fact]
    public async Task WorkspaceAdmin_DirectAdd_AssignsToOwnTenant_Unchanged()
    {
        var db = Guid.NewGuid().ToString();

        int engineerRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            engineerRoleId = role.Id;
        }

        var tenantId = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var svc = Svc(admin, ctx);

        var result = await svc.CreateAsync(new CreateUserRequest
        { Email = "member@tuwaiq.edu.sa", Password = "password123", DisplayName = "New Member", RoleId = engineerRoleId });

        Assert.True(result.IsSuccess);
        var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "member@tuwaiq.edu.sa");
        Assert.Equal(tenantId, created.OwnerId);
    }

    [Fact]
    public async Task WorkspaceAdmin_DirectAdd_CanDelegateDeputy()
    {
        var db = Guid.NewGuid().ToString();

        int deputyRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Workspace Admin Deputy", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            deputyRoleId = role.Id;
        }

        var tenantId = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenantId, IsAdmin = true };
        var ctx = Ctx(admin, db);
        var svc = Svc(admin, ctx);

        // Previously blocked by the blanket escalation guard (any GrantsAdmin role was off-limits
        // to a non-super-admin caller) — now explicitly carved out for Deputy.
        var result = await svc.CreateAsync(new CreateUserRequest
        { Email = "deputy2@tuwaiq.edu.sa", Password = "password123", DisplayName = "New Deputy", RoleId = deputyRoleId });

        Assert.True(result.IsSuccess);
        var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "deputy2@tuwaiq.edu.sa");
        Assert.Equal(deputyRoleId, created.RoleId);
        Assert.Equal(tenantId, created.OwnerId);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

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
}
