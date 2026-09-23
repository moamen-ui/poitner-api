using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Audit;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-15 review finding #2 (HIGH): DemoService's two analytics catch blocks (demo_started in
/// ProvisionAsync, workspace_converted in UpgradeAsync) must ClearChangeTracker() after swallowing a
/// failed UsageEvent insert. Without it, the still-tracked, still-Added UsageEvent is resubmitted —
/// and fails again — by the AuditWriter's SaveChangesAsync that follows on the SAME (request-scoped)
/// DbContext, silently losing the audit row too. Forces the UsageEvent insert to fail with a
/// SaveChangesInterceptor (same technique as AuditWriterTests.FailOnAuditEventInsertInterceptor) and
/// proves the audit row still lands.
/// </summary>
public class DemoServiceAnalyticsFailureTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
    }

    private sealed class FakePasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => "hash:" + password;

        public bool Verify(string password, string hash) => hash == "hash:" + password;
    }

    private sealed class FakeTokenService : ITokenService
    {
        public string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null) =>
            "token-for-" + user.Email;

        public string IssueSelection(User user) => "selection-for-" + user.Email;

        public string IssueImpersonation(
            User user,
            Guid workspaceId,
            long sessionId,
            DateTime expiresAt
        ) => "imp-for-" + user.PublicId.ToString("N");
    }

    private sealed class NoopSettingsService : ISettingsService
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

    private sealed class NoopEmailService : IEmailService
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
                    App = "https://app.pointer.example",
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

    /// <summary>Deterministically fails any SaveChanges(Async) with a pending Added UsageEvent —
    /// same technique as AuditWriterTests.FailOnAuditEventInsertInterceptor.</summary>
    private sealed class FailOnUsageEventInsertInterceptor : SaveChangesInterceptor
    {
        private static void ThrowIfPending(DbContext? context)
        {
            if (
                context?.ChangeTracker.Entries<UsageEvent>().Any(e => e.State == EntityState.Added)
                == true
            )
                throw new InvalidOperationException("simulated usage_event insert failure (test)");
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result
        )
        {
            ThrowIfPending(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default
        )
        {
            ThrowIfPending(eventData.Context);
            return ValueTask.FromResult(result);
        }
    }

    private static (DemoService svc, AppDbContext db) Build(string dbName)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .AddInterceptors(new FailOnUsageEventInsertInterceptor())
            .Options;
        var currentUser = new FakeCurrentUser();
        var db = new AppDbContext(opts, currentUser, new ConfigurationBuilder().Build());
        db.Roles.Add(
            new Role
            {
                Id = 2,
                Name = "Workspace Admin",
                OwnerId = null,
                IsActive = true,
            }
        );
        db.SaveChanges();

        var uow = new UnitOfWork(db);
        // A REAL AuditWriter sharing the SAME DbContext as the UnitOfWork — exactly the production
        // shape (both are resolved from the same request scope).
        var audit = new AuditWriter(
            db,
            currentUser,
            new FakeHttpContextAccessor(),
            new ConfigurationBuilder().Build(),
            NullLogger<AuditWriter>.Instance
        );
        var svc = new DemoService(
            uow,
            new FakePasswordHasher(),
            new FakeTokenService(),
            new NoopEmailService(),
            new NoopSettingsService(),
            new NoopBrandingService(),
            new MembershipService(uow),
            audit
        );
        return (svc, db);
    }

    private static (User User, Guid WorkspaceId) SeedDemoUser(AppDbContext db)
    {
        // Build() already seeded Role Id=2 ("Workspace Admin") — reuse it rather than re-adding
        // (would conflict: the same key already tracked).
        var role = db.Roles.Single(r => r.Id == 2);
        var pid = Guid.NewGuid();
        var workspaceId = pid;
        var expiresAt = DateTime.UtcNow.AddHours(24);
        var user = new User
        {
            PublicId = pid,
            Email = $"demo-{pid.ToString("N")[..8]}@demo.pointer",
            PasswordHash = "hash:olddemo",
            DisplayName = "Demo User",
            RoleId = role.Id,
            Role = role,
            OwnerId = workspaceId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            RecipientEmail = "real@user.com",
        };
        db.Users.Add(user);
        db.SaveChanges();

        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Demo Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = expiresAt,
            }
        );
        db.WorkspaceMemberships.Add(
            new WorkspaceMembership
            {
                UserId = user.Id,
                OwnerId = workspaceId,
                RoleId = role.Id,
                Role = role,
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
                JoinedAt = DateTime.UtcNow,
            }
        );
        db.SaveChanges();

        return (user, workspaceId);
    }

    [Fact]
    public async Task Provision_UsageEventInsertFails_StillWritesAuditRow()
    {
        var (svc, db) = Build(nameof(Provision_UsageEventInsertFails_StillWritesAuditRow));

        var result = await svc.ProvisionAsync("https://demo.pointer.example", "person@real.com");

        Assert.True(result.IsSuccess, result.Message);

        // The forced demo_started insert failure must not poison the shared context: the
        // auth.demo.provisioned audit row from the SaveChangesAsync that follows must still land.
        Assert.Contains(
            db.AuditEvents.IgnoreQueryFilters(),
            e => e.Action == AuditActions.AuthDemoProvisioned
        );
        // And the failed insert must never actually have landed as a usage_events row.
        Assert.DoesNotContain(
            db.UsageEvents.IgnoreQueryFilters(),
            e => e.Type == UsageEventTypes.DemoStarted
        );
    }

    [Fact]
    public async Task Upgrade_UsageEventInsertFails_StillWritesAuditRow()
    {
        var (svc, db) = Build(nameof(Upgrade_UsageEventInsertFails_StillWritesAuditRow));
        var (demo, demoWorkspaceId) = SeedDemoUser(db);

        var result = await svc.UpgradeAsync(
            demo.PublicId,
            demoWorkspaceId,
            new UpgradeDemoRequest
            {
                Email = "permanent@user.com",
                Password = "supersecret",
                DisplayName = "Real Name",
            }
        );

        Assert.True(result.IsSuccess, result.Message);

        // The forced workspace_converted insert failure must not poison the shared context: the
        // auth.demo.upgraded audit row from the SaveChangesAsync that follows must still land.
        Assert.Contains(
            db.AuditEvents.IgnoreQueryFilters(),
            e => e.Action == AuditActions.AuthDemoUpgraded
        );
        Assert.DoesNotContain(
            db.UsageEvents.IgnoreQueryFilters(),
            e => e.Type == UsageEventTypes.WorkspaceConverted
        );
    }
}
