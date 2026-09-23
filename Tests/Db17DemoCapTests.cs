using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Export;
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
/// DB-17 review finding #6: the demo comment cap (create + import) reads the WORKSPACE'S
/// <c>DemoCommentCapOverride</c> — the only copy since DB-11e dropped the users column.
/// </summary>
public class Db17DemoCapTests
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

        // The global fallback demo cap — deliberately high so only the per-workspace override
        // (the thing under test) can be what actually refuses the create/import below.
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(1000);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext BuildInMemoryContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public async Task CommentCap_UsesWorkspaceOverride_NotUser()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var seed = BuildInMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "Demo Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                    // The live demo, with a TIGHT cap of 1.
                    DemoExpiresAt = DateTime.UtcNow.AddHours(1),
                    DemoCommentCapOverride = 1,
                }
            );
            var role = new Role { Name = "Workspace Admin", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            seed.Users.Add(
                new User
                {
                    PublicId = author,
                    Email = $"demo-{author:N}@demo.pointer",
                    PasswordHash = "x",
                    DisplayName = "Demo Admin",
                    RoleId = role.Id,
                    OwnerId = tenant,
                    IsActive = true,
                    IsDemo = true,
                    ApprovalStatus = ApprovalStatus.Approved,
                }
            );
            seed.Projects.Add(
                new Project
                {
                    Key = "proj",
                    Name = "Proj",
                    IsActiveLocal = true,
                    IsActiveStaging = true,
                    IsActiveProduction = true,
                    OwnerId = tenant,
                }
            );
            await seed.SaveChangesAsync();

            // One existing comment already meets the workspace's cap of 1.
            seed.Comments.Add(
                new Comment
                {
                    ProjectId = seed.Projects.Single(p => p.Key == "proj").Id,
                    OwnerId = tenant,
                    AuthorId = author,
                    Body = "first",
                    Status = CommentStatus.Open,
                    Environment = EnvironmentTag.Local,
                    Element = new ElementCapture(),
                }
            );
            await seed.SaveChangesAsync();
        }

        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
        };
        var db = BuildInMemoryContext(user, dbName);
        var uow = new UnitOfWork(db);
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
            new PassThroughEntitlements()
        );

        var result = await commentService.CreateAsync(
            "proj",
            new CreateCommentRequest
            {
                Body = "second — should be refused",
                Environment = EnvironmentTag.Local,
                Element = new ElementCaptureDto(),
            },
            author
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("Demo limit reached", result.Message);
    }

    /// <summary>DB-17 review finding #6 (LOW): a converted workspace — even one still holding a
    /// non-null DemoExpiresAt for the Demo:ConvertRequiresVerification re-verification grace — is
    /// not a demo for the comment cap; the create must succeed despite already being over the old
    /// cap.</summary>
    [Fact]
    public async Task CommentCap_IgnoresConvertedWorkspace()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var seed = BuildInMemoryContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "Converted Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                    // Demo:ConvertRequiresVerification grace: converted AND still non-null.
                    DemoExpiresAt = DateTime.UtcNow.AddHours(48),
                    DemoConvertedAt = DateTime.UtcNow,
                    DemoCommentCapOverride = 1,
                }
            );
            var role = new Role { Name = "Workspace Admin", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
            seed.Users.Add(
                new User
                {
                    PublicId = author,
                    Email = $"real-{author:N}@user.com",
                    PasswordHash = "x",
                    DisplayName = "Real Admin",
                    RoleId = role.Id,
                    OwnerId = tenant,
                    IsActive = true,
                    ApprovalStatus = ApprovalStatus.Approved,
                }
            );
            seed.Projects.Add(
                new Project
                {
                    Key = "proj",
                    Name = "Proj",
                    IsActiveLocal = true,
                    IsActiveStaging = true,
                    IsActiveProduction = true,
                    OwnerId = tenant,
                }
            );
            await seed.SaveChangesAsync();
            // Already at (and over) the old cap of 1.
            seed.Comments.Add(
                new Comment
                {
                    ProjectId = seed.Projects.Single(p => p.Key == "proj").Id,
                    OwnerId = tenant,
                    AuthorId = author,
                    Body = "first",
                    Status = CommentStatus.Open,
                    Environment = EnvironmentTag.Local,
                    Element = new ElementCapture(),
                }
            );
            await seed.SaveChangesAsync();
        }

        var user = new FakeCurrentUser { Id = author, TenantId = tenant };
        var db = BuildInMemoryContext(user, dbName);
        var uow = new UnitOfWork(db);
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
            new PassThroughEntitlements()
        );

        var result = await commentService.CreateAsync(
            "proj",
            new CreateCommentRequest
            {
                Body = "second — a converted workspace is not capped",
                Environment = EnvironmentTag.Local,
                Element = new ElementCaptureDto(),
            },
            author
        );

        Assert.True(result.IsSuccess, result.Message);
    }

    /// <summary>Owns a SqliteConnection + schema (ExportImportServiceTests precedent — the EF
    /// In-Memory provider does not support the JSON column mapping on Comment.Element).</summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDb()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            _connection.CreateFunction<string?, string?>("btrim", s => s?.Trim());
            using var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true });
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options,
                user,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _connection.Dispose();
    }

    [Fact]
    public async Task ImportCap_UsesWorkspaceOverride()
    {
        using var db = new TestDb();
        var tenant = Guid.NewGuid();
        var importer = Guid.NewGuid();

        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            seed.Workspaces.Add(
                new Workspace
                {
                    Id = tenant,
                    Name = "Demo Workspace",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = tenant,
                    DemoExpiresAt = DateTime.UtcNow.AddHours(1),
                    // Tight cap of 1; the import below tries to bring in 2.
                    DemoCommentCapOverride = 1,
                }
            );
            var role = new Role { Name = "Workspace Admin", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            seed.Users.Add(
                new User
                {
                    PublicId = importer,
                    Email = $"demo-{importer:N}@demo.pointer",
                    PasswordHash = "x",
                    DisplayName = "Demo Admin",
                    OwnerId = tenant,
                    RoleId = role.Id,
                    IsActive = true,
                    IsDemo = true,
                    ApprovalStatus = ApprovalStatus.Approved,
                }
            );
            seed.Projects.Add(
                new Project
                {
                    Key = "proj",
                    Name = "Proj",
                    OwnerId = tenant,
                }
            );
            await seed.SaveChangesAsync();
        }

        var caller = new FakeCurrentUser { Id = importer, TenantId = tenant };
        using var ctx = db.MakeContext(caller);
        var uow = new UnitOfWork(ctx);
        var projects = new ProjectService(
            uow,
            caller,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var service = new ExportImportService(uow, projects, caller, new FakeSettings());

        var file = new ExportFileDto
        {
            SchemaVersion = "1.0",
            ExportedAt = DateTime.UtcNow,
            SourceProject = "proj",
            Comments = new List<CommentExportDto>
            {
                new()
                {
                    ExportId = "c-1",
                    ProjectKey = "proj",
                    Body = "one",
                    Environment = "Production",
                    Status = "Applied",
                    IsPrivate = false,
                    CreatedAt = DateTime.UtcNow,
                    AuthorDisplayName = "Alice",
                    Element = new ElementCaptureExportDto { ScreenshotOmitted = true },
                },
                new()
                {
                    ExportId = "c-2",
                    ProjectKey = "proj",
                    Body = "two",
                    Environment = "Production",
                    Status = "Applied",
                    IsPrivate = false,
                    CreatedAt = DateTime.UtcNow,
                    AuthorDisplayName = "Bob",
                    Element = new ElementCaptureExportDto { ScreenshotOmitted = true },
                },
            },
        };

        var result = await service.ImportProjectAsync("proj", file);

        Assert.False(result.IsSuccess);
        Assert.Contains("Demo limit reached", result.Message);
    }
}
