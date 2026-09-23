using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// GET /api/public/stats: thresholds hide small numbers, published numbers are rounded down, and
/// the payload never carries anything tenant-identifying.
/// </summary>
public class PublicStatsTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAdmin => false;
        public bool IsSuperAdmin => false;
        public bool IsQuickAccess => false;
        public Guid? TenantId => null; // anonymous caller — matches the real [AllowAnonymous] request
        public int? RoleId => null;
        public string? KeyScopes => null;
        public string? Scope => null;
        public long? ImpersonationSessionId => null;
        public bool IsImpersonating => false;
    }

    private static AppDbContext BuildContext(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static PlatformInsightsService BuildService(string dbName) =>
        new(new UnitOfWork(BuildContext(dbName)), new FakeCurrentUser());

    private static Project MakeProject(string key, Guid? owner, string? aiTools = null) =>
        new()
        {
            Key = key,
            Name = key,
            OwnerId = owner ?? Guid.NewGuid(),
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true,
            AiToolsUsed = aiTools,
        };

    private static Comment MakeComment(
        int projectId,
        Guid owner,
        DateTime createdAt,
        DateTime? appliedAt,
        string? language = null
    ) =>
        new()
        {
            ProjectId = projectId,
            OwnerId = owner,
            AuthorId = Guid.NewGuid(),
            Body = "x",
            Status = appliedAt != null ? CommentStatus.Applied : CommentStatus.Open,
            CreatedAt = createdAt,
            AppliedAt = appliedAt,
            Language = language,
            Element = new ElementCapture(),
        };

    [Fact]
    public async Task BelowEveryThreshold_EverythingIsNullOrEmpty()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = BuildContext(db))
        {
            // 3 projects across 2 tenants, 10 applied comments — below every threshold
            // (Projects >= 10, Workspaces >= 5, AppliedComments >= 50, median needs >= 20).
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var p1 = MakeProject("p1", tenantA);
            var p2 = MakeProject("p2", tenantA);
            var p3 = MakeProject("p3", tenantB);
            seed.Projects.AddRange(p1, p2, p3);
            seed.SaveChanges();

            var now = DateTime.UtcNow;
            for (var i = 0; i < 10; i++)
                seed.Comments.Add(MakeComment(p1.Id, tenantA, now.AddHours(-5), now.AddHours(-2)));
            seed.SaveChanges();
        }

        var result = await BuildService(db).GetPublicStatsAsync();

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.Null(data.AppliedComments);
        Assert.Null(data.Projects);
        Assert.Null(data.Workspaces);
        Assert.Null(data.MedianHoursToApply);
        Assert.Empty(data.Languages);
        Assert.Empty(data.AiTools);
    }

    [Fact]
    public async Task AboveThresholds_CountsAreRoundedDown()
    {
        var db = Guid.NewGuid().ToString();
        var tenants = Enumerable.Range(0, 27).Select(_ => Guid.NewGuid()).ToList(); // raw Workspaces = 27 -> 20
        using (var seed = BuildContext(db))
        {
            var projects = new List<Project>();
            for (var i = 0; i < 34; i++) // raw Projects = 34 -> 30
                projects.Add(MakeProject($"proj-{i}", tenants[i % tenants.Count]));
            seed.Projects.AddRange(projects);
            seed.SaveChanges();

            var now = DateTime.UtcNow;
            for (var i = 0; i < 67; i++) // raw AppliedComments = 67 -> 60
            {
                var p = projects[i % projects.Count];
                seed.Comments.Add(
                    MakeComment(p.Id, p.OwnerId!.Value, now.AddHours(-(i + 3)), now.AddHours(-i))
                );
            }
            seed.SaveChanges();
        }

        var result = await BuildService(db).GetPublicStatsAsync();

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.Equal(60, data.AppliedComments);
        Assert.Equal(30, data.Projects);
        Assert.Equal(20, data.Workspaces);
        Assert.Equal(3.0, data.MedianHoursToApply); // every comment has a fixed 3h created->applied gap
    }

    [Fact]
    public async Task Median_UsesItsOwnLowerThreshold_IndependentOfAppliedCommentsThreshold()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = BuildContext(db))
        {
            var tenant = Guid.NewGuid();
            var p = MakeProject("p", tenant);
            seed.Projects.Add(p);
            seed.SaveChanges();

            var now = DateTime.UtcNow;
            // 25 applied comments: >= MinAppliedForMedian (20) but < MinAppliedComments (50).
            for (var i = 0; i < 25; i++)
                seed.Comments.Add(MakeComment(p.Id, tenant, now.AddHours(-5), now.AddHours(-1)));
            seed.SaveChanges();
        }

        var result = await BuildService(db).GetPublicStatsAsync();

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.Null(data.AppliedComments); // 25 < 50 — hidden
        Assert.NotNull(data.MedianHoursToApply); // 25 >= 20 — shown
        Assert.Equal(4.0, data.MedianHoursToApply);
    }

    [Fact]
    public async Task LanguagesAndAiTools_OnlyPublishedWhenAtLeastThreeEntriesQualify()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = BuildContext(db))
        {
            var p1 = MakeProject(
                "p1",
                null,
                aiTools: "[\"claude-code\",\"cursor\",\"opencode\",\"windsurf\"]"
            );
            var p2 = MakeProject("p2", null, aiTools: "[\"claude-code\",\"cursor\",\"windsurf\"]");
            var p3 = MakeProject("p3", null, aiTools: "[\"claude-code\",\"cursor\",\"opencode\"]");
            var p4 = MakeProject("p4", null, aiTools: "[\"claude-code\"]");
            var p5 = MakeProject("p5", null, aiTools: "[\"opencode\"]");
            seed.Projects.AddRange(p1, p2, p3, p4, p5);
            seed.SaveChanges();

            var now = DateTime.UtcNow;
            void Comment(Project proj, string lang) =>
                seed.Comments.Add(MakeComment(proj.Id, proj.OwnerId!.Value, now, null, lang));

            // ar: p1,p2,p3,p4 (4 projects) -> qualifies
            Comment(p1, "ar");
            Comment(p2, "ar");
            Comment(p3, "ar");
            Comment(p4, "ar");
            // en: p1,p2,p3 (3 projects) -> qualifies
            Comment(p1, "en");
            Comment(p2, "en");
            Comment(p3, "en");
            // de: p1,p2,p5 (3 projects) -> qualifies
            Comment(p1, "de");
            Comment(p2, "de");
            Comment(p5, "de");
            // fr: p1,p2 (2 projects) -> does NOT qualify
            Comment(p1, "fr");
            Comment(p2, "fr");
            seed.SaveChanges();
        }

        var result = await BuildService(db).GetPublicStatsAsync();

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        // 3 languages clear the >=3-projects bar, so (>= MinListEntriesToPublish) the list publishes.
        Assert.Equal(new[] { "ar", "de", "en" }, data.Languages);
        Assert.DoesNotContain("fr", data.Languages);

        // claude-code (4 projects), cursor (3), opencode (3) qualify; windsurf (2) does not.
        Assert.Equal(new[] { "claude-code", "cursor", "opencode" }, data.AiTools);
        Assert.DoesNotContain("windsurf", data.AiTools);
    }

    [Fact]
    public async Task Languages_ListStaysEmpty_WhenFewerThanThreeEntriesQualify()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = BuildContext(db))
        {
            var p1 = MakeProject("p1", null);
            var p2 = MakeProject("p2", null);
            var p3 = MakeProject("p3", null);
            seed.Projects.AddRange(p1, p2, p3);
            seed.SaveChanges();

            var now = DateTime.UtcNow;
            // Only "ar" qualifies (3 projects); nothing else even comes close.
            seed.Comments.Add(MakeComment(p1.Id, p1.OwnerId!.Value, now, null, "ar"));
            seed.Comments.Add(MakeComment(p2.Id, p2.OwnerId!.Value, now, null, "ar"));
            seed.Comments.Add(MakeComment(p3.Id, p3.OwnerId!.Value, now, null, "ar"));
            seed.SaveChanges();
        }

        var result = await BuildService(db).GetPublicStatsAsync();

        Assert.True(result.IsSuccess);
        // Exactly one qualifying language is still below MinListEntriesToPublish (3) — publishing
        // just "ar" alone could out a single tenant more precisely than the aggregate counts do.
        Assert.Empty(result.Data!.Languages);
    }

    [Fact]
    public void ResponseShape_CarriesNoTenantIdentifyingFields()
    {
        var props = typeof(PublicStatsResponse)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(
            new[]
            {
                "AiTools",
                "AppliedComments",
                "Languages",
                "MedianHoursToApply",
                "Projects",
                "Workspaces",
            },
            props
        );
    }
}
