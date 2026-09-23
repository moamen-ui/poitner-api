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
/// The dashboard comments screen filters the project list by payload flag (R2-06), deploy state
/// (R3-01) and a body search. These ride on the same query the widget and the AI apply path use,
/// so they must be pure narrowing — omitted filters change nothing.
/// </summary>
public class CommentListFilterTests
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

    private static CommentService BuildService(ICurrentUser user, string dbName)
    {
        var uow = new UnitOfWork(BuildContext(user, dbName));
        var projectService = new ProjectService(uow, user, new PassThroughEntitlements(), TestProjectServiceDeps.Settings(), TestProjectServiceDeps.Configuration(), new FakeAuditWriter());
        var actionService = new PredefinedActionService(uow, projectService, user, new PassThroughEntitlements());
        return new CommentService(uow, projectService, actionService, new FakeFileStorage(), user,
            new FakeUploadSigner(), new FakeSettings(), new PassThroughEntitlements());
    }

    // Four comments: open+flagged, applied-not-live, applied+live, plain open.
    private static (string key, int flagged, int appliedNotLive, int live, int plain) Seed(string dbName, Guid tenant, Guid author)
    {
        using var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project { Key = "proj", Name = "Proj", IsActiveLocal = true, IsActiveStaging = true, IsActiveProduction = true, OwnerId = tenant };
        seed.Projects.Add(project);
        seed.SaveChanges();

        Comment Make(string body) => new()
        {
            ProjectId = project.Id, OwnerId = tenant, AuthorId = author, Body = body,
            Status = CommentStatus.Open, Environment = EnvironmentTag.Local, Element = new ElementCapture()
        };

        var flagged = Make("token leaked here");
        flagged.HasPayloadFlag = true;
        flagged.PayloadFlags = new List<string> { "jwt" };

        var appliedNotLive = Make("Fix the header colour");
        appliedNotLive.Status = CommentStatus.Applied;
        appliedNotLive.AppliedAt = DateTime.UtcNow.AddHours(-2);
        appliedNotLive.CommitSha = "abc123";

        var live = Make("Fix the footer spacing");
        live.Status = CommentStatus.Applied;
        live.AppliedAt = DateTime.UtcNow.AddHours(-3);
        live.CommitSha = "def456";
        live.DeployedAt = DateTime.UtcNow.AddHours(-1);
        live.DeployedSha = "def456";

        var plain = Make("Plain open comment");

        seed.Comments.AddRange(flagged, appliedNotLive, live, plain);
        seed.SaveChanges();
        return ("proj", flagged.Id, appliedNotLive.Id, live.Id, plain.Id);
    }

    private static async Task<List<int>> Ids(CommentService svc, string key, CommentFilter filter, Guid caller)
    {
        var result = await svc.ListAsync(key, filter, caller);
        Assert.True(result.IsSuccess);
        return result.Data!.Items.Select(c => c.Id).OrderBy(i => i).ToList();
    }

    [Fact]
    public async Task NoFilters_ReturnsEverything()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var (key, a, b, c, d) = Seed(db, tenant, staff);
        var svc = BuildService(new FakeCurrentUser { Id = staff, IsAdmin = true, TenantId = tenant }, db);

        var ids = await Ids(svc, key, new CommentFilter(), staff);

        Assert.Equal(new[] { a, b, c, d }.OrderBy(i => i), ids);
    }

    [Fact]
    public async Task Flagged_OnlyReturnsPayloadFlaggedComments()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var (key, flagged, _, _, _) = Seed(db, tenant, staff);
        var svc = BuildService(new FakeCurrentUser { Id = staff, IsAdmin = true, TenantId = tenant }, db);

        var ids = await Ids(svc, key, new CommentFilter { Flagged = true }, staff);

        Assert.Equal(new[] { flagged }, ids);
    }

    [Fact]
    public async Task Live_True_ReturnsOnlyAppliedAndDeployed()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var (key, _, _, live, _) = Seed(db, tenant, staff);
        var svc = BuildService(new FakeCurrentUser { Id = staff, IsAdmin = true, TenantId = tenant }, db);

        var ids = await Ids(svc, key, new CommentFilter { Live = true }, staff);

        Assert.Equal(new[] { live }, ids);
    }

    [Fact]
    public async Task Live_False_ReturnsAppliedButNotDeployed_NeverUnapplied()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var (key, _, appliedNotLive, _, _) = Seed(db, tenant, staff);
        var svc = BuildService(new FakeCurrentUser { Id = staff, IsAdmin = true, TenantId = tenant }, db);

        var ids = await Ids(svc, key, new CommentFilter { Live = false }, staff);

        Assert.Equal(new[] { appliedNotLive }, ids);
    }

    [Fact]
    public async Task Search_MatchesBodyCaseInsensitively()
    {
        var db = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var staff = Guid.NewGuid();
        var (key, _, header, footer, _) = Seed(db, tenant, staff);
        var svc = BuildService(new FakeCurrentUser { Id = staff, IsAdmin = true, TenantId = tenant }, db);

        var ids = await Ids(svc, key, new CommentFilter { Search = "  FIX the " }, staff);

        Assert.Equal(new[] { header, footer }.OrderBy(i => i), ids);
    }
}
