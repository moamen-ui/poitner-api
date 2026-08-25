using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Page-context (console/network) capture: gated by BOTH the "Report as a bug" checkbox
/// (IsBugReport) AND the project's PageContextCaptureEnabled toggle — server-side, regardless of
/// what the client sends. Comments on the same (project, route, environment, session) share one
/// PageContextSnapshot instead of each getting a copy.
/// See docs/superpowers/specs/2026-08-25-page-context-capture-design.md.
/// </summary>
public class PageContextCaptureTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
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
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private sealed class Harness
    {
        public required AppDbContext Db { get; init; }
        public required CommentService CommentService { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid AuthorId { get; init; }
    }

    // Builds a tenant + a project (capture toggle per captureEnabled) and a CommentService wired to
    // that tenant's context.
    private static Harness BuildHarness(string dbName, bool captureEnabled)
    {
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Projects.Add(new Project
            {
                Key = "proj", Name = "Proj", IsActive = true, OwnerId = tenant,
                PageContextCaptureEnabled = captureEnabled
            });
            seed.SaveChanges();
        }

        var user = new FakeCurrentUser { Id = author, TenantId = tenant, IsSuperAdmin = false };
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        var commentService = new CommentService(uow, projectService, actionService, new FakeFileStorage(), user, new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());

        return new Harness { Db = db, CommentService = commentService, TenantId = tenant, AuthorId = author };
    }

    private static PageContextCaptureDto SampleCapture(string sessionId = "sess-1") => new()
    {
        SessionId = sessionId,
        ConsoleEntries = new List<ConsoleEntryInputDto>
        {
            new() { Level = "error", Message = "TypeError: x is undefined", Count = 1 }
        },
        NetworkEntries = new List<NetworkEntryInputDto>
        {
            new() { Method = "POST", Url = "https://api.example.com/checkout", StatusCode = 500, DurationMs = 800 }
        }
    };

    private static CreateCommentRequest Req(bool isBugReport, PageContextCaptureDto? pageContext, string route = "/checkout") => new()
    {
        Body = "It crashed",
        Environment = EnvironmentTag.Production,
        IsBugReport = isBugReport,
        PageContext = pageContext,
        Element = new ElementCaptureDto { Route = route }
    };

    [Fact]
    public async Task Create_BugReportWithCaptureEnabled_CreatesSnapshot_AndReturnsItEmbedded()
    {
        var h = BuildHarness(Guid.NewGuid().ToString(), captureEnabled: true);

        var result = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture()), h.AuthorId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.IsBugReport);
        Assert.NotNull(result.Data.PageContext);
        Assert.Equal("/checkout", result.Data.PageContext!.Route);
        Assert.Single(result.Data.PageContext.ConsoleEntries);
        Assert.Single(result.Data.PageContext.NetworkEntries);
        Assert.Equal(500, result.Data.PageContext.NetworkEntries[0].StatusCode);
    }

    [Fact]
    public async Task Create_BugReportButCaptureDisabled_IgnoresPageContext()
    {
        var h = BuildHarness(Guid.NewGuid().ToString(), captureEnabled: false);

        var result = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture()), h.AuthorId);

        Assert.True(result.IsSuccess);
        // IsBugReport is still stamped (cheap triage signal on its own)...
        Assert.True(result.Data!.IsBugReport);
        // ...but no PageContextSnapshot is ever created when the project hasn't opted in, regardless
        // of what the client sent.
        Assert.Null(result.Data.PageContext);
        Assert.Empty(h.Db.PageContextSnapshots.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Create_NotFlaggedAsBug_IgnoresPageContext_EvenIfCaptureEnabledAndDataSent()
    {
        var h = BuildHarness(Guid.NewGuid().ToString(), captureEnabled: true);

        var result = await h.CommentService.CreateAsync("proj", Req(false, SampleCapture()), h.AuthorId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.IsBugReport);
        Assert.Null(result.Data.PageContext);
        Assert.Empty(h.Db.PageContextSnapshots.IgnoreQueryFilters());
    }

    [Fact]
    public async Task Create_TwoBugReports_SamePageAndSession_ShareOnePageContextSnapshot()
    {
        var h = BuildHarness(Guid.NewGuid().ToString(), captureEnabled: true);

        // Two comments, different query strings on the same path, same session — should dedup to
        // ONE PageContextSnapshot (route is normalized to path-only).
        var first = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture(), "/checkout?step=1"), h.AuthorId);
        var second = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture(), "/checkout?step=2"), h.AuthorId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Data!.PageContext);
        Assert.NotNull(second.Data!.PageContext);
        Assert.Equal(first.Data.PageContext!.Id, second.Data.PageContext!.Id);
        Assert.Single(h.Db.PageContextSnapshots.IgnoreQueryFilters());

        // Entries from both submissions accumulated onto the one shared snapshot.
        var snapshot = h.Db.PageContextSnapshots.IgnoreQueryFilters().Single();
        Assert.Equal(2, snapshot.ConsoleEntries.Count);
        Assert.Equal(2, snapshot.NetworkEntries.Count);

        // The list endpoint references it by id rather than duplicating it per comment.
        var list = await h.CommentService.ListAsync("proj", new CommentFilter(), h.AuthorId);
        Assert.True(list.IsSuccess);
        Assert.NotNull(list.Data!.PageContexts);
        Assert.Single(list.Data.PageContexts!);
        Assert.All(list.Data.Items, item => Assert.Equal(snapshot.Id, item.PageContextId));
    }

    [Fact]
    public async Task Create_TwoBugReports_DifferentSessions_GetSeparateSnapshots()
    {
        var h = BuildHarness(Guid.NewGuid().ToString(), captureEnabled: true);

        var first = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture("sess-a")), h.AuthorId);
        var second = await h.CommentService.CreateAsync("proj", Req(true, SampleCapture("sess-b")), h.AuthorId);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotEqual(first.Data!.PageContext!.Id, second.Data!.PageContext!.Id);
        Assert.Equal(2, h.Db.PageContextSnapshots.IgnoreQueryFilters().Count());
    }
}
