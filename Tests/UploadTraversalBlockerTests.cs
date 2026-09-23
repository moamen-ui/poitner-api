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

    // ───────────────────────── DB-16 re-review: trailing-newline regex-anchor bypass ─────────────────────────

    [Fact]
    public void IsCanonical_RejectsFileSegmentWithTrailingNewline()
    {
        // In .NET, `$` (without RegexOptions.Multiline) matches both the true end of input AND the
        // position just before a single trailing '\n' — so "^[0-9a-f]{32}\.png$" would (bug)
        // accept "<hex32>.png\n". Every UploadPaths pattern now anchors with \z instead, and '\n'/
        // '\r' are also in the disallowed-char set as a redundant first guard.
        var owner = Guid.NewGuid().ToString("N");
        var rel = $"uploads/{owner}/proj/{Guid.NewGuid():N}.png\n";

        Assert.False(UploadPaths.IsCanonical(rel));
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

    // ───────────────────── DB-16 re-review: nested vector against a B-owned row ─────────────────────
    // The four vectors above are all crafted to literally start with "uploads/{ownerA}/" — the SAME
    // owner as the comment row that names them. This one instead seeds the crafted path on an
    // OWNER-B comment, so the naive "owned" prefix ("uploads/{ownerB}/…") belongs to the row's real
    // owner, while the embedded second signed-URL layer names owner A's file as the real victim —
    // exercising TryResolve's guard (1) (no second decode) end to end, not just IsCanonical's
    // disallowed-char rejection alone.

    private static string NestedVectorNamingVictim(Guid ownerOfRow, string victimRel) =>
        "/api/uploads/file?p="
        + Uri.EscapeDataString(
            $"uploads/{ownerOfRow:N}/api/uploads/file?p=" + Uri.EscapeDataString($"uploads/{victimRel}")
        );

    [Fact]
    public async Task PurgeDeletedAsync_NestedVectorOnBOwnedRow_NamesOwnerAFile_VictimSurvives()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, _) = await SeedTenantAsync(db, "A");
        var (ownerB, projectB) = await SeedTenantAsync(db, "B");

        var victimFile = $"{Guid.NewGuid():N}.png";
        var victimRel = $"{ownerA:N}/proj/{victimFile}";
        var victimPath = WriteVictimFile($"{ownerA:N}/proj", victimFile);

        var crafted = NestedVectorNamingVictim(ownerB, victimRel);

        var comment = new Comment
        {
            ProjectId = projectB,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "b",
            OwnerId = ownerB,
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

        Assert.True(
            File.Exists(victimPath),
            "victim file (owner A) was deleted via a B-owned row's nested/double-decoded path"
        );
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task EditAsync_RemoveScreenshot_NestedVectorOnBOwnedRow_NamesOwnerAFile_VictimSurvives()
    {
        using var testDb = new TestDb();
        var authorId = Guid.NewGuid();

        Guid ownerA;
        Guid ownerB;
        int commentId;
        string victimPath;
        using (var seedDb = testDb.MakeContext())
        {
            (ownerA, _) = await SeedTenantAsync(seedDb, "A");
            (ownerB, var projectBId) = await SeedTenantAsync(seedDb, "B");

            var victimFile = $"{Guid.NewGuid():N}.png";
            var victimRel = $"{ownerA:N}/proj/{victimFile}";
            victimPath = WriteVictimFile($"{ownerA:N}/proj", victimFile);

            var crafted = NestedVectorNamingVictim(ownerB, victimRel);

            var comment = new Comment
            {
                ProjectId = projectBId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = authorId,
                Body = "hi",
                OwnerId = ownerB,
                Element = new ElementCapture { ScreenshotUrl = crafted },
            };
            seedDb.Comments.Add(comment);
            await seedDb.SaveChangesAsync();
            commentId = comment.Id;
        }

        var editor = new FakeCurrentUser { Id = authorId, TenantId = ownerB };
        using var editDb = testDb.MakeContext(editor);
        var commentService = await BuildCommentServiceAsync(editDb, editor);

        var req = new EditCommentRequest { Body = "hi (edited)", RemoveScreenshot = true };
        var res = await commentService.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
        Assert.True(File.Exists(victimPath), "victim file (owner A) was deleted via owner B's edit");
    }

    // ───────────────────── DB-16 re-review: same-workspace shared-path protection ─────────────────────
    // A file may be named by more than one comment in the SAME workspace (a duplicate/shared upload).
    // Neither delete site may remove it while any OTHER comment (live, or soft-deleted but not yet
    // purged) still names the same canonical path.

    [Fact]
    public async Task PurgeDeletedAsync_SharedWithLiveComment_NotDeleted_NotStamped()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db, "A");

        var fileName = $"{Guid.NewGuid():N}.png";
        var rel = $"uploads/{ownerA:N}/proj/{fileName}";
        var filePath = WriteVictimFile($"{ownerA:N}/proj", fileName);
        var url = _signer.SignedUrl(rel);

        var purgeEligible = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "old",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = url },
            DeletedAt = DateTime.UtcNow.AddDays(-31),
            DeletedBy = Guid.NewGuid(),
        };
        db.Comments.Add(purgeEligible);
        await db.SaveChangesAsync();
        var purgeId = purgeEligible.Id;

        var stillLive = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "same upload, still live",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = url },
        };
        db.Comments.Add(stillLive);
        await db.SaveChangesAsync();

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            _storage,
            _signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.True(File.Exists(filePath), "file still referenced by a live comment was deleted");
        Assert.Equal(1, result.SharedWithLiveComment);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == purgeId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeletedAsync_PositiveControl_OwnedUnsharedPath_DeletedAndStamped()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db, "A");

        var fileName = $"{Guid.NewGuid():N}.png";
        var rel = $"uploads/{ownerA:N}/proj/{fileName}";
        var filePath = WriteVictimFile($"{ownerA:N}/proj", fileName);
        var url = _signer.SignedUrl(rel);

        var comment = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "old",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = url },
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

        Assert.False(File.Exists(filePath));
        Assert.Equal(1, result.CommentsPurged);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(0, result.SharedWithLiveComment);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.NotNull(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task EditAsync_RemoveScreenshot_SharedWithAnotherLiveComment_UrlNulled_FileNotDeleted()
    {
        using var testDb = new TestDb();
        var authorId = Guid.NewGuid();

        Guid ownerA;
        int commentId;
        string filePath;
        using (var seedDb = testDb.MakeContext())
        {
            (ownerA, var projectAId) = await SeedTenantAsync(seedDb, "A");
            var fileName = $"{Guid.NewGuid():N}.png";
            var rel = $"uploads/{ownerA:N}/proj/{fileName}";
            filePath = WriteVictimFile($"{ownerA:N}/proj", fileName);
            var url = _signer.SignedUrl(rel);

            var comment = new Comment
            {
                ProjectId = projectAId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = authorId,
                Body = "hi",
                OwnerId = ownerA,
                Element = new ElementCapture { ScreenshotUrl = url },
            };
            seedDb.Comments.Add(comment);
            await seedDb.SaveChangesAsync();
            commentId = comment.Id;

            var otherLive = new Comment
            {
                ProjectId = projectAId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = Guid.NewGuid(),
                Body = "same upload",
                OwnerId = ownerA,
                Element = new ElementCapture { ScreenshotUrl = url },
            };
            seedDb.Comments.Add(otherLive);
            await seedDb.SaveChangesAsync();
        }

        var editor = new FakeCurrentUser { Id = authorId, TenantId = ownerA };
        using var editDb = testDb.MakeContext(editor);
        var commentService = await BuildCommentServiceAsync(editDb, editor);

        var req = new EditCommentRequest { Body = "hi (edited)", RemoveScreenshot = true };
        var res = await commentService.EditAsync(commentId, req, authorId);

        Assert.True(res.IsSuccess, res.Message);
        Assert.Null(res.Data!.Element.ScreenshotUrl);
        Assert.True(File.Exists(filePath), "file still referenced by another live comment was deleted");
    }

    [Fact]
    public async Task EditAsync_RemoveScreenshot_PositiveControl_OwnedUnsharedFile_Deleted()
    {
        using var testDb = new TestDb();
        var authorId = Guid.NewGuid();

        Guid ownerA;
        int commentId;
        string filePath;
        using (var seedDb = testDb.MakeContext())
        {
            (ownerA, var projectAId) = await SeedTenantAsync(seedDb, "A");
            var fileName = $"{Guid.NewGuid():N}.png";
            var rel = $"uploads/{ownerA:N}/proj/{fileName}";
            filePath = WriteVictimFile($"{ownerA:N}/proj", fileName);
            var url = _signer.SignedUrl(rel);

            var comment = new Comment
            {
                ProjectId = projectAId,
                Environment = EnvironmentTag.Local,
                Status = CommentStatus.Open,
                AuthorId = authorId,
                Body = "hi",
                OwnerId = ownerA,
                Element = new ElementCapture { ScreenshotUrl = url },
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
        Assert.False(File.Exists(filePath));
    }

    // ───────────────────── DB-16 re-review: real round trip through the orphan sweep ─────────────────────

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    public async Task SaveAsync_RoundTrip_ReferencedFile_SurvivesOrphanSweep(string extension)
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db, "A");

        using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var rel = await _storage.SaveAsync(ownerA.ToString("N"), "proj", content, extension);

        Assert.True(UploadPaths.IsCanonical(rel), $"SaveAsync produced a non-canonical path: {rel}");

        var url = _signer.SignedUrl(rel);

        var comment = new Comment
        {
            ProjectId = projectA,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "b",
            OwnerId = ownerA,
            Element = new ElementCapture { ScreenshotUrl = url },
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();

        var absolutePath = Path.Combine(_tempRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(absolutePath));
        File.SetLastWriteTimeUtc(absolutePath, DateTime.UtcNow.AddHours(-72));

        await ScreenshotPurge.OrphanSweepAsync(
            db,
            _storage,
            _signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.True(File.Exists(absolutePath), "a file still referenced by a live comment was swept as an orphan");
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
