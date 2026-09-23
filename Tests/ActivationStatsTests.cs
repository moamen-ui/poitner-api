using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-15: ActivationStatsService — the F4 funnel (GetFunnelAsync, super admin) and the workspace
/// "getting started" checklist (GetActivationAsync). InMemory provider (aggregation is in-memory
/// over a tiny materialized set — no partial-index behaviour under test here; that is covered on
/// Sqlite by WidgetActivationTests/UsageEventFirstCommentTests).
/// </summary>
public class ActivationStatsTests
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static readonly FakeCurrentUser SuperAdmin = new() { IsSuperAdmin = true };

    private static async Task SeedWorkspaceAsync(string dbName, Guid id)
    {
        using var db = BuildContext(SuperAdmin, dbName);
        db.Workspaces.Add(
            new Workspace
            {
                Id = id,
                Name = "Workspace " + id,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();
    }

    private static async Task<int> SeedProjectAsync(string dbName, Guid ownerId, string key)
    {
        using var db = BuildContext(SuperAdmin, dbName);
        var project = new Project
        {
            Key = key,
            Name = key,
            OwnerId = ownerId,
        };
        db.Set<Project>().Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private static async Task AddEventAsync(
        string dbName,
        string type,
        Guid? ownerId,
        int? projectId = null,
        DateTime? createdAt = null
    )
    {
        using var db = BuildContext(SuperAdmin, dbName);
        db.UsageEvents.Add(
            new UsageEvent
            {
                Type = type,
                Source = "test",
                OwnerId = ownerId,
                ProjectId = projectId,
                CreatedAt = createdAt ?? DateTime.UtcNow,
            }
        );
        await db.SaveChangesAsync();
    }

    private static ActivationStatsService Service(ICurrentUser user, string dbName) =>
        new(new UnitOfWork(BuildContext(user, dbName)), user);

    // ---------------------------------------------------------------------------
    // Funnel (super admin)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Funnel_CountsWorkspacesPerStep_AndDemoPath()
    {
        var dbName = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedWorkspaceAsync(dbName, a);
        await SeedWorkspaceAsync(dbName, b);
        var projectA = await SeedProjectAsync(dbName, a, "a-site");
        var projectB = await SeedProjectAsync(dbName, b, "b-site");

        // A: full demo path to first_apply.
        await AddEventAsync(dbName, UsageEventTypes.DemoStarted, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.WorkspaceConverted, a);
        await AddEventAsync(dbName, UsageEventTypes.WidgetInstalled, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.FirstComment, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.FirstApply, a, projectA);

        // B: no demo — enters at widget_installed, reaches first_comment only.
        await AddEventAsync(dbName, UsageEventTypes.WidgetInstalled, b, projectB);
        await AddEventAsync(dbName, UsageEventTypes.FirstComment, b, projectB);

        var result = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 4);

        Assert.True(result.IsSuccess);
        var steps = result.Data!.Steps.ToDictionary(s => s.Key);
        Assert.Equal(1, steps[UsageEventTypes.DemoStarted].Workspaces);
        Assert.Equal(1, steps[UsageEventTypes.WorkspaceConverted].Workspaces);
        Assert.Equal(2, steps[UsageEventTypes.WidgetInstalled].Workspaces);
        Assert.Equal(2, steps[UsageEventTypes.FirstComment].Workspaces);
        Assert.Equal(1, steps[UsageEventTypes.FirstApply].Workspaces);

        // Only A entered via the demo path.
        Assert.Equal(1, steps[UsageEventTypes.WidgetInstalled].DemoPathWorkspaces);
        Assert.Equal(1, steps[UsageEventTypes.FirstApply].DemoPathWorkspaces);
    }

    [Fact]
    public async Task Funnel_WeeklyActivated_UsesEarliestFirstApplyPerWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = Guid.NewGuid();
        await SeedWorkspaceAsync(dbName, owner);
        var p1 = await SeedProjectAsync(dbName, owner, "p1");
        var p2 = await SeedProjectAsync(dbName, owner, "p2");

        var now = DateTime.UtcNow;
        var earlier = now.AddDays(-14); // two ISO weeks back
        var later = now.AddDays(-1);

        await AddEventAsync(dbName, UsageEventTypes.FirstApply, owner, p1, earlier);
        await AddEventAsync(dbName, UsageEventTypes.FirstApply, owner, p2, later);

        var result = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 4);

        Assert.True(result.IsSuccess);
        // Counted once, all-time.
        Assert.Equal(
            1,
            result.Data!.Steps.Single(s => s.Key == UsageEventTypes.FirstApply).Workspaces
        );
        // And in the EARLIER week only — the later row for the same workspace does not add a
        // second "activated" count in its own week.
        var earlierWeekIndex = result.Data.Weeks.FindIndex(w =>
            w.WeekStart <= DateOnly.FromDateTime(earlier)
            && w.WeekStart.AddDays(7) > DateOnly.FromDateTime(earlier)
        );
        var laterWeekIndex = result.Data.Weeks.FindIndex(w =>
            w.WeekStart <= DateOnly.FromDateTime(later)
            && w.WeekStart.AddDays(7) > DateOnly.FromDateTime(later)
        );
        Assert.True(earlierWeekIndex >= 0);
        Assert.Equal(1, result.Data.Weeks[earlierWeekIndex].Activated);
        if (laterWeekIndex != earlierWeekIndex && laterWeekIndex >= 0)
            Assert.Equal(0, result.Data.Weeks[laterWeekIndex].Activated);
    }

    [Fact]
    public async Task Funnel_HardDeletedDemo_StillCountedAsStarted()
    {
        var dbName = Guid.NewGuid().ToString();
        await AddEventAsync(dbName, UsageEventTypes.DemoStarted, null);

        var result = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            1,
            result.Data!.Steps.Single(s => s.Key == UsageEventTypes.DemoStarted).Workspaces
        );
        var recent = Assert.Single(result.Data.Recent);
        Assert.Null(recent.WorkspaceId);
        Assert.Equal("Deleted workspace", recent.WorkspaceName);
    }

    [Fact]
    public async Task Funnel_TwoHardDeletedDemos_CountedSeparately()
    {
        var dbName = Guid.NewGuid().ToString();
        var now = DateTime.UtcNow;

        // Two NULL-owner first_apply rows in different weeks — the §3.1 rule: each is its own
        // pseudo-workspace (keyed on the row's own id), never merged with another NULL-owner row.
        await AddEventAsync(dbName, UsageEventTypes.FirstApply, null, createdAt: now.AddDays(-1));
        await AddEventAsync(dbName, UsageEventTypes.FirstApply, null, createdAt: now.AddDays(-8));

        var result = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 4);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            2,
            result.Data!.Steps.Single(s => s.Key == UsageEventTypes.FirstApply).Workspaces
        );
        Assert.Equal(1, result.Data.Weeks[^1].Activated);
        Assert.Equal(1, result.Data.Weeks[^2].Activated);
    }

    [Fact]
    public async Task Funnel_WeeksValidated()
    {
        var dbName = Guid.NewGuid().ToString();

        var tooFew = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 0);
        var tooMany = await Service(SuperAdmin, dbName).GetFunnelAsync(weeks: 53);

        Assert.False(tooFew.IsSuccess);
        Assert.False(tooFew.IsForbidden);
        Assert.False(tooMany.IsSuccess);
        Assert.False(tooMany.IsForbidden);
    }

    // ---------------------------------------------------------------------------
    // Workspace checklist
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Activation_TenantB_SeesNothingOfA()
    {
        var dbName = Guid.NewGuid().ToString();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedWorkspaceAsync(dbName, a);
        await SeedWorkspaceAsync(dbName, b);
        var projectA = await SeedProjectAsync(dbName, a, "a-site");

        await AddEventAsync(dbName, UsageEventTypes.DemoStarted, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.WorkspaceConverted, a);
        await AddEventAsync(dbName, UsageEventTypes.WidgetInstalled, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.FirstComment, a, projectA);
        await AddEventAsync(dbName, UsageEventTypes.FirstApply, a, projectA);

        var tenantB = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsAdmin = true,
            TenantId = b,
        };
        var result = await Service(tenantB, dbName).GetActivationAsync();

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.IsDemo);
        Assert.All(result.Data.Steps, s => Assert.False(s.Done));
    }

    [Fact]
    public async Task Activation_SuperAdmin_Forbidden()
    {
        var dbName = Guid.NewGuid().ToString();

        var result = await Service(SuperAdmin, dbName).GetActivationAsync();

        Assert.True(result.IsForbidden);
    }
}
