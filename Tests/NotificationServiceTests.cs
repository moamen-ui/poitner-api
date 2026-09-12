using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Notification;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class NotificationServiceTests
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

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static (CommentService commentService, NotificationService notificationService, UnitOfWork uow) BuildServices(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var notificationService = new NotificationService(uow, user);
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        var commentService = new CommentService(
            uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements(),
            null, notificationService);
        return (commentService, notificationService, uow);
    }

    [Fact]
    public async Task AppliedByAnotherUser_CreatesOneRowForAuthor()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var devId = Guid.NewGuid();

        // Seed project and comment
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "test-proj", Name = "Test Project", OwnerId = tenant, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = authorId,
                Body = "Fix header style",
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
        }

        // Developer marks comment applied
        var dev = new FakeCurrentUser { Id = devId, TenantId = tenant, IsAdmin = true };
        var (commentSvc, _, _) = BuildServices(dev, db);

        var updateReq = new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            CommitUrl = "https://github.com/repo/commit/abc1234",
            AppliedByLabel = "Dev Dave"
        };
        var updateRes = await commentSvc.UpdateStatusAsync(1, updateReq, devId);
        Assert.True(updateRes.IsSuccess);

        // Check author notifications
        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant };
        var (_, notifSvc, _) = BuildServices(author, db);

        var listRes = await notifSvc.ListAsync(unread: true);
        Assert.True(listRes.IsSuccess);
        Assert.Single(listRes.Data!.Items);

        var notif = listRes.Data.Items[0];
        Assert.Equal(NotificationType.CommentApplied, notif.Type);
        Assert.Equal(1, notif.CommentId);
        Assert.Equal("Fix header style", notif.CommentBodyExcerpt);
        Assert.NotNull(notif.Payload);
        Assert.Equal("https://github.com/repo/commit/abc1234", notif.Payload.CommitUrl);
        Assert.Equal("Dev Dave", notif.Payload.AppliedByLabel);

        var countRes = await notifSvc.GetUnreadCountAsync();
        Assert.True(countRes.IsSuccess);
        Assert.Equal(1, countRes.Data!.Count);
    }

    [Fact]
    public async Task AppliedByAuthor_CreatesNoNotification()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        // Seed project and comment
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "test-proj", Name = "Test Project", OwnerId = tenant, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = authorId,
                Body = "Self comment",
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
        }

        // Author marks comment applied
        var author = new FakeCurrentUser { Id = authorId, TenantId = tenant, IsAdmin = true };
        var (commentSvc, notifSvc, _) = BuildServices(author, db);

        var updateReq = new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            CommitUrl = "https://github.com/repo/commit/abc1234",
            AppliedByLabel = "Self"
        };
        var updateRes = await commentSvc.UpdateStatusAsync(1, updateReq, authorId);
        Assert.True(updateRes.IsSuccess);

        // Should have 0 notifications
        var listRes = await notifSvc.ListAsync(unread: true);
        Assert.True(listRes.IsSuccess);
        Assert.Empty(listRes.Data!.Items);

        var countRes = await notifSvc.GetUnreadCountAsync();
        Assert.Equal(0, countRes.Data!.Count);
    }

    [Fact]
    public async Task TenantIsolation_UserInTenantBSeesNone()
    {
        var db = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        // Seed notification in tenant A
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var projectA = new Project { Key = "proj-a", Name = "Project A", OwnerId = tenantA, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(projectA);
            seed.SaveChanges();

            var commentA = new Comment
            {
                ProjectId = projectA.Id,
                OwnerId = tenantA,
                AuthorId = userA,
                Body = "Comment A",
                Status = CommentStatus.Applied,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            seed.Comments.Add(commentA);
            seed.SaveChanges();

            var notifA = new Notification
            {
                OwnerId = tenantA,
                UserId = userA,
                Type = NotificationType.CommentApplied,
                CommentId = commentA.Id,
                ProjectId = projectA.Id,
                Payload = JsonSerializer.Serialize(new NotificationPayloadDto { CommitUrl = "https://commit" }),
                CreatedAt = DateTime.UtcNow
            };
            seed.Notifications.Add(notifA);
            seed.SaveChanges();
        }

        // User B in Tenant B tries to query
        var userBContext = new FakeCurrentUser { Id = userB, TenantId = tenantB };
        var (_, notifSvcB, _) = BuildServices(userBContext, db);

        var listRes = await notifSvcB.ListAsync();
        Assert.True(listRes.IsSuccess);
        Assert.Empty(listRes.Data!.Items);

        var countRes = await notifSvcB.GetUnreadCountAsync();
        Assert.Equal(0, countRes.Data!.Count);
    }

    [Fact]
    public async Task OtherUsersNotificationsNotVisible_TwoUsersInSameTenant()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();

        int notifBId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "proj", Name = "Project", OwnerId = tenant, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var commentA = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = userA,
                Body = "Comment A",
                Status = CommentStatus.Applied,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            var commentB = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = userB,
                Body = "Comment B",
                Status = CommentStatus.Applied,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            seed.Comments.AddRange(commentA, commentB);
            seed.SaveChanges();

            var notifA = new Notification
            {
                OwnerId = tenant,
                UserId = userA,
                Type = NotificationType.CommentApplied,
                CommentId = commentA.Id,
                ProjectId = project.Id,
                CreatedAt = DateTime.UtcNow
            };
            var notifB = new Notification
            {
                OwnerId = tenant,
                UserId = userB,
                Type = NotificationType.CommentApplied,
                CommentId = commentB.Id,
                ProjectId = project.Id,
                CreatedAt = DateTime.UtcNow
            };
            seed.Notifications.AddRange(notifA, notifB);
            seed.SaveChanges();
            notifBId = notifB.Id;
        }

        var userACtx = new FakeCurrentUser { Id = userA, TenantId = tenant };
        var (_, notifSvcA, _) = BuildServices(userACtx, db);

        // User A's list never includes B's rows
        var listA = await notifSvcA.ListAsync();
        Assert.True(listA.IsSuccess);
        Assert.Single(listA.Data!.Items);
        Assert.DoesNotContain(listA.Data.Items, n => n.Id == notifBId);

        // User A's unread count does not include B
        var countA = await notifSvcA.GetUnreadCountAsync();
        Assert.Equal(1, countA.Data!.Count);

        // MarkRead on B's id returns 404 (not 403, avoiding existence leaks)
        var markRes = await notifSvcA.MarkReadAsync(notifBId);
        Assert.True(markRes.IsNotFound);

        // MarkAllRead by User A marks only User A's rows
        var readAllRes = await notifSvcA.MarkAllReadAsync();
        Assert.True(readAllRes.IsSuccess);
        Assert.Equal(1, readAllRes.Data!.Marked);

        // Verify B's notification is still unread
        var userBCtx = new FakeCurrentUser { Id = userB, TenantId = tenant };
        var (_, notifSvcB, _) = BuildServices(userBCtx, db);
        var countB = await notifSvcB.GetUnreadCountAsync();
        Assert.Equal(1, countB.Data!.Count);
    }

    [Fact]
    public async Task UnreadCount_And_ReadAll_Workflow()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        int n1Id, n2Id;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "proj", Name = "Project", OwnerId = tenant, IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true };
            seed.Projects.Add(project);
            seed.SaveChanges();

            var comment = new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = user,
                Body = "Comment",
                Status = CommentStatus.Applied,
                Environment = EnvironmentTag.Local,
                Element = new ElementCapture()
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();

            var n1 = new Notification { OwnerId = tenant, UserId = user, Type = NotificationType.CommentApplied, CommentId = comment.Id, ProjectId = project.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-2) };
            var n2 = new Notification { OwnerId = tenant, UserId = user, Type = NotificationType.ReplyAdded, CommentId = comment.Id, ProjectId = project.Id, CreatedAt = DateTime.UtcNow.AddMinutes(-1) };
            seed.Notifications.AddRange(n1, n2);
            seed.SaveChanges();
            n1Id = n1.Id;
            n2Id = n2.Id;
        }

        var userCtx = new FakeCurrentUser { Id = user, TenantId = tenant };
        var (_, notifSvc, _) = BuildServices(userCtx, db);

        // Initially 2 unread
        var count1 = await notifSvc.GetUnreadCountAsync();
        Assert.Equal(2, count1.Data!.Count);

        // Mark one read
        var markRes = await notifSvc.MarkReadAsync(n1Id);
        Assert.True(markRes.IsSuccess);
        Assert.NotNull(markRes.Data!.ReadAt);

        // Now 1 unread
        var count2 = await notifSvc.GetUnreadCountAsync();
        Assert.Equal(1, count2.Data!.Count);

        // MarkAllRead marks remaining 1
        var readAll = await notifSvc.MarkAllReadAsync();
        Assert.True(readAll.IsSuccess);
        Assert.Equal(1, readAll.Data!.Marked);

        // Now 0 unread
        var count3 = await notifSvc.GetUnreadCountAsync();
        Assert.Equal(0, count3.Data!.Count);
    }
}
