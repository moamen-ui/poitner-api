using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Super admins are platform-management only — they must never self-own tenant-scoped resources
/// (Projects, Comments, tenant-wide PredefinedActions). This is a deliberate policy restriction
/// (not a bug fix): the recurring "owner_id" bug class came from every place independently
/// re-deciding what tenant a super admin's own action belongs to, so the fix removes the code
/// path entirely rather than patching each resolution site. A super admin who wants to use the
/// product signs in with a real tenant account instead.
/// </summary>
public class SuperAdminUsageBlockedTests
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
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options, user, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    [Fact]
    public async Task SuperAdmin_CannotCreateProject()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var uow = new UnitOfWork(BuildContext(superAdmin, db));
        var svc = new ProjectService(uow, superAdmin, new PassThroughEntitlements());

        var result = await svc.CreateAsync(new CreateProjectRequest { Key = "ghost", Name = "Ghost" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task SuperAdmin_CannotCreateTenantWidePredefinedAction()
    {
        var db = Guid.NewGuid().ToString();
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var uow = new UnitOfWork(BuildContext(superAdmin, db));
        var projectService = new ProjectService(uow, superAdmin, new PassThroughEntitlements());
        var svc = new PredefinedActionService(uow, projectService, superAdmin, new PassThroughEntitlements());

        var result = await svc.CreateTenantAsync(new CreatePredefinedActionRequest { Text = "Do X", Prompt = "do x" });

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
    }

    [Fact]
    public async Task SuperAdmin_CannotCreateComment()
    {
        var db = Guid.NewGuid().ToString();

        // Seed a real tenant-owned project the super admin could otherwise try to comment on.
        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            seed.Projects.Add(new Project { Key = "proj", Name = "Proj", IsActive = true, OwnerId = Guid.NewGuid() });
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var uow = new UnitOfWork(BuildContext(superAdmin, db));
        var projectService = new ProjectService(uow, superAdmin, new PassThroughEntitlements());
        var actionService = new PredefinedActionService(uow, projectService, superAdmin, new PassThroughEntitlements());
        var commentService = new CommentService(uow, projectService, actionService, new FakeFileStorage(), superAdmin, new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());

        var result = await commentService.CreateAsync("proj", new CreateCommentRequest
        {
            Body = "hello",
            Environment = EnvironmentTag.Local,
            Element = new ElementCaptureDto()
        }, superAdmin.Id!.Value);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsForbidden);
    }
}
