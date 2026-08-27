using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Project;
using Pointer.Application.DTOs.User;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// A super-admin directly creating a user with the global "Workspace Admin" role (the
/// self-owning tenant-owner role — see AuthService.RegisterAdminAsync) must stamp that new
/// user as owning a BRAND NEW workspace (OwnerId == its own PublicId), not inherit the caller's
/// tenant (null, for a super-admin). Getting this wrong leaves the new admin tenant-less: no
/// `tenant` JWT claim, and everything they create afterwards (e.g. projects) is stamped with a
/// throwaway id that never matches their own null-tenant query-filter scope — it "creates" but
/// is immediately invisible to them.
/// </summary>
public class WorkspaceAdminOwnershipTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
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

    [Fact]
    public async Task SuperAdmin_DirectAdd_WorkspaceAdminRole_SelfOwns_NotNull()
    {
        var db = Guid.NewGuid().ToString();

        int roleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var ctx = Ctx(superAdmin, db);
        var uow = new UnitOfWork(ctx);
        var svc = new UserService(uow, new IdentityHasher(), superAdmin, new NoopEmail(),
            new EntitlementService(uow, superAdmin, new FakeSettings()), new NoopBrandingService());

        var result = await svc.CreateAsync(new CreateUserRequest
        { Email = "wa@tuwaiq.edu.sa", Password = "password123", DisplayName = "New WA", RoleId = roleId });
        Assert.True(result.IsSuccess);

        var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "wa@tuwaiq.edu.sa");
        Assert.NotNull(created.OwnerId);
        Assert.Equal(created.PublicId, created.OwnerId);
    }

    [Fact]
    public async Task SuperAdmin_DirectAdd_OrdinaryRole_SelfOwns_NotNull()
    {
        // Regression for the narrower sibling bug: adding an ordinary (non-"Workspace Admin") user
        // used to fall back to TenantStamp.OwnerFor(_currentUser) with NO `?? _currentUser.Id`,
        // which is null for a super-admin caller — the new teammate was created tenant-less and
        // invisible to their own workspace admin (non-super tenant users can never match a
        // null-owner row in the query filter).
        var db = Guid.NewGuid().ToString();

        int roleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var ctx = Ctx(superAdmin, db);
        var uow = new UnitOfWork(ctx);
        var svc = new UserService(uow, new IdentityHasher(), superAdmin, new NoopEmail(),
            new EntitlementService(uow, superAdmin, new FakeSettings()), new NoopBrandingService());

        var result = await svc.CreateAsync(new CreateUserRequest
        { Email = "member@tuwaiq.edu.sa", Password = "password123", DisplayName = "New Member", RoleId = roleId });
        Assert.True(result.IsSuccess);

        var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "member@tuwaiq.edu.sa");
        Assert.NotNull(created.OwnerId);
        Assert.Equal(superAdmin.Id, created.OwnerId);
    }

    [Fact]
    public async Task NewWorkspaceAdmin_CanCreateAndThenSeeTheirOwnProject()
    {
        var db = Guid.NewGuid().ToString();

        int roleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        Guid createdPublicId;
        using (var ctx = Ctx(superAdmin, db))
        {
            var uow = new UnitOfWork(ctx);
            var svc = new UserService(uow, new IdentityHasher(), superAdmin, new NoopEmail(),
                new EntitlementService(uow, superAdmin, new FakeSettings()), new NoopBrandingService());
            var result = await svc.CreateAsync(new CreateUserRequest
            { Email = "wa2@tuwaiq.edu.sa", Password = "password123", DisplayName = "New WA", RoleId = roleId });
            Assert.True(result.IsSuccess);
            createdPublicId = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "wa2@tuwaiq.edu.sa").PublicId;
        }

        // Now act AS that new workspace admin (tenant == their own PublicId, per JwtTokenService).
        var newAdmin = new FakeCurrentUser { Id = createdPublicId, TenantId = createdPublicId, IsAdmin = true };
        using var actingCtx = Ctx(newAdmin, db);
        var actingUow = new UnitOfWork(actingCtx);
        var projects = new ProjectService(actingUow, newAdmin, new EntitlementService(actingUow, newAdmin, new FakeSettings()));

        var create = await projects.CreateAsync(new CreateProjectRequest { Key = "lms", Name = "tuwaiq lms" });
        Assert.True(create.IsSuccess);

        var list = await projects.ListAsync();
        Assert.True(list.IsSuccess);
        Assert.Contains(list.Data!, p => p.Key == "lms");
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
