using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Validators;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Structured AI attribution on replies (AiTool/AiModel) — "the ai reply author should include the
/// ai name with the used model beside the user". Mirrors the FakeCurrentClient pattern from
/// ReplyEditDeleteTests.cs.
/// </summary>
public class ReplyAiAttributionTests
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

    private sealed class FakeCurrentClient(bool isHumanSurface) : ICurrentClient
    {
        public bool IsHumanSurface { get; } = isHumanSurface;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private CommentService BuildService(ICurrentUser user, string dbName, ICurrentClient? currentClient = null)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        return new CommentService(uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements(), currentClient);
    }

    private static int SeedComment(string dbName, Guid tenant, Guid authorId)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
        seed.Projects.Add(project);
        seed.SaveChanges();

        var comment = new Comment
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "the comment",
            Status = CommentStatus.Open, Environment = EnvironmentTag.Local, Element = new ElementCapture()
        };
        seed.Comments.Add(comment);
        seed.SaveChanges();
        return comment.Id;
    }

    // ---- AddReplyAsync: AI caller with tool+model -> stored and returned ----

    [Fact]
    public async Task AddReply_FromAiCaller_WithToolAndModel_IsStoredAndReturned()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(false));
        var result = await svc.AddReplyAsync(commentId, new AddReplyRequest
        {
            Body = "Applied ✓",
            AiTool = "Claude-Code",
            AiModel = "claude-sonnet-5"
        }, authorId);

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.IsAi);
        Assert.Equal("claude-code", result.Data!.AiTool);
        Assert.Equal("claude-sonnet-5", result.Data!.AiModel);

        // Also visible on the comment's replies list.
        var getResult = await svc.GetByIdAsync(commentId, authorId);
        Assert.True(getResult.IsSuccess);
        var reply = Assert.Single(getResult.Data!.Replies!);
        Assert.True(reply.IsAi);
        Assert.Equal("claude-code", reply.AiTool);
        Assert.Equal("claude-sonnet-5", reply.AiModel);
    }

    [Fact]
    public async Task AddReply_FromHumanCaller_WithToolAndModel_BothStoredNull()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        // Widget/dashboard send X-Pointer-Client — IsHumanSurface true. A human can't claim to be
        // an AI even if the request body carries tool/model.
        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(true));
        var result = await svc.AddReplyAsync(commentId, new AddReplyRequest
        {
            Body = "a human reply",
            AiTool = "claude-code",
            AiModel = "claude-sonnet-5"
        }, authorId);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.IsAi);
        Assert.Null(result.Data!.AiTool);
        Assert.Null(result.Data!.AiModel);
    }

    // ---- UpdateStatusAsync (the `apply --mark` flow): same rules ----

    [Fact]
    public async Task MarkApplied_FromAiCaller_WithToolAndModel_IsStoredAndReturned()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        // No X-Pointer-Client header — the CLI's `apply --mark` path.
        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(false));
        var result = await svc.UpdateStatusAsync(commentId, new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            Reply = "Applied ✓ — fixed the button",
            AiTool = "claude-code",
            AiModel = "gpt-5.2"
        }, authorId);

        Assert.True(result.IsSuccess);
        var reply = Assert.Single(result.Data!.Replies!);
        Assert.True(reply.IsAi);
        Assert.Equal("claude-code", reply.AiTool);
        Assert.Equal("gpt-5.2", reply.AiModel);
    }

    [Fact]
    public async Task MarkApplied_FromHumanCaller_WithToolAndModel_BothStoredNull()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(true));
        var result = await svc.UpdateStatusAsync(commentId, new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            Reply = "Applied by hand",
            AiTool = "claude-code",
            AiModel = "claude-sonnet-5"
        }, authorId);

        Assert.True(result.IsSuccess);
        var reply = Assert.Single(result.Data!.Replies!);
        Assert.False(reply.IsAi);
        Assert.Null(reply.AiTool);
        Assert.Null(reply.AiModel);
    }

    // ---- Read-only enforcement still holds for AI-attributed replies ----

    [Fact]
    public async Task AiReply_WithAttribution_StillCannotBeEditedOrDeleted()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(false));
        var added = await svc.AddReplyAsync(commentId, new AddReplyRequest
        {
            Body = "Applied ✓",
            AiTool = "claude-code",
            AiModel = "claude-sonnet-5"
        }, authorId);
        Assert.True(added.IsSuccess);
        var replyId = added.Data!.Id;

        var editResult = await svc.EditReplyAsync(replyId, new UpdateReplyRequest { Body = "hijacked" }, authorId);
        Assert.False(editResult.IsSuccess);

        var deleteResult = await svc.DeleteReplyAsync(replyId, authorId, isAdmin: true);
        Assert.False(deleteResult.IsSuccess);
    }

    // ---- Deleting the comment removes/hides its AI reply ----

    [Fact]
    public async Task DeletingComment_HidesItsAiReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var commentId = SeedComment(db, tenant, authorId);

        var aiSvc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(false));
        var added = await aiSvc.AddReplyAsync(commentId, new AddReplyRequest
        {
            Body = "Applied ✓",
            AiTool = "claude-code",
            AiModel = "claude-sonnet-5"
        }, authorId);
        Assert.True(added.IsSuccess);

        // Sanity: visible before delete.
        var before = await aiSvc.GetByIdAsync(commentId, authorId);
        Assert.True(before.IsSuccess);
        Assert.Single(before.Data!.Replies!);

        var humanSvc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db, new FakeCurrentClient(true));
        var deleteResult = await humanSvc.DeleteAsync(commentId, authorId, isAdmin: false);
        Assert.True(deleteResult.IsSuccess);

        // Observable behaviour: the comment (and so its AI reply) is no longer returned.
        var after = await humanSvc.GetByIdAsync(commentId, authorId);
        Assert.True(after.IsNotFound);
    }

    // ---- Validation ----

    [Theory]
    [InlineData("claude-sonnet-5")]
    [InlineData("gpt-5.2")]
    [InlineData("claude-code")]
    [InlineData("gemini-3.8-flash-high")]
    public void AddReplyValidator_accepts_wellFormed_toolAndModel(string value)
    {
        var r = new AddReplyValidator().TestValidate(new AddReplyRequest { Body = "x", AiTool = value, AiModel = value });
        r.ShouldNotHaveValidationErrorFor(x => x.AiTool);
        r.ShouldNotHaveValidationErrorFor(x => x.AiModel);
    }

    [Fact]
    public void AddReplyValidator_rejects_tooLong_toolAndModel()
    {
        var tooLong = new string('a', 65);
        var r = new AddReplyValidator().TestValidate(new AddReplyRequest { Body = "x", AiTool = tooLong, AiModel = tooLong });
        r.ShouldHaveValidationErrorFor(x => x.AiTool);
        r.ShouldHaveValidationErrorFor(x => x.AiModel);
    }

    [Fact]
    public void AddReplyValidator_rejects_spaceInToolAndModel()
    {
        var r = new AddReplyValidator().TestValidate(new AddReplyRequest { Body = "x", AiTool = "claude code", AiModel = "claude code" });
        r.ShouldHaveValidationErrorFor(x => x.AiTool);
        r.ShouldHaveValidationErrorFor(x => x.AiModel);
    }

    [Fact]
    public void UpdateCommentStatusValidator_accepts_wellFormed_toolAndModel()
    {
        var r = new UpdateCommentStatusValidator().TestValidate(new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            AiTool = "claude-code",
            AiModel = "gpt-5.2"
        });
        r.ShouldNotHaveValidationErrorFor(x => x.AiTool);
        r.ShouldNotHaveValidationErrorFor(x => x.AiModel);
    }

    [Fact]
    public void UpdateCommentStatusValidator_rejects_tooLong_and_space()
    {
        var tooLong = new string('a', 65);
        var r = new UpdateCommentStatusValidator().TestValidate(new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            AiTool = tooLong,
            AiModel = "claude code"
        });
        r.ShouldHaveValidationErrorFor(x => x.AiTool);
        r.ShouldHaveValidationErrorFor(x => x.AiModel);
    }
}
