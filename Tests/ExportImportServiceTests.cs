using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Export;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;

namespace Pointer.Tests;

/// <summary>
/// Integration tests for the comment export/import feature. Uses a SQLite in-memory database
/// (the EF In-Memory provider does not support the JSON column mapping on Comment.Element).
/// Covers: versioned schema shape, screenshot omission, display-name resolution, import row
/// counts + OwnerId stamping + author re-attribution footnote + created_at preservation,
/// and schema-version rejection.
/// </summary>
public class ExportImportServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public Guid? TenantId { get; set; }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    /// <summary>Owns a SqliteConnection + schema so multiple contexts share the same in-memory DB.</summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _connection;

        public TestDb()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();
            // Create the schema once; subsequent contexts reuse the connection.
            using (var bootstrap = MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
            {
                bootstrap.Database.EnsureCreated();
            }
        }

        public AppDbContext MakeContext(ICurrentUser user) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options,
                user
            );

        public void Dispose() => _connection.Dispose();
    }

    private static (IUnitOfWork unitOfWork, IProjectService projectService, IExportImportService service)
        BuildServices(AppDbContext db, FakeCurrentUser user)
    {
        var uow = new UnitOfWork(db);
        var projects = new ProjectService(uow, user);
        var service = new ExportImportService(uow, projects, user, new FakeSettingsService());
        return (uow, projects, service);
    }

    [Fact]
    public async Task ExportProject_returns_versioned_schema_with_screenshots_omitted()
    {
        using var db = new TestDb();
        var tenant = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var seedUser = new FakeCurrentUser { IsSuperAdmin = true };

        // Seed project + two comments (each with one reply) under the tenant.
        using (var seed = db.MakeContext(seedUser))
        {
            var role = new Role { Name = "Member", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();

            seed.Users.Add(
                new User
                {
                    PublicId = alice,
                    Email = "alice@x",
                    DisplayName = "Alice",
                    OwnerId = tenant,
                    RoleId = role.Id
                }
            );
            seed.Users.Add(
                new User
                {
                    PublicId = bob,
                    Email = "bob@x",
                    DisplayName = "Bob",
                    OwnerId = tenant,
                    RoleId = role.Id
                }
            );
            var project = new Project { Key = "alpha", Name = "Alpha", OwnerId = tenant };
            seed.Projects.Add(project);
            await seed.SaveChangesAsync();

            seed.Comments.Add(
                new Comment
                {
                    ProjectId = project.Id,
                    Environment = EnvironmentTag.Staging,
                    Status = CommentStatus.Open,
                    AuthorId = alice,
                    Body = "First",
                    OwnerId = tenant,
                    CreatedAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                    Element = new ElementCapture
                    {
                        Selector = "#a",
                        ScreenshotUrl = "uploads/tenant/alpha/shot1.png"
                    }
                }
            );
            seed.Comments.Add(
                new Comment
                {
                    ProjectId = project.Id,
                    Environment = EnvironmentTag.Production,
                    Status = CommentStatus.Applied,
                    AuthorId = bob,
                    Body = "Second",
                    OwnerId = tenant,
                    CreatedAt = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc),
                    Element = new ElementCapture { Selector = "#b" }
                }
            );
            await seed.SaveChangesAsync();

            // Add replies (need comment ids).
            var c1 = seed.Comments.AsEnumerable().First(c => c.Body == "First");
            var c2 = seed.Comments.AsEnumerable().First(c => c.Body == "Second");
            seed.Replies.Add(
                new Reply
                {
                    CommentId = c1.Id,
                    AuthorId = bob,
                    Body = "r1",
                    OwnerId = tenant
                }
            );
            seed.Replies.Add(
                new Reply
                {
                    CommentId = c2.Id,
                    AuthorId = alice,
                    Body = "r2",
                    OwnerId = tenant
                }
            );
            await seed.SaveChangesAsync();
        }

        // Export as a tenant user (non-admin).
        var caller = new FakeCurrentUser { Id = alice, TenantId = tenant };
        using var ctx = db.MakeContext(caller);
        var (_, _, service) = BuildServices(ctx, caller);

        var result = await service.ExportProjectAsync("alpha", new ExportOptions());

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("1.0", result.Data!.SchemaVersion);
        Assert.Equal("alpha", result.Data.SourceProject);
        Assert.Equal(2, result.Data.Comments.Count);

        var first = result.Data.Comments[0];
        Assert.Equal("First", first.Body);
        Assert.Equal("Staging", first.Environment);
        Assert.Equal("Open", first.Status);
        Assert.Equal("Alice", first.AuthorDisplayName);
        Assert.Null(first.Element!.ScreenshotUrl);
        Assert.True(first.Element.ScreenshotOmitted); // screenshot existed → flagged
        Assert.Single(first.Replies);
        Assert.Equal("Bob", first.Replies[0].AuthorDisplayName);

        var second = result.Data.Comments[1];
        Assert.False(second.Element!.ScreenshotOmitted); // no screenshot → not flagged
    }

    [Fact]
    public async Task ImportProject_stamps_owner_reattributes_author_and_preserves_created_at()
    {
        using var db = new TestDb();
        var tenant = Guid.NewGuid();
        var importer = Guid.NewGuid();

        // Seed one project "beta" under the tenant, and the importing user row (for name resolution only).
        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            var role = new Role { Name = "Member", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
            seed.Users.Add(
                new User
                {
                    PublicId = importer,
                    Email = "i@x",
                    DisplayName = "Importer",
                    OwnerId = tenant,
                    RoleId = role.Id
                }
            );
            var beta = new Project { Key = "beta", Name = "Beta", OwnerId = tenant };
            seed.Projects.Add(beta);
            await seed.SaveChangesAsync();
        }

        var caller = new FakeCurrentUser { Id = importer, TenantId = tenant };
        using var ctx = db.MakeContext(caller);
        var (uow, _, service) = BuildServices(ctx, caller);

        var originalDate = new DateTime(2025, 1, 15, 8, 30, 0, DateTimeKind.Utc);
        var file = new ExportFileDto
        {
            SchemaVersion = "1.0",
            ExportedAt = DateTime.UtcNow,
            SourceProject = "beta",
            Comments = new List<CommentExportDto>
            {
                new()
                {
                    ExportId = "c-1",
                    ProjectKey = "beta",
                    Body = "Imported body",
                    Environment = "Production",
                    Status = "Applied",
                    IsPrivate = false,
                    CreatedAt = originalDate,
                    AuthorDisplayName = "Alice",
                    Element = new ElementCaptureExportDto { ScreenshotOmitted = true },
                    Replies = new List<ReplyExportDto>
                    {
                        new()
                        {
                            ExportId = "r-1",
                            Body = "Reply body",
                            AuthorDisplayName = "Bob",
                            CreatedAt = originalDate.AddHours(1)
                        }
                    }
                }
            }
        };

        var result = await service.ImportProjectAsync("beta", file);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, result.Data!.ImportedComments);
        Assert.Equal(1, result.Data.ImportedReplies);
        Assert.Equal(0, result.Data.SkippedDuplicates);
        Assert.NotEmpty(result.Data.Warnings); // screenshot omission warning

        // Verify DB rows.
        var inserted = await uow
            .Repository<Comment>()
            .Query()
            .Include(c => c.Replies)
            .Where(c => c.ProjectId != 0 && c.Body.Contains("Imported body"))
            .ToListAsync();
        var comment = Assert.Single(inserted);
        Assert.Equal(importer, comment.AuthorId); // re-attributed
        Assert.Equal(tenant, comment.OwnerId); // stamped from the TARGET project
        Assert.Equal(EnvironmentTag.Production, comment.Environment);
        Assert.Equal(CommentStatus.Applied, comment.Status);
        Assert.Contains("*(Imported — originally by: Alice)*", comment.Body);
        Assert.Equal(originalDate, comment.CreatedAt); // preserved verbatim
        Assert.Null(comment.Element.ScreenshotUrl); // screenshot dropped
        var reply = Assert.Single(comment.Replies);
        Assert.Equal(importer, reply.AuthorId);
        Assert.Equal(tenant, reply.OwnerId);
        Assert.Contains("*(Imported — originally by: Bob)*", reply.Body);
        Assert.Equal(originalDate.AddHours(1), reply.CreatedAt);
    }

    [Fact]
    public async Task ImportProject_rejects_unsupported_schema_version()
    {
        using var db = new TestDb();
        var tenant = Guid.NewGuid();
        var importer = Guid.NewGuid();
        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            seed.Projects.Add(new Project { Key = "gamma", Name = "Gamma", OwnerId = tenant });
            await seed.SaveChangesAsync();
        }

        var caller = new FakeCurrentUser { Id = importer, TenantId = tenant };
        using var ctx = db.MakeContext(caller);
        var (_, _, service) = BuildServices(ctx, caller);

        var file = new ExportFileDto
        {
            SchemaVersion = "2.0",
            Comments = new List<CommentExportDto>()
        };

        var result = await service.ImportProjectAsync("gamma", file);

        Assert.False(result.IsSuccess);
        Assert.Contains("Unsupported", result.Message ?? string.Empty);
    }

    [Fact]
    public async Task ExportWorkspace_includes_comments_across_projects()
    {
        using var db = new TestDb();
        var tenant = Guid.NewGuid();
        var alice = Guid.NewGuid();
        using (var seed = db.MakeContext(new FakeCurrentUser { IsSuperAdmin = true }))
        {
            var role = new Role { Name = "Member", OwnerId = tenant };
            seed.Roles.Add(role);
            await seed.SaveChangesAsync();
            seed.Users.Add(
                new User
                {
                    PublicId = alice,
                    Email = "a@x",
                    DisplayName = "Alice",
                    OwnerId = tenant,
                    RoleId = role.Id
                }
            );
            var p1 = new Project { Key = "p1", Name = "P1", OwnerId = tenant };
            var p2 = new Project { Key = "p2", Name = "P2", OwnerId = tenant };
            seed.Projects.AddRange(p1, p2);
            await seed.SaveChangesAsync();

            seed.Comments.Add(
                new Comment
                {
                    ProjectId = p1.Id,
                    Environment = EnvironmentTag.Staging,
                    Status = CommentStatus.Open,
                    AuthorId = alice,
                    Body = "on p1",
                    OwnerId = tenant
                }
            );
            seed.Comments.Add(
                new Comment
                {
                    ProjectId = p2.Id,
                    Environment = EnvironmentTag.Staging,
                    Status = CommentStatus.Open,
                    AuthorId = alice,
                    Body = "on p2",
                    OwnerId = tenant
                }
            );
            await seed.SaveChangesAsync();
        }

        var caller = new FakeCurrentUser { Id = alice, TenantId = tenant };
        using var ctx = db.MakeContext(caller);
        var (_, _, service) = BuildServices(ctx, caller);

        var result = await service.ExportWorkspaceAsync(new ExportOptions());

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data!.SourceProject);
        Assert.Equal(2, result.Data.Comments.Count);
        Assert.Contains(result.Data.Comments, c => c.ProjectKey == "p1");
        Assert.Contains(result.Data.Comments, c => c.ProjectKey == "p2");
    }
}
