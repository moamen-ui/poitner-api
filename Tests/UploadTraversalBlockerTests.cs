using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Pointer.Infrastructure.Storage;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-16 review fix #1 (BLOCKER). Regression coverage for the ownership-check-bypass finding: a
/// crafted screenshot path (dot-dot, percent-encoded dot-dot, backslash separators, a
/// nested/double-decoded signed URL, "../branding/...") could make a naive
/// <c>rel.StartsWith("uploads/{ownerId:N}/")</c> ownership check pass while
/// <c>Path.GetFullPath</c> resolved it outside that owner's folder — deleting another workspace's
/// screenshot or the operator's branding logo. Drives the REAL <see cref="LocalFileStorage"/> (a
/// temp disk directory) and the REAL <see cref="UploadSigner"/>, at BOTH delete sites:
/// <see cref="ScreenshotPurge.PurgeDeletedAsync"/> (step A) and <see cref="CommentService.EditAsync"/>'s
/// "remove screenshot". Every vector must leave the victim file on disk untouched.
/// </summary>
public class UploadTraversalBlockerTests : IDisposable
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

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Pointer.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
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

    /// <summary>Shared-cache Sqlite in-memory database (ScreenshotPurgeTests.TestDb precedent).</summary>
    private sealed class TestDb : IDisposable
    {
        private readonly SqliteConnection _keepAlive;
        private readonly string _connectionString;

        public TestDb()
        {
            _connectionString = $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
            _keepAlive = new SqliteConnection(_connectionString);
            _keepAlive.Open();

            using var bootstrap = MakeContext();
            bootstrap.Database.EnsureCreated();
        }

        public AppDbContext MakeContext(ICurrentUser? user = null) =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                user ?? new FakeCurrentUser { IsSuperAdmin = true },
                new ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    private readonly string _tempRoot;
    private readonly LocalFileStorage _storage;
    private readonly UploadSigner _signer;

    public UploadTraversalBlockerTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "pointer-db16-traversal-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempRoot);
        var env = new FakeWebHostEnvironment { WebRootPath = _tempRoot, ContentRootPath = _tempRoot };
        _signer = new UploadSigner(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );
        _storage = new LocalFileStorage(env, _signer);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private string WriteVictimFile(string relFolder, string fileName)
    {
        var dir = Path.Combine(_tempRoot, "uploads", relFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "victim-bytes");
        return path;
    }

    private static RetentionOptions DefaultOptions() =>
        new()
        {
            Enabled = true,
            BatchSize = 5000,
            DeletedCommentScreenshotDays = 30,
            UploadOrphanGraceHours = 48,
            ScreenshotPurgeDryRun = false,
        };

    /// <summary>
    /// Four of the five bypass vectors — all crafted to literally start with
    /// "uploads/{ownerA}/" (so a naive prefix ownership check calls them "owned") while actually
    /// resolving, once decoded and normalized, to <paramref name="victimRel"/> (a path under
    /// another owner's folder). The fifth vector ("../branding/...") is exercised separately below,
    /// since its victim is the shared branding folder, not a second workspace.
    /// </summary>
    private static string CraftedPath(int vector, string ownerA, string victimRel) =>
        vector switch
        {
            // 1. dot-dot
            0 => $"uploads/{ownerA}/../{victimRel}",
            // 2. percent-encoded dot-dot, arriving via the signed-URL decode branch (ExtractRelPath
            // decodes the "p" query parameter once).
            1
                => "/api/uploads/file?p="
                    + Uri.EscapeDataString($"uploads/{ownerA}/../{victimRel}")
                    + "&exp=99999999999&sig=x",
            // 3. backslash separators (after the literal "uploads/{ownerA}/" prefix).
            2 => $"uploads/{ownerA}/..\\{victimRel.Replace('/', '\\')}",
            // 4. nested/double-decoded signed URL: the outer "p" value, once decoded ONCE, is itself
            // another encoded signed URL naming the victim directly.
            3
                => "/api/uploads/file?p="
                    + Uri.EscapeDataString(
                        "/api/uploads/file?p="
                            + Uri.EscapeDataString($"uploads/{victimRel}")
                            + "&exp=99999999999&sig=x"
                    )
                    + "&exp=99999999999&sig=y",
            _ => throw new ArgumentOutOfRangeException(nameof(vector)),
        };

    private static async Task<(Guid Id, int ProjectId)> SeedTenantAsync(AppDbContext db, string keySuffix)
    {
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Workspace-" + keySuffix,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();

        var project = new Project
        {
            Key = $"proj-{Guid.NewGuid():N}",
            Name = "Proj-" + keySuffix,
            OwnerId = workspaceId,
            IsActiveLocal = true,
            IsActiveStaging = true,
            IsActiveProduction = true,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return (workspaceId, project.Id);
    }

    // ───────────────────────── Purge site: ScreenshotPurge.PurgeDeletedAsync ─────────────────────────

    [Theory]
    [InlineData(0)] // dot-dot
    [InlineData(1)] // encoded dot-dot
    [InlineData(2)] // backslash
    [InlineData(3)] // nested signed URL (double-decode)
    public async Task PurgeDeletedAsync_RejectsBypassVector_VictimSurvives_RowNotStamped(int vector)
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db, "A");
        var (ownerB, _) = await SeedTenantAsync(db, "B");

        var victimFile = $"{Guid.NewGuid():N}.png";
        var victimRel = $"{ownerB:N}/proj/{victimFile}";
        var victimPath = WriteVictimFile($"{ownerB:N}/proj", victimFile);

        var crafted = CraftedPath(vector, ownerA.ToString("N"), victimRel);

        var comment = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "b",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = crafted },
            DeletedAt = DateTime.UtcNow.AddDays(-31),
            DeletedBy = Guid.NewGuid(),
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        var commentId = comment.Id;

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            _storage,
            _signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.True(File.Exists(victimPath), $"vector {vector}: victim file was deleted");
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeletedAsync_RejectsTraversalIntoBranding_LogoSurvives_RowNotStamped()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db, "A");

        var logoPath = WriteVictimFile("branding", "logo.png");
        var crafted = $"uploads/{ownerA:N}/../branding/logo.png";

        var comment = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "b",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = crafted },
            DeletedAt = DateTime.UtcNow.AddDays(-31),
            DeletedBy = Guid.NewGuid(),
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        var commentId = comment.Id;

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            _storage,
            _signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.True(File.Exists(logoPath));
        Assert.Equal(1, result.Skipped);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    // ───────────────────────── Edit site: CommentService.EditAsync ─────────────────────────

    private async Task<CommentService> BuildCommentServiceAsync(AppDbContext db, ICurrentUser user)
    {
        var uow = new UnitOfWork(db);
        var notificationService = new NotificationService(uow, user);
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
        await Task.CompletedTask;
        return new CommentService(
            uow,
            projectService,
            actionService,
            _storage,
            user,
            _signer,
            new FakeSettings(),
            new PassThroughEntitlements(),
            null,
            notificationService
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task EditAsync_RemoveScreenshot_RejectsBypassVector_VictimSurvives(int vector)
    {
        using var testDb = new TestDb();
        var authorId = Guid.NewGuid();

        Guid ownerA;
        Guid ownerB;
        int commentId;
        string victimPath;
        using (var seedDb = testDb.MakeContext())
        {
            (ownerA, var projectAId) = await SeedTenantAsync(seedDb, "A");
            (ownerB, _) = await SeedTenantAsync(seedDb, "B");

            var victimFile = $"{Guid.NewGuid():N}.png";
            var victimRel = $"{ownerB:N}/proj/{victimFile}";
            victimPath = WriteVictimFile($"{ownerB:N}/proj", victimFile);

            var crafted = CraftedPath(vector, ownerA.ToString("N"), victimRel);

            var comment = new Comment
            {
                ProjectId = projectAId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = authorId,
                Body = "hi",
                OwnerId = ownerA,
                Element = new ElementCapture { ScreenshotUrl = crafted },
            };
            seedDb.Comments.Add(comment);
            await seedDb.SaveChangesAsync();
            commentId = comment.Id;
        }

        var editor = new FakeCurrentUser { Id = authorId, TenantId = ownerA };
        using var editDb = testDb.MakeContext(editor);
        var commentService = await BuildCommentServiceAsync(editDb, editor);

        var req = new EditCommentRequest { Body = "hi (edited)", RemoveScreenshot = true };
        var res = await commentService.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
        Assert.True(File.Exists(victimPath), $"vector {vector}: victim file was deleted");
    }

    [Fact]
    public async Task EditAsync_RemoveScreenshot_RejectsTraversalIntoBranding_LogoSurvives()
    {
        using var testDb = new TestDb();
        var authorId = Guid.NewGuid();

        Guid ownerA;
        int commentId;
        string logoPath;
        using (var seedDb = testDb.MakeContext())
        {
            (ownerA, var projectAId) = await SeedTenantAsync(seedDb, "A");
            logoPath = WriteVictimFile("branding", "logo.png");
            var crafted = $"uploads/{ownerA:N}/../branding/logo.png";

            var comment = new Comment
            {
                ProjectId = projectAId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = authorId,
                Body = "hi",
                OwnerId = ownerA,
                Element = new ElementCapture { ScreenshotUrl = crafted },
            };
            seedDb.Comments.Add(comment);
            await seedDb.SaveChangesAsync();
            commentId = comment.Id;
        }

        var editor = new FakeCurrentUser { Id = authorId, TenantId = ownerA };
        using var editDb = testDb.MakeContext(editor);
        var commentService = await BuildCommentServiceAsync(editDb, editor);

        var req = new EditCommentRequest { Body = "hi (edited)", RemoveScreenshot = true };
        var res = await commentService.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
        Assert.True(File.Exists(logoPath));
    }
}
