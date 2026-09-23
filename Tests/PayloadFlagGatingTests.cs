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
/// The caller gate, exercised through the real service rather than asserted on shapes.
///
/// The rule: the widget and dashboard see the advisory flags; everyone else gets the keys absent.
/// It is NOT an auth boundary — the header is trivially forgeable — it is the guarantee that the
/// DOCUMENTED AI paths never see the flag, including the legacy pointer.sh copies already sitting
/// in customer repositories that no client-side change can reach.
/// </summary>
public class PayloadFlagGatingTests
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

    private sealed class FakeClient(bool human) : ICurrentClient
    {
        public bool IsHumanSurface { get; } = human;
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

    private static AppDbContext Ctx(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static CommentService Service(ICurrentUser user, string dbName, ICurrentClient? client)
    {
        var uow = new UnitOfWork(Ctx(user, dbName));
        var projects = new ProjectService(
            uow,
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var actions = new PredefinedActionService(
            uow,
            projects,
            user,
            new PassThroughEntitlements()
        );
        return new CommentService(
            uow,
            projects,
            actions,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            client
        );
    }

    private const string SecretBody = "the key is sk-abcdefghijklmnopqrstuvwxyz012345";

    private static (Guid tenant, Guid author, string key) Seed(string dbName)
    {
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);
        var project = new Project
        {
            Key = "proj",
            Name = "Proj",
            IsActiveLocal = true,
            OwnerId = tenant,
        };
        db.Projects.Add(project);
        db.SaveChanges();

        db.Comments.Add(
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = tenant,
                AuthorId = author,
                Body = SecretBody,
                Status = CommentStatus.Open,
                Environment = EnvironmentTag.Local,
                HasPayloadFlag = true,
                PayloadFlags = new List<string> { "openai_key" },
                Element = new ElementCapture { Selector = "#x", Route = "/" },
            }
        );
        db.SaveChanges();

        return (tenant, author, project.Key);
    }

    [Fact]
    public async Task AHumanSurface_SeesTheAdvisoryFlags()
    {
        var name = nameof(AHumanSurface_SeesTheAdvisoryFlags);
        var (tenant, author, key) = Seed(name);
        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsAdmin = true,
        };

        var result = await Service(user, name, new FakeClient(human: true))
            .ListAsync(key, new CommentFilter { PageSize = 50 }, author);

        var item = Assert.Single(result.Data!.Items);
        Assert.True(item.HasPayloadFlag);
        Assert.Contains("openai_key", item.PayloadFlags!);
    }

    [Fact]
    public async Task EveryOtherCaller_GetsTheKeysAbsent_NotFalse()
    {
        // Absent, not `false`. `false` would be the server asserting the text is clean to a caller
        // it deliberately withholds the answer from.
        var name = nameof(EveryOtherCaller_GetsTheKeysAbsent_NotFalse);
        var (tenant, author, key) = Seed(name);
        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsAdmin = true,
        };

        var result = await Service(user, name, new FakeClient(human: false))
            .ListAsync(key, new CommentFilter { PageSize = 50 }, author);

        var item = Assert.Single(result.Data!.Items);
        Assert.Null(item.HasPayloadFlag);
        Assert.Null(item.PayloadFlags);
    }

    [Fact]
    public async Task NoClientAccessorAtAll_FailsClosed()
    {
        // A caller that was never wired up must hide the flags, not leak them. Losing an advisory
        // badge is cosmetic; leaking one into an AI payload is an injection surface.
        var name = nameof(NoClientAccessorAtAll_FailsClosed);
        var (tenant, author, key) = Seed(name);
        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsAdmin = true,
        };

        var result = await Service(user, name, client: null)
            .ListAsync(key, new CommentFilter { PageSize = 50 }, author);

        Assert.Null(Assert.Single(result.Data!.Items).HasPayloadFlag);
    }

    [Fact]
    public async Task TheApplyQueue_NeverCarriesTheFlags_EvenForAHumanSurface()
    {
        // The strongest guarantee: even a dashboard-authenticated admin pulling the apply queue
        // gets a payload with no flags on it, because the DTO has nowhere to put them.
        var name = nameof(TheApplyQueue_NeverCarriesTheFlags_EvenForAHumanSurface);
        var (tenant, author, key) = Seed(name);
        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsAdmin = true,
        };

        using (var db = Ctx(user, name))
        {
            var comment = db.Comments.IgnoreQueryFilters().First();
            comment.Status = CommentStatus.ReadyToApply;
            db.SaveChanges();
        }

        var queue = await Service(user, name, new FakeClient(human: true))
            .ListApplyQueueAsync(key, new CommentFilter { PageSize = 50 });

        var item = Assert.Single(queue.Data!.Items);
        Assert.Equal(SecretBody, item.Body);

        var serialised = System.Text.Json.JsonSerializer.Serialize(item);
        Assert.DoesNotContain("payloadFlags", serialised, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hasPayloadFlag", serialised, StringComparison.OrdinalIgnoreCase);
    }
}
