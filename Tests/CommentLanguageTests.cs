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
/// Comment.Language: client-detected BCP-47 tag round-trips through create → CommentResponse,
/// the apply-queue (CommentApplyItemDto) and the summary view (CommentSummaryDto); "unknown"/empty
/// normalize to null at write time.
/// </summary>
public class CommentLanguageTests
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

    private static Harness BuildHarness(string dbName)
    {
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Projects.Add(new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant });
            seed.SaveChanges();
        }

        var user = new FakeCurrentUser { Id = author, TenantId = tenant, IsSuperAdmin = false };
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        var commentService = new CommentService(uow, projectService, actionService, new FakeFileStorage(), user, new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());

        return new Harness { Db = db, CommentService = commentService, TenantId = tenant, AuthorId = author };
    }

    private static CreateCommentRequest Req(string body, string? language) => new()
    {
        Body = body,
        Environment = EnvironmentTag.Local,
        Element = new ElementCaptureDto(),
        Language = language
    };

    [Fact]
    public async Task Language_RoundTrips_ToCommentResponse()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var result = await h.CommentService.CreateAsync("proj", Req("مرحبا بكم في هذا الموقع", "ar"), h.AuthorId);
        Assert.True(result.IsSuccess);
        Assert.Equal("ar", result.Data!.Language);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("UNKNOWN")]
    public async Task Language_Empty_Or_Unknown_Stores_Null(string? language)
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var result = await h.CommentService.CreateAsync("proj", Req("hello there", language), h.AuthorId);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.Language);
    }

    [Fact]
    public async Task Language_Is_Normalized_LowercaseTrimmed()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var result = await h.CommentService.CreateAsync("proj", Req("hello there", "  FA  "), h.AuthorId);
        Assert.True(result.IsSuccess);
        Assert.Equal("fa", result.Data!.Language);
    }

    [Fact]
    public async Task Language_RoundTrips_ToApplyQueueItem()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var created = await h.CommentService.CreateAsync("proj", Req("این یک نظر است", "fa"), h.AuthorId);
        Assert.True(created.IsSuccess);

        var queue = await h.CommentService.ListApplyQueueAsync("proj", new CommentFilter());
        Assert.True(queue.IsSuccess);
        var item = Assert.Single(queue.Data!.Items);
        Assert.Equal("fa", item.Language);
    }

    [Fact]
    public async Task Language_RoundTrips_ToSummaryItem()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        var created = await h.CommentService.CreateAsync("proj", Req("これはコメントです", "ja"), h.AuthorId);
        Assert.True(created.IsSuccess);

        var summary = await h.CommentService.ListSummaryAsync("proj", new CommentFilter { View = "summary" }, h.AuthorId);
        Assert.True(summary.IsSuccess);
        var item = Assert.Single(summary.Data!.Items);
        Assert.Equal("ja", item.Language);
    }
}
