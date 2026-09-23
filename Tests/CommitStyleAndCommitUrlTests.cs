using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Project;
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
/// Comment.CommitUrl (set via PATCH /api/comments/{id} alongside AppliedByLabel) and
/// Project.CommitStyle (the one-commit-vs-separate-commits setting skill.md's apply flow reads,
/// surfaced to the widget via CaptureConfigResponse alongside the new CanEditSettings gate).
/// </summary>
public class CommitStyleAndCommitUrlTests
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
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static CommentService BuildCommentService(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        return new CommentService(uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());
    }

    [Fact]
    public async Task UpdateStatusAsync_Applied_StoresCommitUrl()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        int commentId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
            seed.Projects.Add(project);
            seed.SaveChanges();
            var comment = new Comment
            {
                ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "fix this",
                Status = CommentStatus.ReadyToApply, Environment = EnvironmentTag.Local, Element = new ElementCapture(),
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildCommentService(admin, dbName);

        var result = await svc.UpdateStatusAsync(commentId, new UpdateCommentStatusRequest
        {
            Status = CommentStatus.Applied,
            AppliedByLabel = "dev@example.com",
            CommitUrl = "https://github.com/acme/app/commit/abc123",
        }, admin.Id!.Value);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://github.com/acme/app/commit/abc123", result.Data!.CommitUrl);
    }

    [Fact]
    public async Task UpdateStatusAsync_NonAppliedStatus_DoesNotStoreCommitUrl()
    {
        // CommitUrl (like AppliedByLabel) is only ever meaningful alongside Applied — a request
        // that sets it but targets a different status must not persist it, matching AppliedAt/
        // AppliedBy's own existing "only on Applied" gate (CommentService.cs ~497-502).
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        int commentId;
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
            seed.Projects.Add(project);
            seed.SaveChanges();
            var comment = new Comment
            {
                ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "fix this",
                Status = CommentStatus.Open, Environment = EnvironmentTag.Local, Element = new ElementCapture(),
            };
            seed.Comments.Add(comment);
            seed.SaveChanges();
            commentId = comment.Id;
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildCommentService(admin, dbName);

        var result = await svc.UpdateStatusAsync(commentId, new UpdateCommentStatusRequest
        {
            Status = CommentStatus.ReadyToApply,
            CommitUrl = "https://github.com/acme/app/commit/abc123",
        }, admin.Id!.Value);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.CommitUrl);
    }

    [Fact]
    public async Task Project_CommitStyle_DefaultsToSingle()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());

        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        Assert.Equal(CommitStyle.Single, created.CommitStyle);
    }

    [Fact]
    public async Task UpdateAsync_SetsCommitStyle_SurfacedOnProjectResponseAndCaptureConfig()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        var updated = await svc.UpdateAsync(created.Id, new UpdateProjectRequest { CommitStyle = Domain.Enums.CommitStyle.Separate });
        Assert.True(updated.IsSuccess);
        Assert.Equal(CommitStyle.Separate, updated.Data!.CommitStyle);

        var config = await svc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.Equal(CommitStyle.Separate, config.Data!.CommitStyle);
    }

    [Fact]
    public async Task UpdateAsync_NullCommitStyle_LeavesItUntouched()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var created = (await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" })).Data!;

        await svc.UpdateAsync(created.Id, new UpdateProjectRequest { CommitStyle = CommitStyle.Separate });
        var afterUnrelatedPatch = await svc.UpdateAsync(created.Id, new UpdateProjectRequest { Name = "Renamed" });

        Assert.True(afterUnrelatedPatch.IsSuccess);
        Assert.Equal(CommitStyle.Separate, afterUnrelatedPatch.Data!.CommitStyle);
    }

    [Fact]
    public async Task CaptureConfig_CanEditSettings_TrueForAdmin_FalseForOtherStakeholder()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(admin, dbName)), admin, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var adminConfig = await svc.GetCaptureConfigAsync("site");
        Assert.True(adminConfig.Data!.CanEditSettings);

        // A different, non-admin, non-creator stakeholder in the same tenant must not be able to
        // edit — same gate as ProjectService.UpdateAsync's Forbidden check.
        var developer = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = tenant };
        var developerSvc = new ProjectService(new UnitOfWork(BuildContext(developer, dbName)), developer, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var developerConfig = await developerSvc.GetCaptureConfigAsync("site");
        Assert.False(developerConfig.Data!.CanEditSettings);
    }

    [Fact]
    public async Task CaptureConfig_CanEditSettings_TrueForProjectCreator()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var creatorId = Guid.NewGuid();
        var creator = new FakeCurrentUser { Id = creatorId, TenantId = tenant };
        var svc = new ProjectService(new UnitOfWork(BuildContext(creator, dbName)), creator, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        await svc.CreateAsync(new CreateProjectRequest { Key = "site", Name = "Site" });

        var config = await svc.GetCaptureConfigAsync("site");
        Assert.True(config.IsSuccess);
        Assert.True(config.Data!.CanEditSettings);
    }
}
