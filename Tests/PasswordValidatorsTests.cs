using FluentValidation.Results;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Validators;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-14 §6 test 7 — the nine <c>.StrongPassword(...)</c> validators (§3.6) plus the five service-
/// layer re-validations (§3.6 table), proving both speak the SAME policy (never a stale 8-char
/// fallback in the services while the validators enforce 10).
///
/// Doc deviation: the embedded common-password list (see <see cref="PasswordPolicyTests"/>'
/// doc-comment) does not contain the doc's own "password1"/"password123" examples at a length ≥
/// <see cref="PasswordPolicy.MinLength"/> (9 chars — MinLength=10 would reject it as PasswordWeak
/// before ever reaching the common-list check), so this suite uses "qwertyuiop" (10 chars, present
/// in the embedded list) wherever a PasswordCommon assertion is needed.
/// </summary>
public class PasswordValidatorsTests
{
    private const string CommonPw = "qwertyuiop";
    private const string StrongPw = "long-enough-pw-1";

    public static IEnumerable<object[]> Sites()
    {
        yield return new object[]
        {
            "RegisterValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new RegisterValidator()
                        .TestValidate(
                            new RegisterRequest
                            {
                                Email = "a@x.com",
                                Password = pw,
                                DisplayName = "D",
                                RoleId = 1,
                                ProjectKey = "k",
                            }
                        )
                        .Errors.Where(e => e.PropertyName == nameof(RegisterRequest.Password))
            ),
        };
        yield return new object[]
        {
            "RegisterAdminValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new RegisterAdminValidator()
                        .TestValidate(
                            new RegisterAdminRequest
                            {
                                Email = "a@x.com",
                                Password = pw,
                                DisplayName = "D",
                            }
                        )
                        .Errors.Where(e => e.PropertyName == nameof(RegisterAdminRequest.Password))
            ),
        };
        yield return new object[]
        {
            "CreateUserValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new CreateUserValidator()
                        .TestValidate(
                            new CreateUserRequest
                            {
                                Email = "a@x.com",
                                Password = pw,
                                DisplayName = "D",
                                RoleId = 1,
                            }
                        )
                        .Errors.Where(e => e.PropertyName == nameof(CreateUserRequest.Password))
            ),
        };
        yield return new object[]
        {
            "CreateTenantValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new CreateTenantValidator()
                        .TestValidate(
                            new CreateTenantRequest
                            {
                                Email = "a@x.com",
                                Password = pw,
                                DisplayName = "D",
                            }
                        )
                        .Errors.Where(e => e.PropertyName == nameof(CreateTenantRequest.Password))
            ),
        };
        yield return new object[]
        {
            "AcceptInviteRequestValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new AcceptInviteRequestValidator()
                        .TestValidate(
                            new AcceptInviteRequest
                            {
                                Code = "c",
                                Email = "a@x.com",
                                Password = pw,
                                DisplayName = "D",
                            }
                        )
                        .Errors.Where(e => e.PropertyName == nameof(AcceptInviteRequest.Password))
            ),
        };
        yield return new object[]
        {
            "UpgradeDemoValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new UpgradeDemoValidator()
                        .TestValidate(new UpgradeDemoRequest { Email = "a@x.com", Password = pw })
                        .Errors.Where(e => e.PropertyName == nameof(UpgradeDemoRequest.Password))
            ),
        };
        yield return new object[]
        {
            "ChangePasswordRequestValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new ChangePasswordRequestValidator()
                        .TestValidate(
                            new ChangePasswordRequest { CurrentPassword = "x", NewPassword = pw }
                        )
                        .Errors.Where(e =>
                            e.PropertyName == nameof(ChangePasswordRequest.NewPassword)
                        )
            ),
        };
        yield return new object[]
        {
            "ResetPasswordValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new ResetPasswordValidator()
                        .TestValidate(new ResetPasswordRequest { Token = "t", NewPassword = pw })
                        .Errors.Where(e =>
                            e.PropertyName == nameof(ResetPasswordRequest.NewPassword)
                        )
            ),
        };
        yield return new object[]
        {
            "UpdateUserValidator",
            (Func<string, IEnumerable<ValidationFailure>>)(
                pw =>
                    new UpdateUserValidator()
                        .TestValidate(new UpdateUserRequest { RoleId = 1, Password = pw })
                        .Errors.Where(e => e.PropertyName == nameof(UpdateUserRequest.Password))
            ),
        };
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void CommonPassword_IsInvalid(
        string name,
        Func<string, IEnumerable<ValidationFailure>> validate
    )
    {
        var errors = validate(CommonPw).ToList();
        Assert.Contains(errors, e => e.ErrorMessage == MessageKeys.User.PasswordCommon);
    }

    [Theory]
    [MemberData(nameof(Sites))]
    public void StrongPassword_IsValid(
        string name,
        Func<string, IEnumerable<ValidationFailure>> validate
    )
    {
        Assert.Empty(validate(StrongPw));
    }

    [Fact]
    public void UpdateUserValidator_PasswordNull_IsValid()
    {
        var r = new UpdateUserValidator().TestValidate(
            new UpdateUserRequest { RoleId = 1, Password = null }
        );
        r.ShouldNotHaveValidationErrorFor(x => x.Password);
    }

    // ── Service-layer re-validations (§3.6 table; five call sites, four files) ─────────────

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

    private sealed class NoopEmail : IEmailService
    {
        public Task<bool> SendAsync(
            string to,
            string subject,
            string htmlBody,
            CancellationToken ct = default
        ) => Task.FromResult(true);
    }

    private sealed class NoopFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
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

        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> GetAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<Result<Pointer.Application.DTOs.Branding.BrandingResponse>> UpdateAsync(
            Pointer.Application.DTOs.Branding.BrandingWriteDto dto,
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) =>
            Task.FromResult(
                Result<Pointer.Application.DTOs.Branding.BrandingResponse>.Success(
                    DefaultBranding()
                )
            );

        public Task<int> BumpVersionAsync() => Task.FromResult(0);

        public Task<Pointer.Application.DTOs.Branding.BrandingResponse> BuildResponseAsync(
            string publicBase,
            IReadOnlySet<string> existingKinds
        ) => Task.FromResult(DefaultBranding());
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.PublicId.ToString("N");

        public string IssueSelection(User user) => "sel-for-" + user.PublicId.ToString("N");

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "imp-for-" + user.PublicId.ToString("N");
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

    [Fact]
    public async Task ResetPassword_ServiceRejectsEmailAsPassword()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var publicId = Guid.NewGuid();
        Guid stamp;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role
            {
                Name = "Engineer",
                GrantsAdmin = false,
                IsSystem = false,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var user = new User
            {
                PublicId = publicId,
                Email = "reset@t.com",
                PasswordHash = "h:old",
                DisplayName = "U",
                RoleId = role.Id,
                IsActive = true,
                SecurityStamp = Guid.NewGuid(),
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            stamp = user.SecurityStamp;
        }

        var resetTokens = RealResetTokens();
        var token = resetTokens.Create(publicId, stamp);

        using var ctx = Ctx(superAdmin, db);
        var uow = new UnitOfWork(ctx);
        var svc = new AuthService(
            uow,
            new IdentityHasher(),
            new FakeTokenService(),
            superAdmin,
            new NoopSettings(),
            resetTokens,
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );

        var result = await svc.ResetPasswordAsync(
            new ResetPasswordRequest { Token = token, NewPassword = "reset@t.com" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordIsEmail, result.Message);
    }

    [Fact]
    public async Task ChangePassword_ServiceRejectsCommon()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var publicId = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role
            {
                Name = "Engineer",
                GrantsAdmin = false,
                IsSystem = false,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var user = new User
            {
                PublicId = publicId,
                Email = "change@t.com",
                PasswordHash = "h:OldPass1!",
                DisplayName = "U",
                RoleId = role.Id,
                IsActive = true,
                OwnerId = tenant,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            // DB-11f: the User filter's null-tenant bucket is now the platform role (super admins
            // only), so a non-super caller with no membership can no longer see its own row via
            // that branch — a real membership + tenant claim is required, as it always is for a
            // real authenticated member.
            TestSeed.Join(seed, user, tenant, role);
        }

        var caller = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        using var ctx = Ctx(caller, db);
        var uow = new UnitOfWork(ctx);
        var svc = new AuthService(
            uow,
            new IdentityHasher(),
            new FakeTokenService(),
            caller,
            new NoopSettings(),
            RealResetTokens(),
            new NoopEmail(),
            new NoopBrandingService(),
            new ApiKeyService(new UnitOfWork(ctx), new TestApiKeyProtector()),
            new FakeLoginAttemptLimiter(),
            new MembershipService(uow)
        );

        var result = await svc.ChangePasswordAsync(
            new ChangePasswordRequest { CurrentPassword = "OldPass1!", NewPassword = CommonPw }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordCommon, result.Message);
    }

    [Fact]
    public async Task TenantCreate_ServiceRejectsCommon()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var seed = Ctx(superAdmin, db))
        {
            seed.Roles.Add(
                new Role
                {
                    Name = "Workspace Admin",
                    GrantsAdmin = true,
                    IsSystem = true,
                    IsActive = true,
                }
            );
            seed.SaveChanges();
        }

        using var ctx = Ctx(superAdmin, db);
        var uow = new UnitOfWork(ctx);
        var svc = new TenantService(
            uow,
            new IdentityHasher(),
            new NoopFileStorage(),
            new NoopSettings(),
            new Pointer.Infrastructure.Billing.NoopBillingProvider(),
            new MembershipService(uow)
        );

        var result = await svc.CreateAsync(
            new CreateTenantRequest
            {
                Email = "new-tenant@t.com",
                Password = CommonPw,
                DisplayName = "New",
            }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordCommon, result.Message);
    }

    [Fact]
    public async Task AcceptInvite_ServiceRejectsCommon()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        var ownerId = Guid.NewGuid();
        int inviteId;
        using (var seed = Ctx(superAdmin, db))
        {
            var role = new Role
            {
                Name = "Engineer",
                GrantsAdmin = false,
                IsSystem = false,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            seed.Set<Workspace>()
                .Add(
                    new Workspace
                    {
                        Id = ownerId,
                        Name = "T",
                        CreatedAt = DateTime.UtcNow,
                        CreatedBy = ownerId,
                    }
                );
            var invite = new Invite
            {
                OwnerId = ownerId,
                Code = "code-" + Guid.NewGuid().ToString("N"),
                RoleId = role.Id,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                Uses = 0,
            };
            seed.Invites.Add(invite);
            seed.SaveChanges();
            inviteId = invite.Id;
        }

        var code = "";
        using (var read = Ctx(superAdmin, db))
            code = read.Invites.IgnoreQueryFilters().Single(i => i.Id == inviteId).Code;

        var anon = new FakeCurrentUser();
        using var ctx = Ctx(anon, db);
        var uow = new UnitOfWork(ctx);
        var svc = new InviteService(
            uow,
            anon,
            new IdentityHasher(),
            new FakeTokenService(),
            new NoopSettings(),
            new PassThroughEntitlements(),
            new NoopEmail(),
            new NoopBrandingService(),
            new MembershipService(uow)
        );

        var result = await svc.AcceptAsync(
            new AcceptInviteRequest
            {
                Code = code,
                Email = "accept@t.com",
                Password = CommonPw,
                DisplayName = "A",
            }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordCommon, result.Message);
    }

    [Fact]
    public async Task UserUpdate_ServiceRejectsEmailAsPassword()
    {
        var db = Guid.NewGuid().ToString();
        var ownerId = Guid.NewGuid();
        var targetPublicId = Guid.NewGuid();
        int targetUserId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role
            {
                Name = "Engineer",
                GrantsAdmin = false,
                IsSystem = false,
                IsActive = true,
            };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var target = new User
            {
                PublicId = targetPublicId,
                Email = "target@t.com",
                PasswordHash = "h:x",
                DisplayName = "Target",
                RoleId = role.Id,
                OwnerId = ownerId,
                IsActive = true,
            };
            seed.Users.Add(target);
            seed.SaveChanges();
            TestSeed.Join(seed, target, ownerId, role);
            targetUserId = target.Id;
        }

        var admin = new FakeCurrentUser { TenantId = ownerId };
        using var ctx = Ctx(admin, db);
        var uow = new UnitOfWork(ctx);
        var svc = new UserService(
            uow,
            new IdentityHasher(),
            admin,
            new NoopEmail(),
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(uow)
        );

        var result = await svc.UpdateAsync(
            targetUserId,
            new UpdateUserRequest { Password = "target@t.com" }
        );

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.User.PasswordIsEmail, result.Message);
    }
}
