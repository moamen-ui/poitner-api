using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Validators;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11d — POST /api/me/change-email (password-confirmed request) and
/// POST /api/auth/confirm-email-change (scoped token redemption). Fixture mirrors
/// <see cref="DeletionSemanticsTests"/> (InMemory + <c>TestSeed.Join</c> + <c>CapturingEmail</c> +
/// a real <c>ResetTokenService</c>).
/// </summary>
public class ChangeEmailTests
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
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
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
                    App = "https://app.pointer.test",
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

    /// <summary>Records every send; extracts the token= query param from the first link in the body.</summary>
    private sealed class CapturingEmail : IEmailService
    {
        public List<(string To, string Subject, string Html)> Sent { get; } = new();

        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        )
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

        public string IssueImpersonation(
            User operatorUser,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "imp-token-for-" + operatorUser.PublicId.ToString("N");
    }

    /// <summary>
    /// Review finding #2: the EF Core InMemory provider never throws a 23505 duplicate-key
    /// violation on its own (no unique constraints are enforced), so this decorator wraps a real
    /// <see cref="UnitOfWork"/> and makes <see cref="SaveChangesAsync"/> throw the same
    /// <see cref="DbUpdateException"/>/<see cref="PostgresException"/> shape Npgsql produces for
    /// <c>ux_users_email_live</c> — everything else forwards to the inner instance unchanged.
    /// </summary>
    private sealed class ThrowDuplicateKeyUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public IRepository<T> Repository<T>()
            where T : BaseEntity => inner.Repository<T>();

        public DbSet<UsageEvent> UsageEvents => inner.UsageEvents;
        public DbSet<UsageDaily> UsageDaily => inner.UsageDaily;
        public DbSet<Workspace> Workspaces => inner.Workspaces;
        public DbSet<UserAlias> UserAliases => inner.UserAliases;
        public DbSet<AuditEvent> AuditEvents => inner.AuditEvents;
        public DbSet<ImpersonationSession> ImpersonationSessions => inner.ImpersonationSessions;
        public DbSet<BillingPayment> BillingPayments => inner.BillingPayments;
        public DbSet<DiscountRedemption> DiscountRedemptions => inner.DiscountRedemptions;

        public Task<int> SaveChangesAsync() =>
            throw new DbUpdateException(
                "simulated 23505",
                new PostgresException(
                    "duplicate key value violates unique constraint \"ux_users_email_live\"",
                    "ERROR",
                    "ERROR",
                    "23505",
                    constraintName: "ux_users_email_live"
                )
            );

        public Task ExecuteInTransactionAsync(Func<Task> action) =>
            inner.ExecuteInTransactionAsync(action);

        public void PreserveCreatedAtOnInsert(BaseEntity entity) =>
            inner.PreserveCreatedAtOnInsert(entity);

        public void ClearChangeTracker() => inner.ClearChangeTracker();

        public Task<int> AtomicClaimInviteSlotAsync(int inviteId, DateTime now) =>
            inner.AtomicClaimInviteSlotAsync(inviteId, now);

        public Task ExecuteSqlRawAsync(string sql, params object[] parameters) =>
            inner.ExecuteSqlRawAsync(sql, parameters);
    }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .InMemoryEventId
                            .TransactionIgnoredWarning
                    )
                )
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static IResetTokenService RealResetTokens() =>
        new ResetTokenService(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );

    private static AuthService BuildAuthService(
        ICurrentUser user,
        AppDbContext ctx,
        CapturingEmail? email = null,
        IAuditWriter? audit = null
    )
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
            new MembershipService(uow),
            audit
        );
    }

    // ── Seeding ──────────────────────────────────────────────────────────────────────────────

    private sealed class SeededWorkspace
    {
        public Guid OwnerId;
        public int MemberRoleId;
        public User Member = null!;
        public User Other = null!;
    }

    /// <summary>One tenant: Member (password account) and Other (a second identity, so D14 has a
    /// live address to collide with).</summary>
    private static SeededWorkspace SeedWorkspace(AppDbContext seed)
    {
        var memberRole = new Role
        {
            Name = "Engineer",
            GrantsAdmin = false,
            IsSystem = false,
            IsActive = true,
        };
        seed.Roles.Add(memberRole);
        seed.SaveChanges();

        var ownerId = Guid.NewGuid();
        var member = new User
        {
            Email = "member@t.com",
            PasswordHash = "h:pw-member",
            DisplayName = "Member",
            PublicId = Guid.NewGuid(),
            RoleId = memberRole.Id,
            IsActive = true,
        };
        var other = new User
        {
            Email = "other@t.com",
            PasswordHash = "h:pw-other",
            DisplayName = "Other",
            PublicId = Guid.NewGuid(),
            RoleId = memberRole.Id,
            IsActive = true,
        };
        seed.Users.AddRange(member, other);
        seed.SaveChanges();

        TestSeed.Join(seed, member, ownerId, memberRole);
        TestSeed.Join(seed, other, ownerId, memberRole);

        return new SeededWorkspace
        {
            OwnerId = ownerId,
            MemberRoleId = memberRole.Id,
            Member = member,
            Other = other,
        };
    }

    // ── 1. Happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestChange_HappyPath_SendsTwoEmails_WritesNothing()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var email = new CapturingEmail();
        var audit = new FakeAuditWriter();
        Guid beforeStamp;
        using (var ctx = Ctx(caller, db))
        {
            beforeStamp = ctx
                .Users.IgnoreQueryFilters()
                .Single(u => u.Id == ws.Member.Id)
                .SecurityStamp;
            var result = await BuildAuthService(caller, ctx, email, audit)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(MessageKeys.User.EmailChangeLinkSent, result.Message);
        }

        Assert.Equal(2, email.Sent.Count);
        var toNew = email.Sent.Single(s => s.To == "new@t.com");
        Assert.Contains("confirm-email?token=", toNew.Html);
        var toOld = email.Sent.Single(s => s.To == "member@t.com");
        Assert.DoesNotContain("token=", toOld.Html);

        using var check = Ctx(superAdmin, db);
        var row = check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
        Assert.Equal("member@t.com", row.Email);
        Assert.Equal(beforeStamp, row.SecurityStamp);

        var token = CapturingEmail.ExtractToken(toNew.Html);
        var resetTokens = RealResetTokens();
        Assert.True(
            resetTokens.TryValidateScoped(
                token,
                TokenPurposes.ChangeEmail,
                out var pid,
                out var stamp,
                out var payload
            )
        );
        Assert.Equal(ws.Member.PublicId, pid);
        Assert.Equal("new@t.com", payload);
        Assert.False(
            resetTokens.TryValidateScoped(token, TokenPurposes.Erase, out _, out _, out _)
        );
        Assert.False(resetTokens.TryValidate(token, out _, out _));

        Assert.Single(audit.Entries, e => e.Action == AuditActions.AuthEmailChangeRequested);
        var entry = audit.Entries.Single(e => e.Action == AuditActions.AuthEmailChangeRequested);
        Assert.Equal(PseudonymHasher.EmailHash("member@t.com"), entry.Before?["email_hash"]);
        Assert.Equal(PseudonymHasher.EmailHash("new@t.com"), entry.After?["email_hash"]);
    }

    [Fact]
    public async Task RequestChange_WrongPassword_Fails_NoEmail()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var email = new CapturingEmail();
        using var ctx = Ctx(caller, db);
        var result = await BuildAuthService(caller, ctx, email)
            .RequestEmailChangeAsync(
                new ChangeEmailRequest { CurrentPassword = "wrong", NewEmail = "new@t.com" }
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.CurrentPasswordIncorrect, result.Message);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task RequestChange_SameAddress_DifferentCase_EmailUnchanged()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var email = new CapturingEmail();
        using var ctx = Ctx(caller, db);
        var result = await BuildAuthService(caller, ctx, email)
            .RequestEmailChangeAsync(
                new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "MEMBER@T.com" }
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailUnchanged, result.Message);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task RequestChange_AddressOwnedByOtherIdentity_Conflict_NoEmail()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var email = new CapturingEmail();
        using var ctx = Ctx(caller, db);
        // Case-variant of the other identity's address — the app-level D14 check must catch it too.
        var result = await BuildAuthService(caller, ctx, email)
            .RequestEmailChangeAsync(
                new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "Other@T.com" }
            );

        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.User.EmailTaken, result.Message);
        Assert.Empty(email.Sent);
    }

    [Fact]
    public async Task RequestChange_Passwordless_Fails()
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

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        using var ctx = Ctx(caller, db);
        var result = await BuildAuthService(caller, ctx)
            .RequestEmailChangeAsync(
                new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
            );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.ChangeEmailNeedsPassword, result.Message);
    }

    [Fact]
    public async Task RequestChange_SuperAdmin_Forbidden()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        Guid targetPublicId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role
            {
                Name = "Super Admin",
                IsSystem = true,
                IsSuperAdmin = true,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var target = new User
            {
                Email = "root@t.com",
                PasswordHash = "h:pw",
                DisplayName = "Root",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(target);
            seed.SaveChanges();
            targetPublicId = target.PublicId;
        }

        var caller = new FakeCurrentUser { Id = targetPublicId };
        using var ctx = Ctx(caller, db);
        var result = await BuildAuthService(caller, ctx)
            .RequestEmailChangeAsync(
                new ChangeEmailRequest { CurrentPassword = "pw", NewEmail = "new@t.com" }
            );

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.User.ChangeEmailSuperAdmin, result.Message);
    }

    // ── 2. Confirm ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_ValidToken_ChangesEmail_RotatesStamp_NotifiesOld()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            var result = await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
            Assert.True(result.IsSuccess);
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "new@t.com").Html
        );

        var anon = new FakeCurrentUser();
        var confirmEmail = new CapturingEmail();
        var audit = new FakeAuditWriter();
        using (var ctx = Ctx(anon, db))
        {
            var result = await BuildAuthService(anon, ctx, confirmEmail, audit)
                .ConfirmEmailChangeAsync(token);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(MessageKeys.User.EmailChanged, result.Message);
        }

        using var check = Ctx(superAdmin, db);
        var row = check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id);
        Assert.Equal("new@t.com", row.Email);
        Assert.Equal(ws.Member.PublicId, row.PublicId);

        var membership = check
            .WorkspaceMemberships.IgnoreQueryFilters()
            .Single(m => m.UserId == ws.Member.Id && m.OwnerId == ws.OwnerId);
        Assert.Null(membership.LeftAt);

        var names = await UserNameResolver.ResolveAsync(
            new UnitOfWork(check),
            new[] { ws.Member.PublicId },
            ignoreQueryFilters: true
        );
        Assert.Equal("Member", names[ws.Member.PublicId]);

        Assert.Single(confirmEmail.Sent);
        Assert.Equal("member@t.com", confirmEmail.Sent[0].To);

        var entry = audit.Entries.Single(e => e.Action == AuditActions.AuthEmailChanged);
        Assert.Equal(PseudonymHasher.EmailHash("member@t.com"), entry.Before?["email_hash"]);
        Assert.Equal(PseudonymHasher.EmailHash("new@t.com"), entry.After?["email_hash"]);
    }

    [Fact]
    public async Task Confirm_TokenReuse_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "new@t.com").Html
        );

        var anon = new FakeCurrentUser();
        using (var ctx = Ctx(anon, db))
        {
            var first = await BuildAuthService(anon, ctx).ConfirmEmailChangeAsync(token);
            Assert.True(first.IsSuccess);
        }

        using var ctx2 = Ctx(anon, db);
        var second = await BuildAuthService(anon, ctx2).ConfirmEmailChangeAsync(token);
        Assert.False(second.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailChangeLinkInvalid, second.Message);
    }

    [Fact]
    public async Task Confirm_AfterPasswordChange_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "new@t.com").Html
        );

        // Cancellation path: changing the password rotates SecurityStamp, invalidating the pending token.
        using (var ctx = Ctx(caller, db))
        {
            var changed = await BuildAuthService(caller, ctx)
                .ChangePasswordAsync(
                    new ChangePasswordRequest
                    {
                        CurrentPassword = "pw-member",
                        NewPassword = "newpassword1",
                    }
                );
            Assert.True(changed.IsSuccess);
        }

        var anon = new FakeCurrentUser();
        using var confirmCtx = Ctx(anon, db);
        var result = await BuildAuthService(anon, confirmCtx).ConfirmEmailChangeAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailChangeLinkInvalid, result.Message);

        using var check = Ctx(superAdmin, db);
        Assert.Equal(
            "member@t.com",
            check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id).Email
        );
    }

    [Fact]
    public async Task Confirm_AddressTakenMeanwhile_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest
                    {
                        CurrentPassword = "pw-member",
                        NewEmail = "taken-meanwhile@t.com",
                    }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "taken-meanwhile@t.com").Html
        );

        // Someone else registers the address during the 30-minute window.
        using (var seed = Ctx(superAdmin, db))
        {
            var role = seed.Roles.First();
            var interloper = new User
            {
                Email = "taken-meanwhile@t.com",
                PasswordHash = "h:pw",
                DisplayName = "Interloper",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                IsActive = true,
            };
            seed.Users.Add(interloper);
            seed.SaveChanges();
            TestSeed.Join(seed, interloper, ws.OwnerId, role);
        }

        var anon = new FakeCurrentUser();
        using var confirmCtx = Ctx(anon, db);
        var result = await BuildAuthService(anon, confirmCtx).ConfirmEmailChangeAsync(token);

        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.User.EmailTaken, result.Message);

        using var check = Ctx(superAdmin, db);
        Assert.Equal(
            "member@t.com",
            check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id).Email
        );
    }

    [Fact]
    public async Task Confirm_EraseTokenRejected()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var resetTokens = RealResetTokens();
        var token = resetTokens.CreateScoped(
            ws.Member.PublicId,
            ws.Member.SecurityStamp,
            TokenPurposes.Erase
        );

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx).ConfirmEmailChangeAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailChangeLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_ResetTokenRejected()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var resetTokens = RealResetTokens();
        var token = resetTokens.Create(ws.Member.PublicId, ws.Member.SecurityStamp);

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx).ConfirmEmailChangeAsync(token);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailChangeLinkInvalid, result.Message);
    }

    [Fact]
    public async Task Confirm_ThenLogin_OldEmailFails_NewEmailWorks()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "new@t.com").Html
        );

        var anon = new FakeCurrentUser();
        using (var ctx = Ctx(anon, db))
        {
            var confirm = await BuildAuthService(anon, ctx).ConfirmEmailChangeAsync(token);
            Assert.True(confirm.IsSuccess);
        }

        using (var oldLoginCtx = Ctx(new FakeCurrentUser(), db))
        {
            var oldLogin = await BuildAuthService(new FakeCurrentUser(), oldLoginCtx)
                .LoginAsync(new LoginRequest { Email = "member@t.com", Password = "pw-member" });
            Assert.False(oldLogin.IsSuccess);
            Assert.Equal(MessageKeys.Auth.InvalidCredentials, oldLogin.Message);
        }

        using var newLoginCtx = Ctx(new FakeCurrentUser(), db);
        var newLogin = await BuildAuthService(new FakeCurrentUser(), newLoginCtx)
            .LoginAsync(new LoginRequest { Email = "new@t.com", Password = "pw-member" });
        Assert.Equal("ok", newLogin.Data?.Status);
    }

    // ── 2b. Review-finding regressions ──────────────────────────────────────────────────────────

    /// <summary>Review finding #10 — a token minted for one address must not validate for another
    /// after its payload segment is swapped for a well-formed (not garbage) alternative: the HMAC
    /// signs `id|stamp|exp|purpose|payload`, so the old signature no longer matches.</summary>
    [Fact]
    public async Task Confirm_TamperedPayloadSwappedToAnotherAddress_Fails()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "a@t.com" }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "a@t.com").Html
        );

        var parts = token.Split('.');
        var swappedPayload = Convert
            .ToBase64String(System.Text.Encoding.UTF8.GetBytes("b@t.com"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        parts[4] = swappedPayload;
        var tampered = string.Join('.', parts);

        var anon = new FakeCurrentUser();
        using var ctx2 = Ctx(anon, db);
        var result = await BuildAuthService(anon, ctx2).ConfirmEmailChangeAsync(tampered);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.EmailChangeLinkInvalid, result.Message);

        using var check = Ctx(superAdmin, db);
        Assert.Equal(
            "member@t.com",
            check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id).Email
        );
    }

    /// <summary>Review finding #2 — a database-level 23505 on <c>ux_users_email_live</c> (the app-level
    /// D14 check missed a race) must revert the in-memory <c>Email</c>/<c>SecurityStamp</c> mutation and
    /// leave the <see cref="User"/> entity untracked rather than stuck <c>Modified</c> with a change that
    /// was never persisted.</summary>
    [Fact]
    public async Task Confirm_DuplicateKeyOnSave_RevertsInMemoryAndDetaches_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        SeededWorkspace ws;
        using (var seed = Ctx(superAdmin, db))
            ws = SeedWorkspace(seed);

        var caller = new FakeCurrentUser { Id = ws.Member.PublicId, TenantId = ws.OwnerId };
        var requestEmail = new CapturingEmail();
        using (var ctx = Ctx(caller, db))
        {
            await BuildAuthService(caller, ctx, requestEmail)
                .RequestEmailChangeAsync(
                    new ChangeEmailRequest { CurrentPassword = "pw-member", NewEmail = "new@t.com" }
                );
        }
        var token = CapturingEmail.ExtractToken(
            requestEmail.Sent.Single(s => s.To == "new@t.com").Html
        );

        var anon = new FakeCurrentUser();
        using var ctx2 = Ctx(anon, db);
        var real = new UnitOfWork(ctx2);
        var throwing = new ThrowDuplicateKeyUnitOfWork(real);
        var service = new AuthService(
            throwing,
            new IdentityHasher(),
            new FakeTokenService(),
            anon,
            new NoopSettings(),
            RealResetTokens(),
            new CapturingEmail(),
            new NoopBrandingService(),
            new ApiKeyService(throwing, new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(throwing),
            null
        );

        var result = await service.ConfirmEmailChangeAsync(token);

        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.User.EmailTaken, result.Message);
        Assert.Empty(ctx2.ChangeTracker.Entries<User>());

        using var check = Ctx(superAdmin, db);
        Assert.Equal(
            "member@t.com",
            check.Users.IgnoreQueryFilters().Single(u => u.Id == ws.Member.Id).Email
        );
    }

    // ── 3. Validator ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChangeEmailRequestValidator_RejectsBadEmail_AndOver256()
    {
        var validator = new ChangeEmailRequestValidator();

        var badEmail = validator.Validate(
            new ChangeEmailRequest { CurrentPassword = "pw", NewEmail = "not-an-email" }
        );
        Assert.False(badEmail.IsValid);

        var tooLong = validator.Validate(
            new ChangeEmailRequest
            {
                CurrentPassword = "pw",
                NewEmail = new string('a', 251) + "@t.com",
            }
        );
        Assert.False(tooLong.IsValid);

        var ok = validator.Validate(
            new ChangeEmailRequest { CurrentPassword = "pw", NewEmail = "ok@t.com" }
        );
        Assert.True(ok.IsValid);
    }
}
