using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class UsageEventFirstCommentTests
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

    /// <summary>
    /// An in-memory SQLite database that several DbContexts can use CONCURRENTLY.
    ///
    /// The obvious shape — one SqliteConnection handed to every context — does not work here.
    /// SqliteConnection is not thread-safe, and this fixture's whole purpose is two simultaneous
    /// writers; sharing one connection corrupts its internal command list and surfaces later as a
    /// NullReferenceException from SqliteConnection.Dispose, failing the test in roughly a third of
    /// runs with a stack that points at teardown rather than at anything being asserted.
    ///
    /// So each context opens its OWN connection to the same shared-cache in-memory database. A
    /// keep-alive connection holds that database open, since it is destroyed when the last
    /// connection to it closes.
    /// </summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            // Unique per instance so parallel test classes cannot collide on the same database.
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new AppDbContext(
                // Passing the connection STRING (not a connection object) makes each context own
                // and dispose its own connection — which is what keeps the writers independent.
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    [Fact]
    public async Task CreateAsync_ConcurrentComments_EmitsFirstCommentExactlyOnce()
    {
        using var db = new TestDb();
        var user = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var project = new Project
        {
            Id = 1,
            Key = "my-project",
            OwnerId = user.TenantId.Value,
            CreatedBy = user.Id.Value,
        };

        using (var ctx = db.MakeContext(user))
        {
            ctx.Workspaces.Add(
                new Workspace
                {
                    Id = user.TenantId.Value,
                    Name = "Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = user.Id.Value,
                }
            );
            ctx.Projects.Add(project);
            await ctx.SaveChangesAsync();
        }

        var projectService = Substitute.For<IProjectService>();
        projectService
            .EnsureAsync("my-project")
            .Returns(Task.FromResult(Result<int>.Success(project.Id)));
        projectService
            .EnsureAsync("my-project", Arg.Any<EnvironmentTag>())
            .Returns(Task.FromResult(Result<int>.Success(project.Id)));
        // Origin enforcement (R1-05) is off for this project; an unconfigured substitute returns
        // false, which would silently deny every create and make this test look like an event bug.
        projectService
            .IsOriginAllowedAsync(
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<EnvironmentTag>(),
                Arg.Any<bool>()
            )
            .Returns(Task.FromResult(true));

        var entitlements = Substitute.For<IEntitlementService>();
        entitlements
            .CheckCountAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(Task.FromResult(Result.Success()));

        var settings = Substitute.For<ISettingsService>();
        var predefinedActions = Substitute.For<IPredefinedActionService>();

        var task1 = Task.Run(async () =>
        {
            using var ctx = db.MakeContext(user);
            var uow = new UnitOfWork(ctx);
            var service = new CommentService(
                uow,
                projectService,
                predefinedActions,
                null!,
                user,
                null!,
                settings,
                entitlements
            );
            await service.CreateAsync(
                "my-project",
                new CreateCommentRequest { Body = "First", Environment = EnvironmentTag.Local },
                user.Id.Value
            );
        });

        var task2 = Task.Run(async () =>
        {
            using var ctx = db.MakeContext(user);
            var uow = new UnitOfWork(ctx);
            var service = new CommentService(
                uow,
                projectService,
                predefinedActions,
                null!,
                user,
                null!,
                settings,
                entitlements
            );
            await service.CreateAsync(
                "my-project",
                new CreateCommentRequest { Body = "Second", Environment = EnvironmentTag.Local },
                user.Id.Value
            );
        });

        await Task.WhenAll(task1, task2);

        using (var ctx = db.MakeContext(user))
        {
            var firstCommentEvents = await ctx
                .UsageEvents.Where(e => e.ProjectId == 1 && e.Type == "first_comment")
                .ToListAsync();

            Assert.Single(firstCommentEvents);
            var comments = await ctx.Comments.Where(c => c.ProjectId == 1).ToListAsync();
            Assert.Equal(2, comments.Count);
        }
    }
}
