using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Browser-based ("device code") CLI sign-in, mirroring `gh auth login`: start (anonymous) mints a
/// device/user code pair, the dashboard approves or denies it by user code (authenticated,
/// non-super-admin), and poll (anonymous) hands the CLI its personal API key exactly once.
/// </summary>
public class DeviceLoginServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static DeviceLoginService BuildService(AppDbContext db, ICurrentUser user) =>
        new(new UnitOfWork(db), user, new ApiKeyService(new UnitOfWork(db), new TestApiKeyProtector()), new NoopBrandingService(), new MembershipService(new UnitOfWork(db)));

    private static Guid SeedUser(string dbName, out Guid tenant, bool superAdmin = false)
    {
        tenant = Guid.NewGuid();
        var publicId = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var role = new Role { Name = superAdmin ? "Super Admin" : "Developer", IsActive = true, OwnerId = null, IsSuperAdmin = superAdmin };
        seed.Roles.Add(role);
        seed.SaveChanges();
        var user = new User
        {
            PublicId = publicId,
            Email = "dev@example.com",
            PasswordHash = "h:pw",
            DisplayName = "Dev",
            RoleId = role.Id,
            OwnerId = tenant,
            IsActive = true,
            ApprovalStatus = ApprovalStatus.Approved,
        };
        seed.Users.Add(user);
        seed.SaveChanges();
        if (!superAdmin)
            TestSeed.Join(seed, user, tenant, role);
        return publicId;
    }

    [Fact]
    public async Task Start_ReturnsCodesAndVerificationUrl()
    {
        var db = Guid.NewGuid().ToString();
        var svc = BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser());

        var result = await svc.StartAsync(new DeviceLoginStartRequest { ClientName = "pointer-feedback CLI on Test" });

        Assert.True(result.IsSuccess);
        Assert.Equal(43, result.Data!.DeviceCode.Length);
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}$", result.Data.UserCode);
        Assert.DoesNotContain('0', result.Data.UserCode);
        Assert.DoesNotContain('O', result.Data.UserCode);
        Assert.DoesNotContain('1', result.Data.UserCode);
        Assert.DoesNotContain('I', result.Data.UserCode);
        Assert.Equal("https://app.pointer.moamen.work/cli-login?code=" + result.Data.UserCode, result.Data.VerificationUrl);
        Assert.Equal(600, result.Data.ExpiresInSeconds);
        Assert.Equal(3, result.Data.IntervalSeconds);
    }

    [Fact]
    public async Task FullFlow_StartApprovePoll_ReturnsKeyOnceThenExpired()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest { ClientName = "CLI" });
        Assert.True(start.IsSuccess);

        // Poll before approval → pending.
        var pending = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = start.Data!.DeviceCode });
        Assert.Equal("pending", pending.Data!.Status);
        Assert.Null(pending.Data.ApiKey);

        // The signed-in user approves by user code.
        var approver = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var approve = await BuildService(BuildContext(approver, db), approver)
            .ApproveAsync(start.Data.UserCode);
        Assert.True(approve.IsSuccess);
        Assert.Equal("approved", approve.Data!.Status);

        // First poll after approval → the key, exactly once.
        var firstPoll = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = start.Data.DeviceCode });
        Assert.Equal("approved", firstPoll.Data!.Status);
        Assert.False(string.IsNullOrEmpty(firstPoll.Data.ApiKey));
        Assert.StartsWith("ptr_", firstPoll.Data.ApiKey);
        Assert.Equal("dev@example.com", firstPoll.Data.Email);
        Assert.Equal("Dev", firstPoll.Data.DisplayName);

        // Second poll → the row is consumed; must never hand out the key twice.
        var secondPoll = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = start.Data.DeviceCode });
        Assert.Equal("expired", secondPoll.Data!.Status);
        Assert.Null(secondPoll.Data.ApiKey);
    }

    [Fact]
    public async Task Deny_PollReportsDenied()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());

        var denier = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var deny = await BuildService(BuildContext(denier, db), denier).DenyAsync(start.Data!.UserCode);
        Assert.True(deny.IsSuccess);
        Assert.Equal("denied", deny.Data!.Status);

        var poll = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = start.Data.DeviceCode });
        Assert.Equal("denied", poll.Data!.Status);
    }

    [Fact]
    public async Task Poll_UnknownDeviceCode_ReturnsUnknown()
    {
        var db = Guid.NewGuid().ToString();
        var result = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = "not-a-real-code" });

        Assert.Equal("unknown", result.Data!.Status);
    }

    [Fact]
    public async Task Poll_ExpiredPendingRow_ReturnsExpired()
    {
        var db = Guid.NewGuid().ToString();
        var rawDeviceCode = Pointer.Application.Common.QuickAccessTokenGenerator.NewToken();
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.DeviceLogins.Add(new DeviceLogin
            {
                DeviceCodeHash = Pointer.Application.Common.QuickAccessTokenGenerator.Hash(rawDeviceCode),
                UserCode = "ABCD-EFGH",
                ClientName = "CLI",
                Status = DeviceLoginStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddSeconds(-1),
            });
            seed.SaveChanges();
        }

        var result = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .PollAsync(new DeviceLoginPollRequest { DeviceCode = rawDeviceCode });

        Assert.Equal("expired", result.Data!.Status);
    }

    [Fact]
    public async Task Approve_UnknownUserCode_ReturnsNotFound()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var approver = new FakeCurrentUser { Id = publicId, TenantId = tenant };

        var result = await BuildService(BuildContext(approver, db), approver).ApproveAsync("ZZZZ-ZZZZ");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task Approve_AlreadyApproved_ReturnsConflict()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var approver = new FakeCurrentUser { Id = publicId, TenantId = tenant };

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());
        var first = await BuildService(BuildContext(approver, db), approver).ApproveAsync(start.Data!.UserCode);
        Assert.True(first.IsSuccess);

        var second = await BuildService(BuildContext(approver, db), approver).ApproveAsync(start.Data.UserCode);
        Assert.True(second.IsConflict);
    }

    // §6.11 (DB-11a review): approval is refused when the caller's own MEMBERSHIP in the tenant is
    // not live/active/Approved — a disabled member cannot approve a device login even though their
    // identity is otherwise fine.
    [Fact]
    public async Task Approve_DisabledMembership_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        Guid tenant = Guid.NewGuid();
        Guid publicId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = new Role { Name = "Developer", IsActive = true };
            seed.Roles.Add(role);
            seed.SaveChanges();
            var user = new User
            {
                PublicId = Guid.NewGuid(),
                Email = "disabled-dev@example.com",
                PasswordHash = "h:pw",
                DisplayName = "Disabled Dev",
                RoleId = role.Id,
                OwnerId = tenant,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(user);
            seed.SaveChanges();
            TestSeed.Join(seed, user, tenant, role, isActive: false);
            publicId = user.PublicId;
        }

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());

        var approver = new FakeCurrentUser { Id = publicId, TenantId = tenant };
        var result = await BuildService(BuildContext(approver, db), approver).ApproveAsync(start.Data!.UserCode);

        Assert.True(result.IsForbidden);
    }

    // §6.11 (DB-11a review): a caller with no membership at all in the claimed tenant is refused
    // the same way as one with a disabled membership.
    [Fact]
    public async Task Approve_NoMembershipInTenant_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out _);
        var otherTenant = Guid.NewGuid();

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());

        // Claims a DIFFERENT workspace than the one it actually has a membership in.
        var approver = new FakeCurrentUser { Id = publicId, TenantId = otherTenant };
        var result = await BuildService(BuildContext(approver, db), approver).ApproveAsync(start.Data!.UserCode);

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task Approve_SuperAdmin_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        var superAdminId = SeedUser(db, out var tenant, superAdmin: true);
        var superAdmin = new FakeCurrentUser { Id = superAdminId, TenantId = tenant, IsSuperAdmin = true };

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());

        var result = await BuildService(BuildContext(superAdmin, db), superAdmin).ApproveAsync(start.Data!.UserCode);

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task GetInfo_SuperAdmin_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        var superAdminId = SeedUser(db, out var tenant, superAdmin: true);
        var superAdmin = new FakeCurrentUser { Id = superAdminId, TenantId = tenant, IsSuperAdmin = true };

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest());

        var result = await BuildService(BuildContext(superAdmin, db), superAdmin).GetInfoAsync(start.Data!.UserCode);

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task GetInfo_WrongCode_ReturnsNotFound()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var user = new FakeCurrentUser { Id = publicId, TenantId = tenant };

        var result = await BuildService(BuildContext(user, db), user).GetInfoAsync("NOPE-CODE");

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task GetInfo_ReturnsClientNameAndPendingStatus()
    {
        var db = Guid.NewGuid().ToString();
        var publicId = SeedUser(db, out var tenant);
        var user = new FakeCurrentUser { Id = publicId, TenantId = tenant };

        var start = await BuildService(BuildContext(new FakeCurrentUser(), db), new FakeCurrentUser())
            .StartAsync(new DeviceLoginStartRequest { ClientName = "pointer-feedback CLI on Test" });

        var info = await BuildService(BuildContext(user, db), user).GetInfoAsync(start.Data!.UserCode);

        Assert.True(info.IsSuccess);
        Assert.Equal("pending", info.Data!.Status);
        Assert.Equal("pointer-feedback CLI on Test", info.Data.ClientName);
        Assert.Equal(start.Data.UserCode, info.Data.UserCode);
    }
}
