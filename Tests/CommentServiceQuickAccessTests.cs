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
/// A quick-access (Client) account leaves feedback but never manages the project's backlog: it
/// only ever sees its OWN comments (any status), can edit/reply on its own, but can never see,
/// reply to, or change the status of anyone else's comment — including marking one "completed".
/// Every other role keeps seeing the full project backlog, unaffected by these guards.
/// </summary>
public class CommentServiceQuickAccessTests
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

    // Seeds one project owned by `tenant` with two comments: one by `clientId`, one by `otherId`.
    // Returns (projectKey, clientCommentId, otherCommentId).
    private static (string key, int clientCommentId, int otherCommentId) SeedProjectWithTwoComments(
        string dbName, Guid tenant, Guid clientId, Guid otherId)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project { Key = "proj", Name = "Proj", IsActive = true, OwnerId = tenant };
        seed.Projects.Add(project);
        seed.SaveChanges();

        var mine = new Comment
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = clientId, Body = "mine",
            Status = CommentStatus.Open, Environment = EnvironmentTag.Local, Element = new ElementCapture()
        };
        var theirs = new Comment
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = otherId, Body = "theirs",
            Status = CommentStatus.Open, Environment = EnvironmentTag.Local, Element = new ElementCapture()
        };
        seed.Comments.AddRange(mine, theirs);
        seed.SaveChanges();
        return ("proj", mine.Id, theirs.Id);
    }

    [Fact]
    public async Task QuickAccess_ListAsync_OnlySeesOwnComments()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (key, mineId, _) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.ListAsync(key, new CommentFilter(), clientId);

        Assert.True(result.IsSuccess);
        var ids = result.Data!.Items.Select(c => c.Id).ToList();
        Assert.Single(ids);
        Assert.Equal(mineId, ids[0]);
    }

    [Fact]
    public async Task NonQuickAccess_ListAsync_SeesEveryComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var (key, _, _) = SeedProjectWithTwoComments(db, tenant, Guid.NewGuid(), Guid.NewGuid());

        var staff = new FakeCurrentUser { Id = staffId, IsAdmin = true, TenantId = tenant };
        var svc = BuildService(staff, db);

        var result = await svc.ListAsync(key, new CommentFilter(), staffId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.Items.Count);
    }

    [Fact]
    public async Task QuickAccess_GetByIdAsync_CannotSeeOthersComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (_, _, theirsId) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.GetByIdAsync(theirsId, clientId);

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task QuickAccess_GetByIdAsync_CanSeeOwnComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (_, mineId, _) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.GetByIdAsync(mineId, clientId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task QuickAccess_CannotUpdateStatus_EvenOnOwnComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (_, mineId, _) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.UpdateStatusAsync(mineId, new UpdateCommentStatusRequest { Status = CommentStatus.Applied }, clientId);

        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task QuickAccess_CannotReplyToOthersComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (_, _, theirsId) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.AddReplyAsync(theirsId, new AddReplyRequest { Body = "hi" }, clientId);

        Assert.True(result.IsNotFound);
    }

    [Fact]
    public async Task QuickAccess_CanReplyToOwnComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (_, mineId, _) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.AddReplyAsync(mineId, new AddReplyRequest { Body = "hi" }, clientId);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ApplyQueue_ExcludesPrivateComments_EvenForAdmin()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "proj", Name = "Proj", IsActive = true, OwnerId = tenant };
            seed.Projects.Add(project);
            seed.SaveChanges();

            seed.Comments.AddRange(
                new Comment
                {
                    ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "private note",
                    Status = CommentStatus.ReadyToApply, Environment = EnvironmentTag.Local,
                    Element = new ElementCapture(), IsPrivate = true
                },
                new Comment
                {
                    ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "public fix",
                    Status = CommentStatus.ReadyToApply, Environment = EnvironmentTag.Local,
                    Element = new ElementCapture(), IsPrivate = false
                });
            seed.SaveChanges();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, db);

        var result = await svc.ListApplyQueueAsync("proj", new CommentFilter());

        Assert.True(result.IsSuccess);
        Assert.Single(result.Data!.Items);
        Assert.Equal("public fix", result.Data!.Items[0].Body);
    }
}
