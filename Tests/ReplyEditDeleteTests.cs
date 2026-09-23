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
/// A reply can be edited only by its own author (mirrors the comment's own EditAsync policy —
/// not even admins edit someone else's content), but deleted by its author OR a workspace admin
/// (mirrors the comment's own DeleteAsync policy).
/// </summary>
public class ReplyEditDeleteTests
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

    private sealed class FakeFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("uploads/x");

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
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private sealed class FakeCurrentClient(bool isHumanSurface) : ICurrentClient
    {
        public bool IsHumanSurface { get; } = isHumanSurface;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private CommentService BuildService(
        ICurrentUser user,
        string dbName,
        ICurrentClient? currentClient = null
    )
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(
            uow,
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var actionService = new PredefinedActionService(
            uow,
            projectService,
            user,
            new PassThroughEntitlements()
        );
        return new CommentService(
            uow,
            projectService,
            actionService,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            currentClient
        );
    }

    // Seeds one project + one comment (by `authorId`) with one reply (by `replyAuthorId`).
    private static (int commentId, int replyId) SeedCommentWithReply(
        string dbName,
        Guid tenant,
        Guid authorId,
        Guid replyAuthorId,
        bool isAi = false
    )
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project
        {
            Key = "proj",
            Name = "Proj",
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true,
            OwnerId = tenant,
        };
        seed.Projects.Add(project);
        seed.SaveChanges();

        var comment = new Comment
        {
            ProjectId = project.Id,
            OwnerId = tenant,
            AuthorId = authorId,
            Body = "the comment",
            Status = CommentStatus.Open,
            Environment = EnvironmentTag.Local,
            Element = new ElementCapture(),
        };
        seed.Comments.Add(comment);
        seed.SaveChanges();

        var reply = new Reply
        {
            CommentId = comment.Id,
            OwnerId = tenant,
            AuthorId = replyAuthorId,
            Body = "original reply",
            IsAi = isAi,
        };
        seed.Replies.Add(reply);
        seed.SaveChanges();

        return (comment.Id, reply.Id);
    }

    [Fact]
    public async Task Author_CanEdit_OwnReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(db, tenant, Guid.NewGuid(), authorId);

        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db);
        var result = await svc.EditReplyAsync(
            replyId,
            new UpdateReplyRequest { Body = "edited reply" },
            authorId
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("edited reply", result.Data!.Body);
    }

    [Fact]
    public async Task NonAuthor_CannotEdit_SomeoneElsesReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(db, tenant, Guid.NewGuid(), authorId);

        // Even a workspace admin cannot edit someone else's reply — same rule as comments.
        var svc = BuildService(
            new FakeCurrentUser
            {
                Id = otherId,
                IsAdmin = true,
                TenantId = tenant,
            },
            db
        );
        var result = await svc.EditReplyAsync(
            replyId,
            new UpdateReplyRequest { Body = "hijacked" },
            otherId
        );

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task EditReply_NotFound_ReturnsNotFound()
    {
        var db = Guid.NewGuid().ToString();
        var svc = BuildService(
            new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() },
            db
        );

        var result = await svc.EditReplyAsync(
            999_999,
            new UpdateReplyRequest { Body = "x" },
            Guid.NewGuid()
        );

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task Author_CanDelete_OwnReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(db, tenant, Guid.NewGuid(), authorId);

        var svc = BuildService(new FakeCurrentUser { Id = authorId, TenantId = tenant }, db);
        var result = await svc.DeleteReplyAsync(replyId, authorId, isAdmin: false);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Admin_CanDelete_SomeoneElsesReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(db, tenant, Guid.NewGuid(), authorId);

        var svc = BuildService(
            new FakeCurrentUser
            {
                Id = adminId,
                IsAdmin = true,
                TenantId = tenant,
            },
            db
        );
        var result = await svc.DeleteReplyAsync(replyId, adminId, isAdmin: true);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task NonAuthor_NonAdmin_CannotDelete_SomeoneElsesReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(db, tenant, Guid.NewGuid(), authorId);

        var svc = BuildService(new FakeCurrentUser { Id = otherId, TenantId = tenant }, db);
        var result = await svc.DeleteReplyAsync(replyId, otherId, isAdmin: false);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AddReply_FromHumanSurface_IsNotFlaggedAi()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        int commentId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project
            {
                Key = "proj",
                Name = "Proj",
                IsActiveLocal = true,
                IsActiveStaging = true,
                IsActiveProduction = true,
                OwnerId = tenant,
            };
            seed.Projects.Add(project);
            seed.SaveChanges();
            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = authorId,
                Body = "the comment",
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture(),
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
        }

        // Widget/dashboard send X-Pointer-Client — IsHumanSurface true.
        var svc = BuildService(
            new FakeCurrentUser { Id = authorId, TenantId = tenant },
            db,
            new FakeCurrentClient(true)
        );
        var result = await svc.AddReplyAsync(
            commentId,
            new AddReplyRequest { Body = "a human reply" },
            authorId
        );

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.IsAi);
    }

    [Fact]
    public async Task AddReply_FromNonHumanSurface_IsFlaggedAi()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        int commentId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project
            {
                Key = "proj",
                Name = "Proj",
                IsActiveLocal = true,
                IsActiveStaging = true,
                IsActiveProduction = true,
                OwnerId = tenant,
            };
            seed.Projects.Add(project);
            seed.SaveChanges();
            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = authorId,
                Body = "the comment",
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture(),
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
        }

        // The CLI/pointer.sh/skill.md path — no X-Pointer-Client header, IsHumanSurface false.
        var svc = BuildService(
            new FakeCurrentUser { Id = authorId, TenantId = tenant },
            db,
            new FakeCurrentClient(false)
        );
        var result = await svc.AddReplyAsync(
            commentId,
            new AddReplyRequest { Body = "an automated reply" },
            authorId
        );

        Assert.True(result.IsSuccess);
        Assert.True(result.Data!.IsAi);
    }

    [Fact]
    public async Task AiReply_CannotBeEdited_EvenByItsOwnAuthor()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var replyAuthorId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(
            db,
            tenant,
            Guid.NewGuid(),
            replyAuthorId,
            isAi: true
        );

        var svc = BuildService(new FakeCurrentUser { Id = replyAuthorId, TenantId = tenant }, db);
        var result = await svc.EditReplyAsync(
            replyId,
            new UpdateReplyRequest { Body = "hijacked" },
            replyAuthorId
        );

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AiReply_CannotBeDeleted_EvenByAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var replyAuthorId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var (_, replyId) = SeedCommentWithReply(
            db,
            tenant,
            Guid.NewGuid(),
            replyAuthorId,
            isAi: true
        );

        var svc = BuildService(
            new FakeCurrentUser
            {
                Id = adminId,
                IsAdmin = true,
                TenantId = tenant,
            },
            db
        );
        var result = await svc.DeleteReplyAsync(replyId, adminId, isAdmin: true);

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task DeleteReply_NotFound_ReturnsNotFound()
    {
        var db = Guid.NewGuid().ToString();
        var svc = BuildService(
            new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() },
            db
        );

        var result = await svc.DeleteReplyAsync(999_999, Guid.NewGuid(), isAdmin: false);

        Assert.True(result.IsNotFound);
    }
}
