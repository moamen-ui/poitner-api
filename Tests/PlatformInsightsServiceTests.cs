using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Super-admin (cross-tenant) and workspace-admin (own-tenant) usage insights. Seeds two tenants
/// worth of users/comments/events and asserts every list, both medians, the NotFixed rate (incl.
/// the zero-denominator -> null case), and workspace-admin tenant isolation.
/// </summary>
public class PlatformInsightsServiceTests
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static PlatformInsightsService BuildService(ICurrentUser user, string dbName) =>
        new(new UnitOfWork(BuildContext(user, dbName)), user);

    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    /// <summary>
    /// Seeds:
    /// - Roles: "Workspace Admin" (global) + "Member" (global).
    /// - Users: one workspace-admin per tenant (for the tenant-name lookup) + 3 regular users with
    ///   varied Language (en / null / ar).
    /// - Projects: projA (tenant A), projB (tenant B).
    /// - Comments (6 total, tenant A: c1-c4, tenant B: c5-c6) covering every status, both medians'
    ///   inputs, the NotFixed/AwaitingVerification/Verified proxies, device/browser buckets, and
    ///   every FeatureInsights flag.
    /// - Two widget_language usage events per app-language bucket, on the SAME project each time,
    ///   to prove the count is DISTINCT PROJECTS, not raw event count.
    /// </summary>
    private static (int projA, int projB) Seed(string dbName)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var waRole = new Role { Name = "Workspace Admin", GrantsAdmin = true, IsSystem = true };
        var memberRole = new Role { Name = "Member", GrantsAdmin = false };
        seed.Roles.AddRange(waRole, memberRole);
        seed.SaveChanges();

        seed.Users.AddRange(
            new User { Email = "wa-a@x.com", DisplayName = "Admin A", RoleId = waRole.Id, OwnerId = TenantA, Language = null },
            new User { Email = "wa-b@x.com", DisplayName = "Admin B", RoleId = waRole.Id, OwnerId = TenantB, Language = null },
            new User { Email = "u1@x.com", DisplayName = "U1", RoleId = memberRole.Id, OwnerId = TenantA, Language = "en" },
            new User { Email = "u2@x.com", DisplayName = "U2", RoleId = memberRole.Id, OwnerId = TenantA, Language = null },
            new User { Email = "u3@x.com", DisplayName = "U3", RoleId = memberRole.Id, OwnerId = TenantB, Language = "ar" });
        seed.SaveChanges();

        var projA = new Project { Key = "proj-a", Name = "Project A", OwnerId = TenantA, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
        var projB = new Project { Key = "proj-b", Name = "Project B", OwnerId = TenantB, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
        seed.Projects.AddRange(projA, projB);
        seed.SaveChanges();

        var now = DateTime.UtcNow;

        var c1 = new Comment // tenant A, Open, ar, mobile/Chrome
        {
            ProjectId = projA.Id, OwnerId = TenantA, AuthorId = Guid.NewGuid(), Body = "c1",
            Status = CommentStatus.Open, Language = "ar", CreatedAt = now.AddHours(-10),
            Element = new ElementCapture { DeviceType = "mobile", UserAgent = "Mozilla/5.0 Chrome/120.0" }
        };
        var c2 = new Comment // tenant A, ReadyToApply, en, desktop/Firefox
        {
            ProjectId = projA.Id, OwnerId = TenantA, AuthorId = Guid.NewGuid(), Body = "c2",
            Status = CommentStatus.ReadyToApply, Language = "en", CreatedAt = now.AddHours(-9),
            Element = new ElementCapture { DeviceType = "desktop", UserAgent = "Mozilla/5.0 Firefox/120.0" }
        };
        var c3 = new Comment // tenant A, Applied (awaiting verification), en, desktop/Chrome, feature flags
        {
            ProjectId = projA.Id, OwnerId = TenantA, AuthorId = Guid.NewGuid(), Body = "c3",
            Status = CommentStatus.Applied, Language = "en",
            CreatedAt = now.AddHours(-8), AppliedAt = now.AddHours(-6), // 2h created->applied
            IsBugReport = true, IsPrivate = true,
            Element = new ElementCapture { DeviceType = "desktop", UserAgent = "Mozilla/5.0 Chrome/120.0", ScreenshotUrl = "uploads/shot.png" },
            PickedActions = new List<CommentPickedAction> { new() { Text = "Fix it", Prompt = "fix" } }
        };
        var c4 = new Comment // tenant A, Applied + Verified, unknown language, mobile/Safari
        {
            ProjectId = projA.Id, OwnerId = TenantA, AuthorId = Guid.NewGuid(), Body = "c4",
            Status = CommentStatus.Applied, Language = null,
            CreatedAt = now.AddHours(-7), AppliedAt = now.AddHours(-3), // 4h created->applied
            VerifiedAt = now, // 3h applied->verified
            Element = new ElementCapture { DeviceType = "mobile", UserAgent = "Mozilla/5.0 Safari/605.1" }
        };
        var c5 = new Comment // tenant B, Archived, ar, tablet/Edge
        {
            ProjectId = projB.Id, OwnerId = TenantB, AuthorId = Guid.NewGuid(), Body = "c5",
            Status = CommentStatus.Archived, Language = "ar", CreatedAt = now,
            Element = new ElementCapture { DeviceType = "tablet", UserAgent = "Mozilla/5.0 Edg/120.0" }
        };
        var c6 = new Comment // tenant B, applied-then-reopened (NotFixed proxy), en, no device/UA
        {
            ProjectId = projB.Id, OwnerId = TenantB, AuthorId = Guid.NewGuid(), Body = "c6",
            Status = CommentStatus.Open, Language = "en",
            CreatedAt = now, AppliedAt = now.AddHours(1), // 1h created->applied
            Element = new ElementCapture()
        };

        seed.Comments.AddRange(c1, c2, c3, c4, c5, c6);
        seed.SaveChanges();

        // Two events on the SAME project for the "en" page bucket (must count as 1 distinct project),
        // one event on the other project for "ar".
        seed.UsageEvents.AddRange(
            new UsageEvent { Type = "widget_language", Source = "web-component", ProjectId = projA.Id, OwnerId = TenantA, CreatedAt = now, Meta = "{\"ui\":\"en\",\"browser\":\"en-US\",\"page\":\"en\"}" },
            new UsageEvent { Type = "widget_language", Source = "web-component", ProjectId = projA.Id, OwnerId = TenantA, CreatedAt = now, Meta = "{\"ui\":\"en\",\"browser\":\"en-US\",\"page\":\"en\"}" },
            new UsageEvent { Type = "widget_language", Source = "web-component", ProjectId = projB.Id, OwnerId = TenantB, CreatedAt = now, Meta = "{\"ui\":\"ar\",\"browser\":\"ar-SA\",\"page\":\"ar\"}" },
            // Malformed meta must be skipped without throwing.
            new UsageEvent { Type = "widget_language", Source = "web-component", ProjectId = projB.Id, OwnerId = TenantB, CreatedAt = now, Meta = "not-json" });
        seed.SaveChanges();

        return (projA.Id, projB.Id);
    }

    [Fact]
    public async Task PlatformInsights_NonSuperAdmin_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db);
        var service = BuildService(new FakeCurrentUser { IsAdmin = true, TenantId = TenantA }, db);

        var result = await service.GetPlatformInsightsAsync();

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task PlatformInsights_SuperAdmin_SeesEveryList()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db);
        var service = BuildService(new FakeCurrentUser { IsSuperAdmin = true }, db);

        var result = await service.GetPlatformInsightsAsync();
        Assert.True(result.IsSuccess);
        var data = result.Data!;

        // Languages
        Assert.Equal(1, data.Languages.UserLanguages.Single(s => s.Key == "en").Count);
        Assert.Equal(1, data.Languages.UserLanguages.Single(s => s.Key == "ar").Count);
        Assert.Equal(3, data.Languages.UserLanguages.Single(s => s.Key == "unset").Count); // 2 WA admins + u2

        Assert.Equal(2, data.Languages.CommentLanguages.Single(s => s.Key == "ar").Count); // c1, c5
        Assert.Equal(3, data.Languages.CommentLanguages.Single(s => s.Key == "en").Count); // c2, c3, c6
        Assert.Equal(1, data.Languages.CommentLanguages.Single(s => s.Key == "unknown").Count); // c4

        Assert.Equal(1, data.Languages.AppLanguages.Single(s => s.Key == "en").Count); // 2 events, 1 distinct project
        Assert.Equal(1, data.Languages.AppLanguages.Single(s => s.Key == "ar").Count);
        Assert.Equal(1, data.Languages.BrowserLanguages.Single(s => s.Key == "en-US").Count);
        Assert.Equal(1, data.Languages.BrowserLanguages.Single(s => s.Key == "ar-SA").Count);

        // Funnel
        Assert.Equal(2, data.Funnel.Open); // c1, c6
        Assert.Equal(1, data.Funnel.Ready); // c2
        Assert.Equal(2, data.Funnel.Applied); // c3, c4
        Assert.Equal(1, data.Funnel.Archived); // c5
        Assert.Equal(1, data.Funnel.Verified); // c4
        Assert.Equal(2.0, data.Funnel.MedianHoursCreatedToApplied); // median(1,2,4) = 2
        Assert.Equal(3.0, data.Funnel.MedianHoursAppliedToVerified); // only c4 -> 3

        var byWorkspace = data.Funnel.ByWorkspace;
        var a = byWorkspace.Single(w => w.TenantId == TenantA);
        Assert.Equal("Admin A", a.TenantName);
        Assert.Equal(1, a.Open);
        Assert.Equal(1, a.Ready);
        Assert.Equal(2, a.Applied);
        Assert.Equal(1, a.Verified);
        var b = byWorkspace.Single(w => w.TenantId == TenantB);
        Assert.Equal("Admin B", b.TenantName);
        Assert.Equal(1, b.Open);
        Assert.Equal(0, b.Applied);

        // Verification (proxy semantics — see VerificationInsights doc comment)
        Assert.Equal(1, data.Verification.Verified); // c4
        Assert.Equal(1, data.Verification.NotFixed); // c6 (applied then reopened)
        Assert.Equal(1, data.Verification.AwaitingVerification); // c3 (still Applied, not verified)
        Assert.Equal(0.5, data.Verification.NotFixedRate); // 1 / (1 + 1)

        // Devices
        Assert.Equal(2, data.Devices.DeviceTypes.Single(s => s.Key == "mobile").Count); // c1, c4
        Assert.Equal(2, data.Devices.DeviceTypes.Single(s => s.Key == "desktop").Count); // c2, c3
        Assert.Equal(1, data.Devices.DeviceTypes.Single(s => s.Key == "tablet").Count); // c5
        Assert.Equal(1, data.Devices.DeviceTypes.Single(s => s.Key == "unknown").Count); // c6 (no DeviceType)

        Assert.Equal(2, data.Devices.Browsers.Single(s => s.Key == "Chrome").Count); // c1, c3
        Assert.Equal(1, data.Devices.Browsers.Single(s => s.Key == "Firefox").Count); // c2
        Assert.Equal(1, data.Devices.Browsers.Single(s => s.Key == "Safari").Count); // c4
        Assert.Equal(1, data.Devices.Browsers.Single(s => s.Key == "Edge").Count); // c5
        Assert.Equal(1, data.Devices.Browsers.Single(s => s.Key == "Other").Count); // c6 (no UA)

        // Features
        Assert.Equal(6, data.Features.Total);
        Assert.Equal(1, data.Features.WithScreenshot); // c3
        Assert.Equal(1, data.Features.BugReports); // c3
        Assert.Equal(1, data.Features.WithPredefinedActions); // c3
        Assert.Equal(1, data.Features.Private); // c3
    }

    [Fact]
    public async Task PlatformInsights_NotFixedRate_IsNull_WhenDenominatorIsZero()
    {
        // Fresh DB: only an Open comment with no AppliedAt/VerifiedAt anywhere.
        var db = Guid.NewGuid().ToString();
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var proj = new Project { Key = "p", Name = "P", OwnerId = TenantA, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(proj);
            seed.SaveChanges();
            seed.Comments.Add(new Comment
            {
                ProjectId = proj.Id, OwnerId = TenantA, AuthorId = Guid.NewGuid(), Body = "x",
                Status = CommentStatus.Open, CreatedAt = DateTime.UtcNow, Element = new ElementCapture()
            });
            seed.SaveChanges();
        }

        var service = BuildService(new FakeCurrentUser { IsSuperAdmin = true }, db);
        var result = await service.GetPlatformInsightsAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Data!.Verification.Verified);
        Assert.Equal(0, result.Data!.Verification.NotFixed);
        Assert.Null(result.Data!.Verification.NotFixedRate);
        Assert.Null(result.Data!.Funnel.MedianHoursCreatedToApplied);
        Assert.Null(result.Data!.Funnel.MedianHoursAppliedToVerified);
    }

    [Fact]
    public async Task WorkspaceInsights_NonAdmin_IsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db);
        var service = BuildService(new FakeCurrentUser { TenantId = TenantA }, db);

        var result = await service.GetWorkspaceInsightsAsync();

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task WorkspaceInsights_ScopesToCallersTenant_Only()
    {
        var db = Guid.NewGuid().ToString();
        Seed(db);

        var service = BuildService(new FakeCurrentUser { IsAdmin = true, TenantId = TenantB }, db);
        var result = await service.GetWorkspaceInsightsAsync();

        Assert.True(result.IsSuccess);
        var data = result.Data!;

        // Only tenant B's two comments (c5 Archived, c6 Open/applied-then-reopened) are visible.
        Assert.Equal(1, data.Funnel.Open);
        Assert.Equal(0, data.Funnel.Ready);
        Assert.Equal(0, data.Funnel.Applied);
        Assert.Equal(1, data.Funnel.Archived);
        Assert.Empty(data.Funnel.ByWorkspace); // cross-tenant list stays empty in the workspace view

        Assert.Single(data.ByProject);
        Assert.Equal("proj-b", data.ByProject[0].ProjectKey);

        Assert.Equal(2, data.CommentLanguages.Sum(s => s.Count));

        Assert.Equal(2, data.Devices.DeviceTypes.Sum(s => s.Count));

        Assert.Equal(8, data.Activity.Count);
        var expectedCurrentMonday = MondayOfWeek(DateTime.UtcNow);
        Assert.Equal(expectedCurrentMonday, data.Activity[^1].WeekStart);
        Assert.Equal(2, data.Activity[^1].Created); // c5 + c6, both created "now"
        Assert.Equal(1, data.Activity[^1].Applied); // c6 has AppliedAt "now"+1h, same week
    }

    private static DateOnly MondayOfWeek(DateTime utc)
    {
        var date = DateOnly.FromDateTime(utc);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
