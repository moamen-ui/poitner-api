using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11f follow-up F1 (in scope for Part A, owner header): every membership role write refuses the
/// super-admin role and any role owned by a workspace other than the membership's own — including
/// for a super-admin caller, who is otherwise exempt from the ordinary GrantsAdmin/IsSuperAdmin
/// escalation guard (cross-review Opus MEDIUM O4/O5). Covers UserService.ApproveAsync/UpdateAsync;
/// RoleService.DeleteAsync's reassignment guard is covered by RoleServiceDeleteTests.
/// </summary>
public class Db11fRoleWriteGuardTests
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

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

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
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class NoopBrandingService : IBrandingService
    {
        private static Pointer.Application.DTOs.Branding.BrandingResponse DefaultBranding() =>
            new()
            {
                ProductName = "Pointer",
                Tagline = string.Empty,
                PrimaryColor = "#2563eb",
                Urls = new Pointer.Application.DTOs.Branding.BrandingUrlsResponse
                {
                    App = "https://app.pointer.moamen.work",
                },
                Assets = new Pointer.Application.DTOs.Branding.BrandingAssetsResponse(),
            };

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(DefaultBranding());
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static UserService Svc(ICurrentUser user, AppDbContext ctx)
    {
        var uow = new UnitOfWork(ctx);
        return new UserService(
            uow,
            new IdentityHasher(),
            user,
            new NoopEmail(),
            new EntitlementService(uow, user, new FakeSettings()),
            new NoopBrandingService(),
            new MembershipService(uow)
        );
    }

    private sealed record Seeded(
        Guid Workspace,
        Guid OtherWorkspace,
        int MemberId,
        int OwnRoleId,
        int ForeignRoleId,
        int SuperAdminRoleId
    );

    // A member with a live (Pending) membership in `Workspace`, holding a non-admin role that
    // `Workspace` owns. `ForeignRoleId` is owned by a DIFFERENT workspace; `SuperAdminRoleId` is the
    // global platform role. Neither may ever be written onto the membership.
    private static Seeded Seed(string dbName)
    {
        var workspace = Guid.NewGuid();
        var otherWorkspace = Guid.NewGuid();
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var ownRole = new Role
        {
            Name = "OwnDev",
            OwnerId = workspace,
            IsActive = true,
            GrantsAdmin = false,
        };
        var foreignRole = new Role
        {
            Name = "ForeignDev",
            OwnerId = otherWorkspace,
            IsActive = true,
            GrantsAdmin = false,
        };
        var superAdminRole = new Role
        {
            Name = "SA",
            OwnerId = null,
            IsActive = true,
            IsSuperAdmin = true,
        };
        seed.Roles.AddRange(ownRole, foreignRole, superAdminRole);
        seed.SaveChanges();

        var member = new User
        {
            Email = "member@w.com",
            PasswordHash = "x",
            DisplayName = "Member",
            RoleId = ownRole.Id,
            PublicId = Guid.NewGuid(),
            IsActive = true,
        };
        seed.Users.Add(member);
        seed.SaveChanges();
        TestSeed.Join(
            seed,
            member,
            workspace,
            ownRole,
            isActive: true,
            status: ApprovalStatus.Pending
        );

        return new Seeded(
            workspace,
            otherWorkspace,
            member.Id,
            ownRole.Id,
            foreignRole.Id,
            superAdminRole.Id
        );
    }

    [Fact]
    public async Task ApproveAsync_RefusesSuperAdminRole_EvenForSuperAdminCaller()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, dbName);
        var svc = Svc(superAdmin, db);

        var result = await svc.ApproveAsync(
            s.MemberId,
            new ApproveUserRequest { RoleId = s.SuperAdminRoleId }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        var membership = db.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == s.MemberId);
        Assert.Equal(s.OwnRoleId, membership.RoleId);
        Assert.Equal(ApprovalStatus.Pending, membership.ApprovalStatus);
    }

    [Fact]
    public async Task ApproveAsync_RefusesForeignWorkspaceRole_EvenForSuperAdminCaller()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, dbName);
        var svc = Svc(superAdmin, db);

        var result = await svc.ApproveAsync(
            s.MemberId,
            new ApproveUserRequest { RoleId = s.ForeignRoleId }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        var membership = db.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == s.MemberId);
        Assert.Equal(s.OwnRoleId, membership.RoleId);
    }

    [Fact]
    public async Task UpdateAsync_RefusesSuperAdminRole_EvenForSuperAdminCaller()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, dbName);
        var svc = Svc(superAdmin, db);

        var result = await svc.UpdateAsync(
            s.MemberId,
            new UpdateUserRequest { RoleId = s.SuperAdminRoleId }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        var membership = db.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == s.MemberId);
        Assert.Equal(s.OwnRoleId, membership.RoleId);
    }

    [Fact]
    public async Task UpdateAsync_RefusesForeignWorkspaceRole_EvenForSuperAdminCaller()
    {
        var dbName = Guid.NewGuid().ToString();
        var s = Seed(dbName);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, dbName);
        var svc = Svc(superAdmin, db);

        var result = await svc.UpdateAsync(
            s.MemberId,
            new UpdateUserRequest { RoleId = s.ForeignRoleId }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        var membership = db.Set<WorkspaceMembership>()
            .IgnoreQueryFilters()
            .Single(m => m.UserId == s.MemberId);
        Assert.Equal(s.OwnRoleId, membership.RoleId);
    }

    // ── UserService.CreateAsync (F1 gap closed this review round) ──────────────────────────

    [Fact]
    public async Task CreateAsync_NonSuperCaller_RefusesSuperAdminRoleViaRoleId()
    {
        // The Role query filter's "own-plus-global" bucket makes the (global) super-admin role
        // VISIBLE to a non-super caller's GetActiveRoleAsync(id) lookup — F1's unified check (added
        // once, right after both CreateAsync branches resolve role+ownerId) refuses it exactly like
        // ApproveAsync/UpdateAsync, on top of the pre-existing GrantsAdmin/IsSuperAdmin guard.
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int superAdminRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Domain.Entity.Workspace
                {
                    Id = tenant,
                    Name = "T",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                }
            );
            var superRole = new Role
            {
                Name = "SA",
                IsSuperAdmin = true,
                GrantsAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(superRole);
            seed.SaveChanges();
            superAdminRoleId = superRole.Id;
        }

        var admin = new FakeCurrentUser { TenantId = tenant, IsAdmin = true };
        using var ctx = Ctx(admin, dbName);
        var svc = Svc(admin, ctx);

        var result = await svc.CreateAsync(
            new CreateUserRequest
            {
                Email = "escalator@x.com",
                Password = "irrelevant",
                DisplayName = "Escalator",
                RoleId = superAdminRoleId,
            }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
        Assert.Empty(ctx.Users.IgnoreQueryFilters().Where(u => u.Email == "escalator@x.com"));
    }

    [Fact]
    public async Task CreateAsync_SuperAdminCaller_RefusesIfDeputyLookupResolvesARogueSuperAdminRole()
    {
        // Defense-in-depth for the CreateAsync super-admin branch (task item 1): even if a rogue
        // GLOBAL role happened to share the real Deputy role's name (a data-corruption scenario —
        // the name lookup itself is now scoped to OwnerId == null, task item 2's fix, so this can
        // only still bite if the FIRST OwnerId==null match by that name is the rogue row), the F1
        // check right after both branches refuses a super-admin role rather than minting a
        // membership that carries it.
        var dbName = Guid.NewGuid().ToString();
        var target = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Domain.Entity.Workspace
                {
                    Id = target,
                    Name = "Target",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = target,
                }
            );
            // Rogue row inserted FIRST (lower Id) so an unordered FirstOrDefault by name would pick
            // it ahead of the real global Deputy role seeded further below.
            var rogueDeputy = new Role
            {
                Name = "Workspace Admin Deputy",
                OwnerId = null,
                IsSuperAdmin = true,
                GrantsAdmin = true,
                IsActive = true,
            };
            var realAdminRole = new Role
            {
                Name = "Workspace Admin",
                OwnerId = null,
                GrantsAdmin = true,
                IsActive = true,
            };
            seed.Roles.AddRange(rogueDeputy, realAdminRole);
            seed.SaveChanges();

            var admin = new User
            {
                Email = "target-admin@x.com",
                PasswordHash = "h",
                DisplayName = "TargetAdmin",
                PublicId = Guid.NewGuid(),
                RoleId = realAdminRole.Id,
                IsActive = true,
            };
            seed.Users.Add(admin);
            seed.SaveChanges();
            TestSeed.Join(seed, admin, target, realAdminRole);
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var ctx = Ctx(superAdmin, dbName);
        var svc = Svc(superAdmin, ctx);

        var result = await svc.CreateAsync(
            new CreateUserRequest
            {
                Email = "deputy@x.com",
                Password = "irrelevant",
                DisplayName = "Deputy",
                TargetOwnerId = target,
            }
        );

        // Either outcome proves the fix: if the name lookup's OwnerId == null scoping still let a
        // rogue row through (this test deliberately makes the rogue row OwnerId == null too, the
        // worst case), F1 refuses it outright before any user/membership is created. What must
        // NEVER happen is a membership carrying the super-admin role.
        if (result.IsSuccess)
        {
            var created = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "deputy@x.com");
            Assert.False(
                ctx.Roles.IgnoreQueryFilters().Single(r => r.Id == created.RoleId).IsSuperAdmin
            );
        }
        else
        {
            Assert.Equal(MessageKeys.Role.EscalationNotAllowed, result.Message);
            Assert.Empty(ctx.Users.IgnoreQueryFilters().Where(u => u.Email == "deputy@x.com"));
        }
    }
}
