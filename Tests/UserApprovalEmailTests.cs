using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
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
/// UserService.ApproveAsync/RejectAsync name the user's workspace in their notification email
/// (subject + body), falling back to the pre-existing workspace-agnostic wording when the workspace
/// row is missing or still on the DB-03 boot-time placeholder name.
/// </summary>
public class UserApprovalEmailTests
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

    private sealed class SpyEmailService : IEmailService
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

    private static UserService Svc(
        AppDbContext db,
        SpyEmailService email,
        ICurrentUser? user = null
    )
    {
        var caller = user ?? new FakeCurrentUser { IsSuperAdmin = true };
        var uow = new UnitOfWork(db);
        return new UserService(
            uow,
            new IdentityHasher(),
            caller,
            email,
            new PassThroughEntitlements(),
            new NoopBrandingService(),
            new MembershipService(uow)
        );
    }

    // Seeds a pending user in a workspace with the given name; returns (userId, tenantId, roleId).
    private static (int userId, Guid tenant, int roleId) SeedPendingUser(
        string dbName,
        string workspaceName
    )
    {
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var tenant = Guid.NewGuid();
        seed.Workspaces.Add(
            new Workspace
            {
                Id = tenant,
                Name = workspaceName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = tenant,
            }
        );
        var role = new Role
        {
            Name = "Developer",
            GrantsAdmin = false,
            IsActive = true,
            OwnerId = tenant,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();

        var user = new User
        {
            Email = "pending@acme.com",
            PasswordHash = "x",
            DisplayName = "Pending User",
            RoleId = role.Id,
            PublicId = Guid.NewGuid(),
            ApprovalStatus = ApprovalStatus.Pending,
            IsActive = false,
            OwnerId = tenant,
        };
        seed.Users.Add(user);
        seed.SaveChanges();
        TestSeed.Join(seed, user, tenant, role, isActive: false, status: ApprovalStatus.Pending);

        return (user.Id, tenant, role.Id);
    }

    [Fact]
    public async Task Approve_NamedWorkspace_SubjectAndBodyIncludeWorkspaceName()
    {
        var dbName = Guid.NewGuid().ToString();
        var (userId, _, roleId) = SeedPendingUser(dbName, "Acme Inc");
        var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var spy = new SpyEmailService();

        var result = await Svc(db, spy)
            .ApproveAsync(userId, new ApproveUserRequest { RoleId = roleId });

        Assert.True(result.IsSuccess);
        Assert.Single(spy.Sent);
        Assert.Equal("Your Pointer account for Acme Inc is approved", spy.Sent[0].Subject);
        Assert.Contains("Acme Inc", spy.Sent[0].Html);
    }

    [Fact]
    public async Task Approve_PlaceholderWorkspace_FallsBackToOldWording()
    {
        var dbName = Guid.NewGuid().ToString();
        var (userId, _, roleId) = SeedPendingUser(dbName, Workspace.PlaceholderName);
        var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var spy = new SpyEmailService();

        var result = await Svc(db, spy)
            .ApproveAsync(userId, new ApproveUserRequest { RoleId = roleId });

        Assert.True(result.IsSuccess);
        Assert.Single(spy.Sent);
        Assert.Equal("Your Pointer account is approved", spy.Sent[0].Subject);
        Assert.DoesNotContain("workspace", spy.Sent[0].Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approve_WorkspaceNameWithMarkup_IsHtmlEncodedInBody()
    {
        var dbName = Guid.NewGuid().ToString();
        var (userId, _, roleId) = SeedPendingUser(dbName, "<b>Acme</b>");
        var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var spy = new SpyEmailService();

        var result = await Svc(db, spy)
            .ApproveAsync(userId, new ApproveUserRequest { RoleId = roleId });

        Assert.True(result.IsSuccess);
        Assert.Contains("&lt;b&gt;Acme&lt;/b&gt;", spy.Sent[0].Html);
        Assert.DoesNotContain("<b>Acme</b> workspace", spy.Sent[0].Html);
    }

    [Fact]
    public async Task Reject_NamedWorkspace_SubjectAndBodyIncludeWorkspaceName()
    {
        var dbName = Guid.NewGuid().ToString();
        var (userId, _, _) = SeedPendingUser(dbName, "Acme Inc");
        var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var spy = new SpyEmailService();

        var result = await Svc(db, spy).RejectAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Single(spy.Sent);
        Assert.Equal("Your Pointer account request for Acme Inc", spy.Sent[0].Subject);
        Assert.Contains("Acme Inc", spy.Sent[0].Html);
    }

    [Fact]
    public async Task Reject_PlaceholderWorkspace_FallsBackToOldWording()
    {
        var dbName = Guid.NewGuid().ToString();
        var (userId, _, _) = SeedPendingUser(dbName, Workspace.PlaceholderName);
        var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var spy = new SpyEmailService();

        var result = await Svc(db, spy).RejectAsync(userId);

        Assert.True(result.IsSuccess);
        Assert.Single(spy.Sent);
        Assert.Equal("Your Pointer account request", spy.Sent[0].Subject);
        Assert.DoesNotContain("workspace", spy.Sent[0].Html, StringComparison.OrdinalIgnoreCase);
    }
}
