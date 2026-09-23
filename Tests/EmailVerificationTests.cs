using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
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
/// DB-14 §6 tests 1-4, 9 — the verified-at-creation rule per site (§3.2), the confirm/resend link
/// (§3.3), and <c>MeResponse.EmailVerified/EmailVerificationRequired</c> (§3.5). Fixture mirrors
/// <see cref="ChangeEmailTests"/> / <see cref="InviteServiceTests"/> (InMemory + a real
/// <see cref="ResetTokenService"/> + a real <see cref="EmailVerificationService"/> wired to a
/// <c>CapturingEmail</c> double so the token itself can be inspected).
/// </summary>
public class EmailVerificationTests
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

    private sealed class IdentityHasher : IPasswordHasher
    {
        public string Hash(string password) => "h:" + password;
        public bool Verify(string password, string hash) => hash == "h:" + password;
    }

    /// <summary>Signup enabled; every other flag/string/int at its fallback.</summary>
    private sealed class SettingsEnabled : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(key == ISettingsService.ScopedAdminSignupEnabled || fallback);
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
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(Pointer.Application.DTOs.Branding.BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Pointer.Application.Response.Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(DefaultBranding()));
        public Task<int> BumpVersionAsync() => Task.FromResult(0);
        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(DefaultBranding());
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) =>
            Task.FromResult("");
        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");
        public string IssueSelection(User user) => "sel-for-" + user.PublicId.ToString("N");
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

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            u,
            new ConfigurationBuilder().Build()
        );

    private static IResetTokenService RealResetTokens() =>
        new ResetTokenService(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789" })
                .Build()
        );

    private static EmailVerificationService BuildEmailVerification(
        ICurrentUser user,
        AppDbContext ctx,
        IResetTokenService resetTokens,
        CapturingEmail email,
        IMemoryCache cache
    ) =>
        new(
            new UnitOfWork(ctx),
            user,
            new MembershipService(new UnitOfWork(ctx)),
            resetTokens,
            email,
            new NoopBrandingService(),
            cache,
            NullLogger<EmailVerificationService>.Instance
        );

    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail email,
        IResetTokenService resetTokens,
        IEmailVerificationService emailVerification
    ) =>
        new(
            new UnitOfWork(ctx),
            new IdentityHasher(),
            new FakeTokenService(),
            user,
            new SettingsEnabled(),
            resetTokens,
            email,
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(new UnitOfWork(ctx)),
            audit: null,
            emailVerification: emailVerification
        );

    private static InviteService BuildInviteService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail email,
        IEmailVerificationService emailVerification
    ) =>
        new(
            new UnitOfWork(ctx),
            user,
            new IdentityHasher(),
            new FakeTokenService(),
            new SettingsEnabled(),
            new PassThroughEntitlements(),
            email,
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx)),
            audit: null,
            emailVerification: emailVerification
        );

    private static UserService BuildUserService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail email,
        IEmailVerificationService emailVerification
    ) =>
        new(
            new UnitOfWork(ctx),
            new IdentityHasher(),
            user,
            email,
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx)),
            audit: null,
            emailVerification: emailVerification
        );

    private static TenantService BuildTenantService(ICurrentUser user, AppDbContext ctx) =>
        new(
            new UnitOfWork(ctx),
            new IdentityHasher(),
            new NoopFileStorage(),
            new SettingsEnabled(),
            new NoopBillingProvider(),
            new MembershipService(new UnitOfWork(ctx))
        );

    private static DemoService BuildDemoService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail email,
        IEmailVerificationService emailVerification
    ) =>
        new(
            new UnitOfWork(ctx),
            new IdentityHasher(),
            new FakeTokenService(),
            email,
            new SettingsEnabled(),
            new NoopBrandingService(),
            new MembershipService(new UnitOfWork(ctx)),
            audit: null,
            emailVerification: emailVerification
        );

    private const string StrongPw = "correct-horse-battery-1";

    // ── 1. RegisterAdminAsync ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAdmin_CreatesUnverified_SendsLink()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Roles.Add(new Role { Name = "Workspace Admin", GrantsAdmin = true, IsActive = true, OwnerId = null });
            seed.SaveChanges();
        }

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var email = new CapturingEmail();
        var resetTokens = RealResetTokens();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var emailVerification = BuildEmailVerification(anon, ctx, resetTokens, email, cache);
        var auth = BuildAuthService(anon, ctx, email, resetTokens, emailVerification);

        var result = await auth.RegisterAdminAsync(
            new RegisterAdminRequest { Email = "New@Admin.com", Password = StrongPw, DisplayName = "New Admin" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "new@admin.com");
        Assert.Null(identity.EmailVerifiedAt);

        var sent = Assert.Single(email.Sent);
        Assert.Contains("verify-email?token=", sent.Html);

        var token = CapturingEmail.ExtractToken(sent.Html);
        Assert.True(resetTokens.TryValidateScoped(token, TokenPurposes.VerifyEmail, out var pid, out _, out var payload));
        Assert.Equal(identity.PublicId, pid);
        Assert.Equal("new@admin.com", payload);
        Assert.False(resetTokens.TryValidateScoped(token, TokenPurposes.ChangeEmail, out _, out _, out _));
        Assert.False(resetTokens.TryValidate(token, out _, out _));
    }

    // ── 2. Invite accept — addressed vs. open, new vs. existing identity ────────────────────

    private static (Guid OwnerId, int MemberRoleId) SeedTenant(string db)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
        seed.Roles.Add(role);
        var adminRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
        seed.Roles.Add(adminRole);
        seed.SaveChanges();

        var owner = Guid.NewGuid();
        var admin = new User
        {
            Email = $"admin-{owner:N}@t.com",
            PasswordHash = "h:pw",
            DisplayName = "Admin",
            PublicId = Guid.NewGuid(),
            OwnerId = owner,
            RoleId = adminRole.Id,
            IsActive = true,
            EmailVerifiedAt = DateTime.UtcNow,
        };
        seed.Users.Add(admin);
        seed.SaveChanges();
        TestSeed.Join(seed, admin, owner, adminRole);

        return (owner, role.Id);
    }

    private static int SeedInvite(string db, Guid owner, int roleId, string? email)
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var invite = new Invite
        {
            OwnerId = owner,
            Code = "code-" + Guid.NewGuid().ToString("N"),
            RoleId = roleId,
            // Real invite creation (InviteService.CreateAsync/CreateInternalAsync) always normalizes
            // before storing (EmailNormalizer.Normalize(request.Email)) — mirror that here so the
            // AcceptAsync email-lock comparison (`invite.Email != emailNormalized`, itself unchanged
            // by DB-14) behaves as it does in production rather than tripping on a raw-cased seed.
            Email = EmailNormalizer.Normalize(email),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            Uses = 0,
        };
        seed.Invites.Add(invite);
        seed.SaveChanges();
        return invite.Id;
    }

    private static string CodeOf(string db, int inviteId)
    {
        using var s = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        return s.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId).Code;
    }

    [Fact]
    public async Task AcceptInvite_Addressed_VerifiedNoMail()
    {
        var db = Guid.NewGuid().ToString();
        var (owner, roleId) = SeedTenant(db);
        var inviteId = SeedInvite(db, owner, roleId, email: "A@x.com");
        var code = CodeOf(db, inviteId);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(anon, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var invites = BuildInviteService(anon, ctx, capturing, emailVerification);

        var result = await invites.AcceptAsync(
            new AcceptInviteRequest { Code = code, Email = "a@x.com", Password = StrongPw, DisplayName = "Joiner" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "a@x.com");
        Assert.NotNull(identity.EmailVerifiedAt);
        Assert.Empty(capturing.Sent);
    }

    [Fact]
    public async Task AcceptInvite_Addressed_ExistingUnverifiedIdentity_BecomesVerified()
    {
        var db = Guid.NewGuid().ToString();
        var (ownerA, roleIdA) = SeedTenant(db);
        var (ownerB, roleIdB) = SeedTenant(db);

        // An existing unverified identity, already a member of workspace B.
        var existingPublicId = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var memberRoleB = seed.Roles.IgnoreQueryFilters().Single(r => r.Id == roleIdB);
            var existing = new User
            {
                PublicId = existingPublicId,
                Email = "a@x.com",
                PasswordHash = "h:" + StrongPw,
                DisplayName = "Existing",
                RoleId = roleIdB,
                OwnerId = ownerB,
                IsActive = true,
                EmailVerifiedAt = null,
            };
            seed.Users.Add(existing);
            seed.SaveChanges();
            TestSeed.Join(seed, existing, ownerB, memberRoleB);
        }

        var inviteId = SeedInvite(db, ownerA, roleIdA, email: "A@x.com");
        var code = CodeOf(db, inviteId);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(anon, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var invites = BuildInviteService(anon, ctx, capturing, emailVerification);

        var result = await invites.AcceptAsync(
            new AcceptInviteRequest { Code = code, Email = "a@x.com", Password = StrongPw, DisplayName = "Existing" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == existingPublicId);
        Assert.NotNull(identity.EmailVerifiedAt);
        Assert.Empty(capturing.Sent);
    }

    [Fact]
    public async Task AcceptInvite_Open_ExistingUnverifiedIdentity_StaysUnverified()
    {
        var db = Guid.NewGuid().ToString();
        var (ownerA, roleIdA) = SeedTenant(db);
        var (ownerB, roleIdB) = SeedTenant(db);

        var existingPublicId = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var memberRoleB = seed.Roles.IgnoreQueryFilters().Single(r => r.Id == roleIdB);
            var existing = new User
            {
                PublicId = existingPublicId,
                Email = "open@x.com",
                PasswordHash = "h:" + StrongPw,
                DisplayName = "Existing",
                RoleId = roleIdB,
                OwnerId = ownerB,
                IsActive = true,
                EmailVerifiedAt = null,
            };
            seed.Users.Add(existing);
            seed.SaveChanges();
            TestSeed.Join(seed, existing, ownerB, memberRoleB);
        }

        var inviteId = SeedInvite(db, ownerA, roleIdA, email: null); // open — anyone with the link
        var code = CodeOf(db, inviteId);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(anon, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var invites = BuildInviteService(anon, ctx, capturing, emailVerification);

        var result = await invites.AcceptAsync(
            new AcceptInviteRequest { Code = code, Email = "open@x.com", Password = StrongPw, DisplayName = "Existing" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == existingPublicId);
        Assert.Null(identity.EmailVerifiedAt);
        Assert.Empty(capturing.Sent);
    }

    [Fact]
    public async Task AcceptInvite_Open_UnverifiedWithMail()
    {
        var db = Guid.NewGuid().ToString();
        var (owner, roleId) = SeedTenant(db);
        var inviteId = SeedInvite(db, owner, roleId, email: null);
        var code = CodeOf(db, inviteId);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(anon, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var invites = BuildInviteService(anon, ctx, capturing, emailVerification);

        var result = await invites.AcceptAsync(
            new AcceptInviteRequest { Code = code, Email = "brandnew@x.com", Password = StrongPw, DisplayName = "New" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "brandnew@x.com");
        Assert.Null(identity.EmailVerifiedAt);
        Assert.Single(capturing.Sent);
    }

    // ── 3. Quick access ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task QuickAccess_VerifiedAtCreation()
    {
        var db = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid();
        int projectId;
        int quickAccessRoleId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Client", GrantsAdmin = false, IsSystem = false, IsActive = true, QuickAccess = true, OwnerId = null };
            seed.Roles.Add(role);
            seed.Set<Workspace>().Add(new Workspace { Id = owner, Name = "T", CreatedAt = DateTime.UtcNow, CreatedBy = owner });
            var project = new Project { Key = "proj", Name = "Proj", OwnerId = owner, AppUrl = "https://app.example.com" };
            seed.Projects.Add(project);
            seed.SaveChanges();
            projectId = project.Id;
            quickAccessRoleId = role.Id;
        }

        var admin = new FakeCurrentUser { TenantId = owner };
        using var ctx = Ctx(admin, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(admin, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var invites = BuildInviteService(admin, ctx, capturing, emailVerification);

        var result = await invites.CreateAsync(
            new CreateInviteRequest { Email = "client@x.com", ProjectId = projectId, RoleId = quickAccessRoleId }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "client@x.com");
        Assert.NotNull(identity.EmailVerifiedAt);
    }

    // ── 4. Super admin creates a workspace ───────────────────────────────────────────────────

    [Fact]
    public async Task TenantCreate_BySuperAdmin_Verified()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Roles.Add(new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true, OwnerId = null });
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = Ctx(superAdmin, db);
        var svc = BuildTenantService(superAdmin, ctx);

        var result = await svc.CreateAsync(
            new CreateTenantRequest { Email = "operator-made@x.com", Password = StrongPw, DisplayName = "New" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "operator-made@x.com");
        Assert.NotNull(identity.EmailVerifiedAt);
    }

    // ── 5. Admin adds a member ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UserCreate_ByAdmin_UnverifiedWithMail()
    {
        var db = Guid.NewGuid().ToString();
        var (owner, roleId) = SeedTenant(db);

        var admin = new FakeCurrentUser { TenantId = owner };
        using var ctx = Ctx(admin, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(admin, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var users = BuildUserService(admin, ctx, capturing, emailVerification);

        var result = await users.CreateAsync(
            new CreateUserRequest { Email = "member@x.com", Password = StrongPw, DisplayName = "Member", RoleId = roleId }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == "member@x.com");
        Assert.Null(identity.EmailVerifiedAt);
        Assert.Single(capturing.Sent);
    }

    // ── 6. Demo upgrade ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DemoUpgrade_ResetsVerification_SendsMail()
    {
        var db = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid();
        var publicId = Guid.NewGuid();
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            // Simulates a demo row the DB-14 backfill happened to touch (created before the cutoff) —
            // the upgrade step must still null it out (D14.1 never grandfathers a demo address that
            // is about to be replaced by a real one).
            var demo = new User
            {
                PublicId = publicId,
                Email = "demo-abc@demo.pointer",
                PasswordHash = "h:demo",
                DisplayName = "Demo User",
                RoleId = role.Id,
                OwnerId = owner,
                IsActive = true,
                IsDemo = true,
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                EmailVerifiedAt = DateTime.UtcNow.AddDays(-1),
            };
            seed.Users.Add(demo);
            seed.SaveChanges();
            TestSeed.Join(seed, demo, owner, role);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = owner };
        using var ctx = Ctx(caller, db);
        var capturing = new CapturingEmail();
        var emailVerification = BuildEmailVerification(caller, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));
        var demoSvc = BuildDemoService(caller, ctx, capturing, emailVerification);

        var result = await demoSvc.UpgradeAsync(
            publicId,
            new UpgradeDemoRequest { Email = "real@x.com", Password = StrongPw, DisplayName = "Real Person" }
        );
        Assert.True(result.IsSuccess, result.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        Assert.Null(identity.EmailVerifiedAt);
        Assert.Single(capturing.Sent);
    }

    // ── 7. Join-or-create onto an existing identity leaves verification alone (non-invite path) ─

    [Fact]
    public async Task JoinExistingIdentity_DoesNotTouchVerification()
    {
        var db = Guid.NewGuid().ToString();
        var existingEmail = "founder@x.com";
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Roles.Add(new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true, OwnerId = null });
            seed.SaveChanges();
        }
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var ctx0 = Ctx(superAdmin, db))
        {
            var svc0 = BuildTenantService(superAdmin, ctx0);
            var first = await svc0.CreateAsync(
                new CreateTenantRequest { Email = existingEmail, Password = StrongPw, DisplayName = "Founder" }
            );
            Assert.True(first.IsSuccess, first.Message);
        }

        // Manually unverify the existing identity to prove a SECOND TenantService.CreateAsync join
        // (D13: one identity may administer several workspaces) never touches it.
        using (var mutate = Ctx(superAdmin, db))
        {
            var row = mutate.Users.IgnoreQueryFilters().Single(u => u.Email == existingEmail);
            row.EmailVerifiedAt = null;
            mutate.SaveChanges();
        }

        using var ctx = Ctx(superAdmin, db);
        var svc = BuildTenantService(superAdmin, ctx);
        var second = await svc.CreateAsync(
            new CreateTenantRequest { Email = existingEmail, Password = StrongPw, DisplayName = "Founder" }
        );
        Assert.True(second.IsSuccess, second.Message);

        var identity = ctx.Users.IgnoreQueryFilters().Single(u => u.Email == existingEmail);
        Assert.Null(identity.EmailVerifiedAt); // untouched — D14.3 only applies to a NEW identity
    }

    // ── Confirm ──────────────────────────────────────────────────────────────────────────────

    private static Guid SeedUnverified(AppDbContext seed, out Guid stamp, bool deleted = false)
    {
        var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
        seed.Roles.Add(role);
        seed.SaveChanges();
        var publicId = Guid.NewGuid();
        var user = new User
        {
            PublicId = publicId,
            Email = "confirm@t.com",
            PasswordHash = "h:pw",
            DisplayName = "U",
            RoleId = role.Id,
            IsActive = true,
            SecurityStamp = Guid.NewGuid(),
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        seed.Users.Add(user);
        seed.SaveChanges();
        stamp = user.SecurityStamp;
        return publicId;
    }

    [Fact]
    public async Task Confirm_ValidToken_SetsVerifiedAt_Idempotent()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp);

        var resetTokens = RealResetTokens();
        var token = resetTokens.CreateScoped(publicId, stamp, TokenPurposes.VerifyEmail, "confirm@t.com");

        using var ctx = Ctx(superAdmin, db);
        var capturing = new CapturingEmail();
        var svc = BuildEmailVerification(superAdmin, ctx, resetTokens, capturing, new MemoryCache(new MemoryCacheOptions()));

        var first = await svc.ConfirmAsync(token);
        Assert.True(first.IsSuccess, first.Message);
        var verifiedAt = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId).EmailVerifiedAt;
        Assert.NotNull(verifiedAt);

        var second = await svc.ConfirmAsync(token); // idempotent re-click
        Assert.True(second.IsSuccess, second.Message);
        var stillVerifiedAt = ctx.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId).EmailVerifiedAt;
        Assert.Equal(verifiedAt, stillVerifiedAt);
    }

    [Fact]
    public async Task Confirm_PayloadMismatch_AfterEmailChange_Invalid()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp);

        var resetTokens = RealResetTokens();
        // Minted for the OLD address; the identity's e-mail has since changed.
        var token = resetTokens.CreateScoped(publicId, stamp, TokenPurposes.VerifyEmail, "old@t.com");

        using var ctx = Ctx(superAdmin, db);
        var svc = BuildEmailVerification(superAdmin, ctx, resetTokens, new CapturingEmail(), new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ConfirmAsync(token);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_WrongPurpose_Invalid()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp);

        var resetTokens = RealResetTokens();
        var eraseToken = resetTokens.CreateScoped(publicId, stamp, TokenPurposes.Erase, "confirm@t.com");

        using var ctx = Ctx(superAdmin, db);
        var svc = BuildEmailVerification(superAdmin, ctx, resetTokens, new CapturingEmail(), new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ConfirmAsync(eraseToken);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_StampRotated_Invalid()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp);

        var resetTokens = RealResetTokens();
        var token = resetTokens.CreateScoped(publicId, stamp, TokenPurposes.VerifyEmail, "confirm@t.com");

        // Rotate the stamp after minting (e.g. a password change) — the link should die with it.
        using (var mutate = Ctx(superAdmin, db))
        {
            var row = mutate.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
            row.SecurityStamp = Guid.NewGuid();
            mutate.SaveChanges();
        }

        using var ctx = Ctx(superAdmin, db);
        var svc = BuildEmailVerification(superAdmin, ctx, resetTokens, new CapturingEmail(), new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ConfirmAsync(token);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_Deleted_Invalid()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp, deleted: true);

        var resetTokens = RealResetTokens();
        var token = resetTokens.CreateScoped(publicId, stamp, TokenPurposes.VerifyEmail, "confirm@t.com");

        using var ctx = Ctx(superAdmin, db);
        var svc = BuildEmailVerification(superAdmin, ctx, resetTokens, new CapturingEmail(), new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ConfirmAsync(token);
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationLinkInvalid, result.Message);
    }

    // ── Resend ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resend_Throttled_5Minutes()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId, stamp;
        using (var seed = Ctx(superAdmin, db))
            publicId = SeedUnverified(seed, out stamp);

        var caller = new FakeCurrentUser { Id = publicId };
        using var ctx = Ctx(caller, db);
        var capturing = new CapturingEmail();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = BuildEmailVerification(caller, ctx, RealResetTokens(), capturing, cache);

        var first = await svc.ResendAsync();
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal(MessageKeys.Auth.VerificationSent, first.Message);

        var second = await svc.ResendAsync();
        Assert.False(second.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationRecentlySent, second.Message);

        Assert.Single(capturing.Sent);
    }

    [Fact]
    public async Task Resend_AlreadyVerified()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            publicId = SeedUnverified(seed, out _);
            var row = seed.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
            row.EmailVerifiedAt = DateTime.UtcNow;
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = publicId };
        using var ctx = Ctx(caller, db);
        var capturing = new CapturingEmail();
        var svc = BuildEmailVerification(caller, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ResendAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.AlreadyVerified, result.Message);
        Assert.Empty(capturing.Sent);
    }

    [Fact]
    public async Task Resend_Demo_NotApplicable()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid publicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true, IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            publicId = Guid.NewGuid();
            seed.Users.Add(new User
            {
                PublicId = publicId,
                Email = "demo-x@demo.pointer",
                PasswordHash = "h:demo",
                DisplayName = "Demo",
                RoleId = role.Id,
                IsActive = true,
                IsDemo = true,
            });
            seed.SaveChanges();
        }

        var caller = new FakeCurrentUser { Id = publicId };
        using var ctx = Ctx(caller, db);
        var capturing = new CapturingEmail();
        var svc = BuildEmailVerification(caller, ctx, RealResetTokens(), capturing, new MemoryCache(new MemoryCacheOptions()));

        var result = await svc.ResendAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Auth.VerificationNotApplicable, result.Message);
        Assert.Empty(capturing.Sent);
    }

    // ── 9. Me flags (§3.5) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Me_EmailVerifiedFlags()
    {
        var adminRole = new Role { Id = 1, Name = "Workspace Admin", GrantsAdmin = true };
        var stakeholderRole = new Role { Id = 2, Name = "Developer", GrantsAdmin = false };

        var unverifiedAdmin = new User { PublicId = Guid.NewGuid(), Email = "a@x.com", RoleId = adminRole.Id };
        var unverifiedAdminMe = UserMapper.ToMeResponse(unverifiedAdmin, adminRole);
        Assert.False(unverifiedAdminMe.EmailVerified);
        Assert.True(unverifiedAdminMe.EmailVerificationRequired);

        var unverifiedStakeholder = new User { PublicId = Guid.NewGuid(), Email = "b@x.com", RoleId = stakeholderRole.Id };
        var unverifiedStakeholderMe = UserMapper.ToMeResponse(unverifiedStakeholder, stakeholderRole);
        Assert.False(unverifiedStakeholderMe.EmailVerified);
        Assert.False(unverifiedStakeholderMe.EmailVerificationRequired);

        var verifiedAdmin = new User { PublicId = Guid.NewGuid(), Email = "c@x.com", RoleId = adminRole.Id, EmailVerifiedAt = DateTime.UtcNow };
        var verifiedAdminMe = UserMapper.ToMeResponse(verifiedAdmin, adminRole);
        Assert.True(verifiedAdminMe.EmailVerified);
        Assert.False(verifiedAdminMe.EmailVerificationRequired);

        var demoAdmin = new User { PublicId = Guid.NewGuid(), Email = "d@x.com", RoleId = adminRole.Id, IsDemo = true };
        var demoAdminMe = UserMapper.ToMeResponse(demoAdmin, adminRole);
        Assert.True(demoAdminMe.EmailVerified);
    }
}
