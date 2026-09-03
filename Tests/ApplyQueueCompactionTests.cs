using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The apply-queue's compaction mapping (docs' "Compact apply-queue JSON" plan): per-comment
/// Element fields are parsed server-side (no more JSON-strings-inside-JSON), and page/UA metadata
/// is deduped into PagedData.Pages/UserAgents, keyed by `route + deviceType` — not route alone, so
/// two comments on the same route from different devices never collide.
/// </summary>
public class ApplyQueueCompactionTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
    }

    private sealed class FakeFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension) => Task.FromResult("uploads/x");
        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;
        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class FakeUploadSigner : IUploadSigner
    {
        public string SignedUrl(string relPath) => relPath;
        public bool Validate(string relPath, long exp, string sig) => true;
        public string ExtractRelPath(string stored) => stored;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private CommentService BuildService(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        return new CommentService(uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());
    }

    private static (string dbName, string projectKey, Guid tenant) SeedProject(string dbName)
    {
        var tenant = Guid.NewGuid();
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        seed.Projects.Add(new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant });
        seed.SaveChanges();
        return (dbName, "proj", tenant);
    }

    private static Comment MakeComment(int projectId, Guid tenant, Guid author, string route, string device, string? ua, CommentStatus status = CommentStatus.ReadyToApply) => new()
    {
        ProjectId = projectId,
        OwnerId = tenant,
        AuthorId = author,
        Body = "body",
        Status = status,
        Environment = EnvironmentTag.Local,
        Element = new ElementCapture { Route = route, DeviceType = device, UserAgent = ua, PageUrl = $"https://x.test{route}", PageTitle = "t" }
    };

    [Fact]
    public async Task ApplyQueue_DedupesPagesByRoutePlusDevice_NotRouteAlone()
    {
        var db = Guid.NewGuid().ToString();
        var (dbName, key, tenant) = SeedProject(db);
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = await seed.Projects.FirstAsync();
            seed.Comments.AddRange(
                MakeComment(project.Id, tenant, author, "/checkout", "mobile", "UA-mobile"),
                MakeComment(project.Id, tenant, author, "/checkout", "mobile", "UA-mobile"), // same route+device → same page entry
                MakeComment(project.Id, tenant, author, "/checkout", "desktop", "UA-desktop")); // same route, different device → separate entry
            await seed.SaveChangesAsync();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, dbName);

        var result = await svc.ListApplyQueueAsync(key, new Application.DTOs.Comment.CommentFilter());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data!.Pages);
        Assert.Equal(2, result.Data!.Pages!.Count); // mobile x1, desktop x1 — not 3 (no accidental collision) and not 1 (device distinguishes)

        var items = result.Data!.Items;
        Assert.Equal(3, items.Count);
        var mobileRefs = items.Where(i => i.Element.PageRef != null && result.Data!.Pages![i.Element.PageRef!].Device == "mobile").Select(i => i.Element.PageRef).Distinct().ToList();
        Assert.Single(mobileRefs); // both mobile comments share one PageRef
    }

    [Fact]
    public async Task ApplyQueue_DedupesUserAgentsAcrossPages()
    {
        var db = Guid.NewGuid().ToString();
        var (dbName, key, tenant) = SeedProject(db);
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = await seed.Projects.FirstAsync();
            seed.Comments.AddRange(
                MakeComment(project.Id, tenant, author, "/a", "mobile", "same-ua"),
                MakeComment(project.Id, tenant, author, "/b", "mobile", "same-ua")); // different route, same UA → one UserAgents entry
            await seed.SaveChangesAsync();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, dbName);

        var result = await svc.ListApplyQueueAsync(key, new Application.DTOs.Comment.CommentFilter());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.Pages!.Count); // two distinct routes
        Assert.Single(result.Data!.UserAgents!); // but one shared UA
        Assert.Equal("same-ua", result.Data!.UserAgents!.Values.Single());
    }

    [Fact]
    public async Task ApplyQueue_ParsesStringifiedElementFields_IntoRealJson()
    {
        var db = Guid.NewGuid().ToString();
        var (dbName, key, tenant) = SeedProject(db);
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = await seed.Projects.FirstAsync();
            var comment = MakeComment(project.Id, tenant, author, "/checkout", "mobile", "ua");
            comment.Element.Classes = "[\"btn\",\"btn-primary\"]";
            comment.Element.ComputedStyles = "{\"color\":\"red\"}";
            comment.Element.AppliedCssRules = "[]";
            comment.Element.ParentInfo = "{\"tag\":\"main\",\"id\":null,\"classes\":[]}";
            seed.Comments.Add(comment);
            await seed.SaveChangesAsync();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, dbName);

        var result = await svc.ListApplyQueueAsync(key, new Application.DTOs.Comment.CommentFilter());
        var element = result.Data!.Items.Single().Element;

        Assert.Equal(JsonValueKind.Array, element.Classes!.Value.ValueKind);
        Assert.Equal(2, element.Classes!.Value.GetArrayLength());
        Assert.Equal(JsonValueKind.Object, element.ComputedStyles!.Value.ValueKind);
        Assert.Equal("red", element.ComputedStyles!.Value.GetProperty("color").GetString());
        Assert.Equal(JsonValueKind.Object, element.Parent!.Value.ValueKind);
        Assert.Equal("main", element.Parent!.Value.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task ApplyQueue_MalformedStringifiedField_FallsBackToRawStringInsteadOfBreaking()
    {
        var db = Guid.NewGuid().ToString();
        var (dbName, key, tenant) = SeedProject(db);
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = await seed.Projects.FirstAsync();
            var comment = MakeComment(project.Id, tenant, author, "/checkout", "mobile", "ua");
            comment.Element.ComputedStyles = "{not valid json"; // simulates an old/malformed row
            seed.Comments.Add(comment);
            await seed.SaveChangesAsync();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, dbName);

        var result = await svc.ListApplyQueueAsync(key, new Application.DTOs.Comment.CommentFilter());

        Assert.True(result.IsSuccess); // never a 500/failure on malformed legacy data
        var element = result.Data!.Items.Single().Element;
        Assert.Equal(JsonValueKind.String, element.ComputedStyles!.Value.ValueKind);
        Assert.Equal("{not valid json", element.ComputedStyles!.Value.GetString());
    }
}
