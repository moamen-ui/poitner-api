using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Notification;
using Pointer.Application.Resources;
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

public class CommentVerifyTests
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
        /// <summary>DB-16: recorded so Edit_RemoveScreenshot_* tests can assert what (if anything)
        /// was actually deleted.</summary>
        public List<string> Deleted { get; } = new();

        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("uploads/x");

        public Task DeleteAsync(string relativePathOrUrl)
        {
            Deleted.Add(relativePathOrUrl);
            return Task.CompletedTask;
        }

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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static (
        CommentService commentService,
        NotificationService notificationService,
        UnitOfWork uow
    ) BuildServices(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var notificationService = new NotificationService(uow, user);
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
        var commentService = new CommentService(
            uow,
            projectService,
            actionService,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            null,
            notificationService
        );
        return (commentService, notificationService, uow);
    }

    /// <summary>DB-16: same wiring as <see cref="BuildServices"/> but also hands back the
    /// FakeFileStorage instance so a test can assert what got deleted (or didn't).</summary>
    private static (CommentService commentService, FakeFileStorage fileStorage) BuildServicesWithStorage(
        ICurrentUser user,
        string dbName
    )
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var notificationService = new NotificationService(uow, user);
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
        var fileStorage = new FakeFileStorage();
        var commentService = new CommentService(
            uow,
            projectService,
            actionService,
            fileStorage,
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            null,
            notificationService
        );
        return (commentService, fileStorage);
    }

    private static int SeedCommentWithScreenshot(
        string dbName,
        Guid tenant,
        Guid authorId,
        string? screenshotUrl
    )
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project
        {
            Key = "proj",
            Name = "Proj",
            OwnerId = tenant,
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true,
        };
        seed.Projects.Add(project);
        seed.SaveChanges();

        var comment = new Comment
        {
            ProjectId = project.Id,
            OwnerId = tenant,
            AuthorId = authorId,
            Body = "hi",
            Status = CommentStatus.Open,
            Environment = EnvironmentTag.Local,
            Element = new ElementCapture { ScreenshotUrl = screenshotUrl },
        };
        seed.Comments.Add(comment);
        seed.SaveChanges();
        return comment.Id;
    }

    // ── DB-16: ownership-checked single-file delete (EditAsync "remove screenshot") ─────────

    [Fact]
    public async Task Edit_RemoveScreenshot_PassesOwnedRelPathToStorage()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var rel = $"uploads/{tenant:N}/proj/{Guid.NewGuid():N}.png";
        var commentId = SeedCommentWithScreenshot(db, tenant, authorId, rel);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, fileStorage) = BuildServicesWithStorage(author, db);

        var req = new EditCommentRequest { Body = "hi", RemoveScreenshot = true };
        var res = await commentSvc.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Equal(new[] { rel }, fileStorage.Deleted);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
    }

    [Fact]
    public async Task Edit_RemoveScreenshot_ForeignPath_NullsUrl_DeletesNothing()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var foreignOwner = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var rel = $"uploads/{foreignOwner:N}/proj/{Guid.NewGuid():N}.png";
        var commentId = SeedCommentWithScreenshot(db, tenant, authorId, rel);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, fileStorage) = BuildServicesWithStorage(author, db);

        var req = new EditCommentRequest { Body = "hi", RemoveScreenshot = true };
        var res = await commentSvc.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Empty(fileStorage.Deleted);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
    }

    [Fact]
    public async Task Delete_SoftDeletes_TouchesNoFile()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var rel = $"uploads/{tenant:N}/proj/{Guid.NewGuid():N}.png";
        var commentId = SeedCommentWithScreenshot(db, tenant, authorId, rel);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, fileStorage) = BuildServicesWithStorage(author, db);

        var res = await commentSvc.DeleteAsync(commentId, authorId, isAdmin: false);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Empty(fileStorage.Deleted);
    }

    private static int SeedAppliedComment(string dbName, Guid tenant, Guid authorId, Guid appliedBy)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project
        {
            Key = "proj",
            Name = "Proj",
            OwnerId = tenant,
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true,
        };
        seed.Projects.Add(project);
        var role = new Role
        {
            Name = "Member",
            OwnerId = tenant,
            IsActive = true,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();

        // DB-11a: EnqueueAsync only queues a notification for a recipient with a live, active,
        // Approved membership — appliedBy needs a real identity + membership to receive one.
        var appliedByUser = new User
        {
            PublicId = appliedBy,
            Email = "dev@x.com",
            PasswordHash = "x",
            DisplayName = "Dev",
            RoleId = role.Id,
            OwnerId = tenant,
            IsActive = true,
        };
        seed.Users.Add(appliedByUser);
        seed.SaveChanges();
        TestSeed.Join(seed, appliedByUser, tenant, role);

        var comment = new Comment
        {
            ProjectId = project.Id,
            OwnerId = tenant,
            AuthorId = authorId,
            Body = "Button should be green",
            Status = CommentStatus.Applied,
            AppliedAt = DateTime.UtcNow.AddHours(-1),
            AppliedBy = appliedBy,
            AppliedByLabel = "Dev Dave",
            CommitUrl = "https://github.com/repo/commit/123",
            Environment = EnvironmentTag.Local,
            Element = new ElementCapture(),
        };
        seed.Comments.Add(comment);
        seed.SaveChanges();
        return comment.Id;
    }

    [Fact]
    public async Task Author_ThumbsUp_SetsVerifiedAt_AndAddsReply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        var commentId = SeedAppliedComment(db, tenant, authorId, devId);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, _, _) = BuildServices(author, db);

        var req = new VerifyCommentRequest { Ok = true };
        var res = await commentSvc.VerifyAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Data!.VerifiedAt);
        Assert.Equal(CommentStatus.Applied, res.Data.Status);
        Assert.Single(res.Data.Replies);
        Assert.Equal("Verified ✓", res.Data.Replies[0].Body);
    }

    [Fact]
    public async Task Author_ThumbsUp_WithCustomNote_UsesNote()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        var commentId = SeedAppliedComment(db, tenant, authorId, devId);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, _, _) = BuildServices(author, db);

        var req = new VerifyCommentRequest { Ok = true, Note = "Looks great now, thanks!" };
        var res = await commentSvc.VerifyAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Data!.VerifiedAt);
        Assert.Single(res.Data.Replies);
        Assert.Equal("Looks great now, thanks!", res.Data.Replies[0].Body);
    }

    [Fact]
    public void Validator_ThumbsDown_WithoutNote_FailsValidation()
    {
        var validator = new VerifyCommentValidator();
        var req = new VerifyCommentRequest { Ok = false, Note = "" };
        var validationResult = validator.Validate(req);

        Assert.False(validationResult.IsValid);
        Assert.Contains(
            validationResult.Errors,
            e => e.ErrorMessage == MessageKeys.Comment.VerifyNoteRequired
        );
    }

    [Fact]
    public async Task Author_ThumbsDown_WithNote_ReopensComment_AndEmitsNotificationToAppliedBy()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        var commentId = SeedAppliedComment(db, tenant, authorId, devId);

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, _, _) = BuildServices(author, db);

        var req = new VerifyCommentRequest { Ok = false, Note = "still red on mobile Safari" };
        var res = await commentSvc.VerifyAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess);
        Assert.Equal(CommentStatus.Open, res.Data!.Status);
        Assert.Null(res.Data.VerifiedAt);
        Assert.Single(res.Data.Replies);
        Assert.Equal("Not fixed: still red on mobile Safari", res.Data.Replies[0].Body);

        // Check that devId received CommentReopened notification
        var dev = new FakeCurrentUser { Id = devId, TenantId = tenant };
        var (_, notifSvc, _) = BuildServices(dev, db);
        var notifs = await notifSvc.ListAsync(unread: true);

        Assert.True(notifs.IsSuccess);
        Assert.Single(notifs.Data!.Items);
        var notif = notifs.Data.Items[0];
        Assert.Equal(NotificationType.CommentReopened, notif.Type);
        Assert.Equal(commentId, notif.CommentId);
        Assert.NotNull(notif.Payload);
        Assert.Equal("still red on mobile Safari", notif.Payload.ReplyExcerpt);
    }

    [Fact]
    public async Task NonAuthor_NonAdmin_ReturnsForbidden()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        var bystanderId = Guid.NewGuid();
        var commentId = SeedAppliedComment(db, tenant, authorId, devId);

        var bystander = new FakeCurrentUser
        {
            Id = bystanderId,
            TenantId = tenant,
            IsAdmin = false,
        };
        var (commentSvc, _, _) = BuildServices(bystander, db);

        var req = new VerifyCommentRequest { Ok = true };
        var res = await commentSvc.VerifyAsync(commentId, req, bystanderId);

        Assert.True(res.IsForbidden);
    }

    [Fact]
    public async Task QuickAccess_Author_IsAllowedToVerify()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();
        var commentId = SeedAppliedComment(db, tenant, authorId, devId);

        // Quick-access client who IS the author
        var clientAuthor = new FakeCurrentUser
        {
            Id = authorId,
            TenantId = tenant,
            IsQuickAccess = true,
        };
        var (commentSvc, _, _) = BuildServices(clientAuthor, db);

        var req = new VerifyCommentRequest { Ok = true };
        var res = await commentSvc.VerifyAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess);
        Assert.NotNull(res.Data!.VerifiedAt);
    }

    [Fact]
    public async Task Verify_OnNonAppliedComment_ReturnsFailure()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        int openCommentId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = tenant,
                IsActiveLocal = true,
                IsActiveStaging = true,
                IsActiveProduction = true,
            };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = authorId,
                Body = "Open comment",
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture(),
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            openCommentId = comment.Id;
        }

        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (commentSvc, _, _) = BuildServices(author, db);

        var req = new VerifyCommentRequest { Ok = true };
        var res = await commentSvc.VerifyAsync(openCommentId, req, authorId);

        Assert.False(res.IsSuccess);
        Assert.Equal(MessageKeys.Comment.VerifyRequiresApplied, res.Message);
    }
}
