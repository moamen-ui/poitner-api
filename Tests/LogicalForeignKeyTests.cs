using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-06: real foreign keys for the integer logical references that previously had none
/// (ai_rules/predefined_actions/predefined_action_suggestions/quick_access_links.project_id,
/// quick_access_links.invite_id, invites.project_id/role_id/plan_id, role_tenant_overrides.role_id,
/// usage_events.project_id), plus the notifications.project_id cascade fix.
/// </summary>
public class LogicalForeignKeyTests
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

    /// <summary>
    /// Sqlite shared-cache fixture (enforces FKs and cascades, unlike InMemory). Copied from
    /// Tests/UsageEventFirstCommentTests.cs:43-60.
    /// </summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private static async Task<(Guid ownerId, int projectId)> SeedWorkspaceAndProjectAsync(
        AppDbContext ctx
    )
    {
        var ownerId = Guid.NewGuid();
        ctx.Workspaces.Add(
            new Workspace
            {
                Id = ownerId,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = ownerId,
            }
        );
        var project = new Project
        {
            Key = "proj-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Project",
            OwnerId = ownerId,
        };
        ctx.Projects.Add(project);
        await ctx.SaveChangesAsync();
        return (ownerId, project.Id);
    }

    // ── 1. PredefinedAction_WithUnknownProject_IsRejected ───────────────────────────────

    [Fact]
    public async Task PredefinedAction_WithUnknownProject_IsRejected()
    {
        using var db = new TestDb();
        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = db.MakeContext(admin);

        var (ownerId, _) = await SeedWorkspaceAndProjectAsync(ctx);

        ctx.PredefinedActions.Add(
            new PredefinedAction
            {
                OwnerId = ownerId,
                ProjectId = 999,
                Text = "Do a thing",
                Prompt = "Do a thing",
            }
        );

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    // ── 2. DeletingProject_CascadesActionsSuggestionsRulesLinks ─────────────────────────

    [Fact]
    public async Task DeletingProject_CascadesActionsSuggestionsRulesLinks()
    {
        using var db = new TestDb();
        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        int projectId;
        Guid ownerId;

        using (var seed = db.MakeContext(admin))
        {
            (ownerId, projectId) = await SeedWorkspaceAndProjectAsync(seed);

            seed.AiRules.Add(
                new AiRule
                {
                    OwnerId = ownerId,
                    ProjectId = projectId,
                    Title = "Rule",
                    Prompt = "Do it well",
                }
            );
            seed.PredefinedActions.Add(
                new PredefinedAction
                {
                    OwnerId = ownerId,
                    ProjectId = projectId,
                    Text = "Action",
                    Prompt = "Action prompt",
                }
            );
            var suggestion = new PredefinedActionSuggestion
            {
                OwnerId = ownerId,
                ProjectId = projectId,
                Text = "Suggestion",
                Prompt = "Suggestion prompt",
            };
            seed.PredefinedActionSuggestions.Add(suggestion);
            await seed.SaveChangesAsync();

            var invite = new Invite
            {
                OwnerId = ownerId,
                Code = "code-" + Guid.NewGuid().ToString("N")[..8],
                ProjectId = projectId,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            };
            seed.Invites.Add(invite);
            await seed.SaveChangesAsync();

            seed.QuickAccessLinks.Add(
                new QuickAccessLink
                {
                    OwnerId = ownerId,
                    UserId = Guid.NewGuid(),
                    ProjectId = projectId,
                    InviteId = invite.Id,
                    TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
                    ExpiresAt = DateTime.UtcNow.AddDays(1),
                }
            );

            seed.Notifications.Add(
                new Notification
                {
                    OwnerId = ownerId,
                    UserId = ownerId,
                    Type = NotificationType.SuggestionSubmitted,
                    CommentId = null,
                    ProjectId = projectId,
                    SuggestionId = suggestion.Id,
                }
            );

            await seed.SaveChangesAsync();
        }

        using (var ctx = db.MakeContext(admin))
        {
            var project = await ctx.Projects.SingleAsync(p => p.Id == projectId);
            ctx.Projects.Remove(project);
            await ctx.SaveChangesAsync();
        }

        using var verify = db.MakeContext(admin);
        Assert.Empty(verify.AiRules.IgnoreQueryFilters().Where(x => x.ProjectId == projectId));
        Assert.Empty(
            verify.PredefinedActions.IgnoreQueryFilters().Where(x => x.ProjectId == projectId)
        );
        Assert.Empty(
            verify
                .PredefinedActionSuggestions.IgnoreQueryFilters()
                .Where(x => x.ProjectId == projectId)
        );
        Assert.Empty(
            verify.QuickAccessLinks.IgnoreQueryFilters().Where(x => x.ProjectId == projectId)
        );
        Assert.Empty(
            verify.Notifications.IgnoreQueryFilters().Where(x => x.ProjectId == projectId)
        );
    }

    // ── 3. DeletingProject_SetsInviteAndUsageEventProjectNull ───────────────────────────

    [Fact]
    public async Task DeletingProject_SetsInviteAndUsageEventProjectNull()
    {
        using var db = new TestDb();
        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        int projectId,
            inviteId,
            usageEventId;
        Guid ownerId;

        using (var seed = db.MakeContext(admin))
        {
            (ownerId, projectId) = await SeedWorkspaceAndProjectAsync(seed);

            var invite = new Invite
            {
                OwnerId = ownerId,
                Code = "code-" + Guid.NewGuid().ToString("N")[..8],
                ProjectId = projectId,
                ExpiresAt = DateTime.UtcNow.AddDays(7),
            };
            seed.Invites.Add(invite);

            var usageEvent = new UsageEvent
            {
                OwnerId = ownerId,
                ProjectId = projectId,
                Type = "first_comment",
                Source = "test",
                CreatedAt = DateTime.UtcNow,
            };
            seed.UsageEvents.Add(usageEvent);

            await seed.SaveChangesAsync();
            inviteId = invite.Id;
            usageEventId = usageEvent.Id;
        }

        using (var ctx = db.MakeContext(admin))
        {
            var project = await ctx.Projects.SingleAsync(p => p.Id == projectId);
            ctx.Projects.Remove(project);
            await ctx.SaveChangesAsync();
        }

        using var verify = db.MakeContext(admin);
        var survivingInvite = await verify
            .Invites.IgnoreQueryFilters()
            .SingleAsync(i => i.Id == inviteId);
        Assert.Null(survivingInvite.ProjectId);

        var survivingUsageEvent = await verify.UsageEvents.SingleAsync(e => e.Id == usageEventId);
        Assert.Null(survivingUsageEvent.ProjectId);
    }

    // ── 4. Invite_WithUnknownPlan_IsRejected ────────────────────────────────────────────

    [Fact]
    public async Task Invite_WithUnknownPlan_IsRejected()
    {
        using var db = new TestDb();
        var admin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = db.MakeContext(admin);

        var ownerId = Guid.NewGuid();
        ctx.Workspaces.Add(
            new Workspace
            {
                Id = ownerId,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = ownerId,
            }
        );
        await ctx.SaveChangesAsync();

        ctx.Invites.Add(
            new Invite
            {
                OwnerId = ownerId,
                Code = "code-" + Guid.NewGuid().ToString("N")[..8],
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                PlanId = 999,
            }
        );

        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }
}
