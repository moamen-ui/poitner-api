using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Project;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.DTOs.User;
using Pointer.Application.DTOs.Workspace;
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
/// DB-12 §6 test 6 — one fact per catalogue row, asserting the call sites inside the SERVICES
/// really write the row the catalogue promises (action, target, owner, before/after), using
/// <see cref="FakeAuditWriter"/> instead of the database. Actor-kind resolution itself (System vs
/// User vs SuperAdmin) is the real <c>AuditWriter</c>'s job and is covered by
/// <c>Tests/AuditWriterTests.cs</c> (DB-12 part 1) — these tests only prove the SERVICE supplied the
/// right <see cref="AuditEntry"/>.
/// </summary>
public class AuditWrittenByServicesTests
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
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) => "token";
        public string IssueSelection(User user) => "selection";
    }

    private sealed class FakeReset : IResetTokenService
    {
        public string Create(Guid id, Guid stamp) => "r";

        public bool TryValidate(string token, out Guid id, out Guid stamp)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            return false;
        }

        public string CreateScoped(Guid id, Guid stamp, string purpose, string? payload = null) => "r";

        public bool TryValidateScoped(string token, string purpose, out Guid id, out Guid stamp, out string? payload)
        {
            id = Guid.Empty;
            stamp = Guid.Empty;
            payload = null;
            return false;
        }
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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default) =>
            Task.FromResult(true);
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

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) =>
            Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)
                )
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static AuthService AuthSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new FakeTokenService(),
            user,
            new FakeSettings(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(new UnitOfWork(db)),
            audit
        );

    private static UserService UserSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            user,
            new NoopEmail(),
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(db)),
            audit
        );

    private static InviteService InviteSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            user,
            new FakePasswordHasher(),
            new FakeTokenService(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            new NoopEmail(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(db)),
            audit
        );

    private static WorkspaceService WorkspaceSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(new UnitOfWork(db), user, audit);

    private static ProjectService ProjectSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            audit
        );

    private static TenantService TenantSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            new FakePasswordHasher(),
            new NoopFileStorage(),
            new FakeSettings(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(db)),
            audit
        );

    private static IdentityEraseService EraseSvc(AppDbContext db, ICurrentUser user, FakeAuditWriter audit) =>
        new(
            new UnitOfWork(db),
            new MembershipService(new UnitOfWork(db)),
            user,
            new FakePasswordHasher(),
            new FakeReset(),
            new NoopEmail(),
            new NoopBrandingService(),
            audit
        );

    // ── auth.login.succeeded / auth.login.failed ───────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_Success_WritesAuthLoginSucceeded()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var anon = new FakeCurrentUser();

        int roleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            roleId = role.Id;

            var user = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "member@example.com",
                PasswordHash = "h:Passw0rd!",
                DisplayName = "Member",
                RoleId = roleId,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenant, role);
        }

        var audit = new FakeAuditWriter();
        using var ctx = Ctx(anon, db);
        var svc = AuthSvc(ctx, anon, audit);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = "member@example.com", Password = "Passw0rd!" }
        );

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthLoginSucceeded, entry.Action);
        Assert.Equal(tenant, entry.OwnerId);
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_WritesAuthLoginFailed_HashedNeverRaw()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var anon = new FakeCurrentUser();
        const string email = "member2@example.com";

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var user = new User
            {
                PublicId = Guid.NewGuid(),
                Email = email,
                PasswordHash = "h:Correct1!",
                DisplayName = "Member2",
                RoleId = role.Id,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenant, role);
        }

        var audit = new FakeAuditWriter();
        using var ctx = Ctx(anon, db);
        var svc = AuthSvc(ctx, anon, audit);

        var result = await svc.LoginAsync(new LoginRequest { Email = email, Password = "WrongPassword!" });

        Assert.False(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthLoginFailed, entry.Action);
        Assert.Equal(AuditTargets.User, entry.TargetType);

        // Never the raw e-mail anywhere in the entry (D12.3) — only the pseudonym or the identity's
        // own public id ever appear. Widened per review finding #12: also serialise Before/After
        // KEYS (a raw address could leak as a dictionary KEY, not just a value) and
        // ActorUserIdOverride (a raw address could be mistakenly passed as the override itself).
        var serialized =
            $"{entry.Action}|{entry.TargetType}|{entry.TargetId}|{entry.OwnerId}|{entry.ActorUserIdOverride}|"
            + string.Join(',', (entry.Before ?? new Dictionary<string, string>()).Keys)
            + string.Join(',', (entry.Before ?? new Dictionary<string, string>()).Values)
            + string.Join(',', (entry.After ?? new Dictionary<string, string>()).Keys)
            + string.Join(',', (entry.After ?? new Dictionary<string, string>()).Values);
        Assert.DoesNotContain(email, serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoginAsync_UnknownEmail_WritesAuthLoginFailed_AsEmailHash()
    {
        var db = Guid.NewGuid().ToString();
        var anon = new FakeCurrentUser();
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(anon, db);
        var svc = AuthSvc(ctx, anon, audit);

        var result = await svc.LoginAsync(
            new LoginRequest { Email = "nobody@example.com", Password = "whatever1!" }
        );

        Assert.False(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthLoginFailed, entry.Action);
        Assert.Equal(AuditTargets.EmailHash, entry.TargetType);
        Assert.Equal(PseudonymHasher.EmailHash("nobody@example.com"), entry.TargetId);
        Assert.DoesNotContain("nobody@example.com", entry.TargetId, StringComparison.OrdinalIgnoreCase);
    }

    // ── auth.password.changed ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePasswordAsync_Success_WritesAuthPasswordChanged()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var user = new User
            {
                PublicId = publicId,
                Email = "self@example.com",
                PasswordHash = "h:OldPass1!",
                DisplayName = "Self",
                RoleId = role.Id,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenant, role);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(caller, db);
        var svc = AuthSvc(ctx, caller, audit);

        var result = await svc.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = "OldPass1!", NewPassword = "NewPass1!" }
        );

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.AuthPasswordChanged, entry.Action);
        Assert.Equal(publicId.ToString(), entry.TargetId);
    }

    // ── member.updated / member.removed ─────────────────────────────────────────────────────

    [Fact]
    public async Task UserService_UpdateAsync_RoleChange_WritesMemberUpdated_BeforeAfterDiffer()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int oldRoleId,
            newRoleId,
            userId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var oldRole = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            var newRole = new Role { Name = "Reviewer", GrantsAdmin = false, IsActive = true };
            seed.Roles.AddRange(oldRole, newRole);
            seed.SaveChanges();
            oldRoleId = oldRole.Id;
            newRoleId = newRole.Id;

            var user = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "target@example.com",
                PasswordHash = "h:x",
                DisplayName = "Target",
                RoleId = oldRoleId,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            userId = user.Id;
            TestSeed.Join(seed, user, tenant, oldRole);
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = UserSvc(ctx, admin, audit);

        var result = await svc.UpdateAsync(userId, new UpdateUserRequest { RoleId = newRoleId });

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.MemberUpdated, entry.Action);
        Assert.NotEqual(entry.Before!["role_id"], entry.After!["role_id"]);
        Assert.Equal(oldRoleId.ToString(), entry.Before!["role_id"]);
        Assert.Equal(newRoleId.ToString(), entry.After!["role_id"]);
    }

    [Fact]
    public async Task UserService_DeleteAsync_WritesMemberRemoved()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        int memberRowId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var memberRole = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(memberRole);
            seed.SaveChanges();

            var member = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "removable@example.com",
                PasswordHash = "h:x",
                DisplayName = "Removable",
                RoleId = memberRole.Id,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.Add(member);
            seed.SaveChanges();
            memberRowId = member.Id;
            TestSeed.Join(seed, member, tenant, memberRole);
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = UserSvc(ctx, admin, audit);

        var result = await svc.DeleteAsync(memberRowId);

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.MemberRemoved, entry.Action);
    }

    // ── invite.created / invite.revoked ─────────────────────────────────────────────────────

    [Fact]
    public async Task InviteService_CreateAsync_WritesInviteCreated()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = InviteSvc(ctx, admin, audit);

        var result = await svc.CreateAsync(new CreateInviteRequest { ExpiresInDays = 7 });

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.InviteCreated, entry.Action);
        Assert.Equal(AuditTargets.Invite, entry.TargetType);
        Assert.Equal(tenant, entry.OwnerId);
    }

    [Fact]
    public async Task InviteService_RevokeAsync_WritesInviteRevoked()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };

        int inviteId;
        using (var ctx0 = Ctx(admin, db))
        {
            var createAudit = new FakeAuditWriter();
            var createSvc = InviteSvc(ctx0, admin, createAudit);
            var created = await createSvc.CreateAsync(new CreateInviteRequest { ExpiresInDays = 7 });
            inviteId = created.Data!.Id;
        }

        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = InviteSvc(ctx, admin, audit);

        var result = await svc.RevokeAsync(inviteId);

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.InviteRevoked, entry.Action);
        Assert.Equal(inviteId.ToString(), entry.TargetId);
    }

    // ── workspace.renamed ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkspaceService_RenameAsync_WritesWorkspaceRenamed_BeforeAfterName()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = tenant,
                        Name = "Old Name",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = tenant,
                    }
                );
            seed.SaveChanges();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = WorkspaceSvc(ctx, admin, audit);

        var result = await svc.RenameAsync(new UpdateWorkspaceNameRequest { Name = "New Name" });

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.WorkspaceRenamed, entry.Action);
        Assert.Equal("Old Name", entry.Before!["name"]);
        Assert.Equal("New Name", entry.After!["name"]);
    }

    // ── project.created ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectService_CreateAsync_WritesProjectCreated()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = ProjectSvc(ctx, admin, audit);

        var result = await svc.CreateAsync(new CreateProjectRequest { Key = "proj-1", Name = "Proj 1" });

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.ProjectCreated, entry.Action);
        Assert.Equal(tenant, entry.OwnerId);
        Assert.Equal("proj-1", entry.After!["key"]);
    }

    // ── tenant.status_changed ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TenantService_SetStatusAsync_WritesTenantStatusChanged()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsActive = true };
            seed.Roles.Add(adminRole);
            seed.SaveChanges();

            seed.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = workspaceId,
                        Name = "Tenant",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = workspaceId,
                    }
                );
            seed.SaveChanges();

            var adminUser = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "tenantadmin@example.com",
                PasswordHash = "h:x",
                DisplayName = "Tenant Admin",
                RoleId = adminRole.Id,
                OwnerId = workspaceId,
                ApprovalStatus = ApprovalStatus.Pending,
                IsActive = false,
            };
            seed.Users.Add(adminUser);
            seed.SaveChanges();
            TestSeed.Join(seed, adminUser, workspaceId, adminRole, isActive: false, status: ApprovalStatus.Pending);
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true, Id = Guid.NewGuid() };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(superAdmin, db);
        var svc = TenantSvc(ctx, superAdmin, audit);

        var result = await svc.SetStatusAsync(workspaceId, "approve");

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TenantStatusChanged, entry.Action);
        Assert.Equal(workspaceId, entry.OwnerId);
        Assert.Equal("approve", entry.After!["action"]);
    }

    // ── ownership.transferred (two rows) ────────────────────────────────────────────────────

    [Fact]
    public async Task UserService_TransferOwnershipAsync_WritesTwoOwnershipTransferredRows()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        Guid adminPublicId,
            deputyPublicId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var adminRole = new Role
            {
                Name = "Workspace Admin",
                GrantsAdmin = true,
                IsSystem = true,
                IsActive = true,
            };
            var deputyRole = new Role
            {
                Name = "Workspace Admin Deputy",
                GrantsAdmin = true,
                IsSystem = true,
                IsActive = true,
            };
            seed.Roles.AddRange(adminRole, deputyRole);
            seed.SaveChanges();

            adminPublicId = Guid.NewGuid();
            deputyPublicId = Guid.NewGuid();

            var admin = new User
            {
                PublicId = adminPublicId,
                Email = "admin@example.com",
                PasswordHash = "h:x",
                DisplayName = "Admin",
                RoleId = adminRole.Id,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            var deputy = new User
            {
                PublicId = deputyPublicId,
                Email = "deputy@example.com",
                PasswordHash = "h:x",
                DisplayName = "Deputy",
                RoleId = deputyRole.Id,
                OwnerId = tenant,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
            };
            seed.Users.AddRange(admin, deputy);
            seed.SaveChanges();

            TestSeed.Join(seed, admin, tenant, adminRole);
            TestSeed.Join(seed, deputy, tenant, deputyRole);
        }

        var caller = new FakeCurrentUser { Id = adminPublicId, IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(caller, db);
        var svc = UserSvc(ctx, caller, audit);

        var result = await svc.TransferOwnershipAsync(deputyPublicId);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, audit.Entries.Count);
        Assert.All(audit.Entries, e => Assert.Equal(AuditActions.OwnershipTransferred, e.Action));
    }

    // ── tenant.hard_deleted — System actor from the hosted cleanup path ─────────────────────

    [Fact]
    public async Task TenantService_HardDeleteAsync_FromHostedPath_WritesTenantHardDeleted_ReasonDemoExpired()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = workspaceId,
                        Name = "Demo Workspace",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = workspaceId,
                    }
                );
            seed.SaveChanges();
        }

        // No authenticated caller — mirrors DemoCleanupService's background-scope invocation
        // (no HttpContext, ICurrentUser.Id is null).
        var hostedCaller = new FakeCurrentUser();
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(hostedCaller, db);
        var svc = TenantSvc(ctx, hostedCaller, audit);

        var result = await svc.HardDeleteAsync(workspaceId, reason: "demo_expired");

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TenantHardDeleted, entry.Action);
        Assert.Null(entry.OwnerId); // deliberately null — the row must outlive the workspace it is about
        Assert.Equal(workspaceId.ToString(), entry.TargetId);
        Assert.Equal("demo_expired", entry.After!["reason"]);
        // Review finding #11: explicit, not left to the (also-correct) fallback of ICurrentUser.Id
        // being null in the hosted job's scope.
        Assert.Equal(AuditActorKind.System, entry.ActorKindOverride);
    }

    [Fact]
    public async Task TenantService_HardDeleteAsync_DefaultReason_IsAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var workspaceId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = workspaceId,
                        Name = "Tenant",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = workspaceId,
                    }
                );
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true, Id = Guid.NewGuid() };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(superAdmin, db);
        var svc = TenantSvc(ctx, superAdmin, audit);

        var result = await svc.HardDeleteAsync(workspaceId);

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("admin", entry.After!["reason"]);
    }

    // ── status.reset — always written, even when nothing was ever overridden ────────────────

    [Fact]
    public async Task StatusAdminService_ResetAsync_NeverOverridden_WritesExactlyOneRow()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(admin, db);
        var svc = new StatusAdminService(new UnitOfWork(ctx), admin, audit);

        // No StatusPresentation row exists for this (value, owner) at all — review finding #1.
        var result = await svc.ResetAsync((int)CommentStatus.Open);

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.StatusReset, entry.Action);
        Assert.Equal(AuditTargets.Status, entry.TargetType);
        Assert.Equal(((int)CommentStatus.Open).ToString(), entry.TargetId);
        Assert.Equal(string.Empty, entry.After!["label"]);
    }

    // ── tenant_invite.created — no duplicate invite.created row from the delegated path ─────

    [Fact]
    public async Task TenantInviteService_CreateAsync_WritesExactlyOneRow_ActionTenantInviteCreated()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(superAdmin, db);
        var invites = InviteSvc(ctx, superAdmin, audit);
        var tenantInvites = new TenantInviteService(new UnitOfWork(ctx), invites, superAdmin, audit);

        var result = await tenantInvites.CreateAsync(new CreateTenantInviteRequest { Email = "owner@new.test" });

        Assert.True(result.IsSuccess, result.Message);
        // Exactly one row total: InviteService.CreateAsync must have been called with
        // writeAudit: false (review finding #3) so only TenantInviteService's own write lands.
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.TenantInviteCreated, entry.Action);
        Assert.Equal(AuditTargets.TenantInvite, entry.TargetType);
    }

    // ── member.left / identity.erase_requested / identity.erased (DB-11c review finding #12) ─

    [Fact]
    public async Task UserService_LeaveWorkspaceAsync_WritesMemberLeft()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            var memberRole = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.AddRange(adminRole, memberRole);
            seed.SaveChanges();

            var admin = new User { PublicId = Guid.NewGuid(), Email = "admin@example.com", PasswordHash = "h:x", DisplayName = "Admin", RoleId = adminRole.Id, OwnerId = tenant, ApprovalStatus = ApprovalStatus.Approved, IsActive = true };
            var member = new User { PublicId = publicId, Email = "leaver@example.com", PasswordHash = "h:x", DisplayName = "Leaver", RoleId = memberRole.Id, OwnerId = tenant, ApprovalStatus = ApprovalStatus.Approved, IsActive = true };
            seed.Users.AddRange(admin, member);
            seed.SaveChanges();
            TestSeed.Join(seed, admin, tenant, adminRole);
            TestSeed.Join(seed, member, tenant, memberRole);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(caller, db);
        var svc = UserSvc(ctx, caller, audit);

        var result = await svc.LeaveWorkspaceAsync();

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.MemberLeft, entry.Action);
        Assert.Equal(AuditTargets.Membership, entry.TargetType);
        Assert.Equal(tenant, entry.OwnerId);
        Assert.Equal(publicId, entry.ActorUserIdOverride);
        Assert.Equal(AuditActorKind.User, entry.ActorKindOverride);
    }

    [Fact]
    public async Task IdentityEraseService_RequestEraseLinkAsync_WritesIdentityEraseRequested()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();

            var user = new User { PublicId = publicId, Email = "linkonly@example.com", PasswordHash = "h:x", DisplayName = "Link Only", RoleId = role.Id, OwnerId = tenant, ApprovalStatus = ApprovalStatus.Approved, IsActive = true, PasswordlessOnly = true };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenant, role);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(caller, db);
        var svc = EraseSvc(ctx, caller, audit);

        var result = await svc.RequestEraseLinkAsync();

        Assert.True(result.IsSuccess, result.Message);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal(AuditActions.IdentityEraseRequested, entry.Action);
        Assert.Equal(AuditTargets.User, entry.TargetType);
        Assert.Equal(publicId.ToString(), entry.TargetId);
        Assert.Equal(publicId, entry.ActorUserIdOverride);
        // Review finding #8: null here (authenticated self path) — AuditWriter's normal actor-kind
        // resolution runs instead of a hard-coded User override.
        Assert.Null(entry.ActorKindOverride);
    }

    [Fact]
    public async Task IdentityEraseService_EraseSelfAsync_WritesIdentityErased_PerMembershipAndOperatorRow()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var publicId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var roleA = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            var roleB = new Role { Name = "Engineer", GrantsAdmin = false, IsActive = true };
            seed.Roles.AddRange(roleA, roleB);
            seed.SaveChanges();

            var user = new User { PublicId = publicId, Email = "multi@example.com", PasswordHash = "h:Passw0rd!", DisplayName = "Multi", RoleId = roleA.Id, OwnerId = tenantA, ApprovalStatus = ApprovalStatus.Approved, IsActive = true };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenantA, roleA);
            TestSeed.Join(seed, user, tenantB, roleB);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = tenantA };
        var audit = new FakeAuditWriter();
        using var ctx = Ctx(caller, db);
        var svc = EraseSvc(ctx, caller, audit);

        var result = await svc.EraseSelfAsync(new DeleteMyAccountRequest { Password = "Passw0rd!" });

        Assert.True(result.IsSuccess, result.Message);

        // Review finding #9: one identity.erased row PER ended membership (workspace-scoped —
        // visible to a workspace-scoped audit query) plus the operator-level row (OwnerId=null).
        var erasedEntries = audit.Entries.Where(e => e.Action == AuditActions.IdentityErased).ToList();
        Assert.Equal(3, erasedEntries.Count);
        Assert.Single(erasedEntries, e => e.TargetType == AuditTargets.Membership && e.OwnerId == tenantA);
        Assert.Single(erasedEntries, e => e.TargetType == AuditTargets.Membership && e.OwnerId == tenantB);
        Assert.Single(erasedEntries, e => e.TargetType == AuditTargets.User && e.OwnerId == null && e.TargetId == publicId.ToString());

        // Review finding #8: self-erase resolves as User through normal AuditWriter inference — the
        // service must not force-override it.
        Assert.All(erasedEntries, e => Assert.Null(e.ActorKindOverride));
        Assert.All(erasedEntries, e => Assert.Equal(publicId, e.ActorUserIdOverride));
    }
}
