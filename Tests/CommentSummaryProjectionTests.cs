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
/// CommentService.ListSummaryAsync (?view=summary) — the lean AI-agent projection. Must apply the
/// EXACT same visibility rules as ListAsync (quick-access, private comments) since both now share
/// BuildCommentQuery, and must map Route/SourcePath/AuthorName correctly while omitting the heavy
/// per-comment payload (Replies, Element.Snapshot/ComputedStyles/AppliedCssRules/ParentInfo).
/// </summary>
public class CommentSummaryProjectionTests
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

    private CommentService BuildService(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        return new CommentService(uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());
    }

    private static (string key, int clientCommentId, int otherCommentId) SeedProjectWithTwoComments(
        string dbName, Guid tenant, Guid clientId, Guid otherId)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
        seed.Projects.Add(project);
        seed.SaveChanges();

        var mine = new Comment
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = clientId, Body = "mine",
            Status = CommentStatus.Open, Environment = EnvironmentTag.Staging,
            Element = new ElementCapture { Route = "/mine", SourcePath = "src/Mine.tsx" },
        };
        var theirs = new Comment
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = otherId, Body = "theirs",
            Status = CommentStatus.Open, Environment = EnvironmentTag.Local,
            Element = new ElementCapture { Route = "/theirs", SourcePath = "src/Theirs.tsx" },
        };
        seed.Comments.AddRange(mine, theirs);
        seed.SaveChanges();
        return ("proj", mine.Id, theirs.Id);
    }

    [Fact]
    public async Task QuickAccess_ListSummaryAsync_OnlySeesOwnComments()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var (key, mineId, _) = SeedProjectWithTwoComments(db, tenant, clientId, Guid.NewGuid());

        var client = new FakeCurrentUser { Id = clientId, IsQuickAccess = true, TenantId = tenant };
        var svc = BuildService(client, db);

        var result = await svc.ListSummaryAsync(key, new CommentFilter(), clientId);

        Assert.True(result.IsSuccess);
        var ids = result.Data!.Items.Select(c => c.Id).ToList();
        Assert.Single(ids);
        Assert.Equal(mineId, ids[0]);
    }

    [Fact]
    public async Task NonQuickAccess_ListSummaryAsync_SeesEveryComment()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var (key, _, _) = SeedProjectWithTwoComments(db, tenant, Guid.NewGuid(), Guid.NewGuid());

        var staff = new FakeCurrentUser { Id = staffId, IsAdmin = true, TenantId = tenant };
        var svc = BuildService(staff, db);

        var result = await svc.ListSummaryAsync(key, new CommentFilter(), staffId);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Data!.Items.Count);
    }

    [Fact]
    public async Task ListSummaryAsync_PrivateComment_HiddenFromNonAuthor_ButCountedAsHidden()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
            seed.Projects.Add(project);
            seed.SaveChanges();
            seed.Comments.Add(new Comment
            {
                ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "secret",
                Status = CommentStatus.Open, Environment = EnvironmentTag.Local, IsPrivate = true,
                Element = new ElementCapture(),
            });
            seed.SaveChanges();
        }

        var other = new FakeCurrentUser { Id = otherId, IsAdmin = true, TenantId = tenant };
        var svc = BuildService(other, db);

        var result = await svc.ListSummaryAsync("proj", new CommentFilter(), otherId);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(1, result.Data!.HiddenPrivateCount);
    }

    [Fact]
    public async Task ListSummaryAsync_MapsRouteSourcePathAndAuthorName()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var authorId = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
            seed.Projects.Add(project);
            var role = new Role { Name = "Developer", OwnerId = null };
            seed.Roles.Add(role);
            seed.SaveChanges();
            seed.Users.Add(new User
            {
                PublicId = authorId, Email = "dev@example.com", PasswordHash = "x",
                DisplayName = "Dev Person", RoleId = role.Id, OwnerId = tenant, IsActive = true,
            });
            seed.SaveChanges();
            seed.Comments.Add(new Comment
            {
                ProjectId = project.Id, OwnerId = tenant, AuthorId = authorId, Body = "fix this",
                Status = CommentStatus.ReadyToApply, Environment = EnvironmentTag.Production,
                Element = new ElementCapture { Route = "/checkout", SourcePath = "src/Checkout.tsx", Snapshot = "<div>huge</div>" },
            });
            seed.SaveChanges();
        }

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, db);

        var result = await svc.ListSummaryAsync("proj", new CommentFilter(), admin.Id!.Value);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Data!.Items);
        Assert.Equal("/checkout", item.Route);
        Assert.Equal("src/Checkout.tsx", item.SourcePath);
        Assert.Equal("Dev Person", item.AuthorName);
        Assert.Equal(CommentStatus.ReadyToApply, item.Status);
        Assert.Equal(EnvironmentTag.Production, item.Environment);
        Assert.Equal("fix this", item.Body);
    }

    [Fact]
    public async Task ListSummaryAsync_StatusAndEnvironmentFilters_Apply()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var (key, mineId, _) = SeedProjectWithTwoComments(db, tenant, Guid.NewGuid(), Guid.NewGuid());
        _ = mineId;

        var admin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = tenant };
        var svc = BuildService(admin, db);

        var result = await svc.ListSummaryAsync(key, new CommentFilter { Environment = EnvironmentTag.Local }, admin.Id!.Value);

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Data!.Items);
        Assert.Equal(EnvironmentTag.Local, item.Environment);
    }
}
