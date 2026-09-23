using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Storage;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-16: the screenshot purge (step A, deleted-comment files) and the uploads orphan sweep
/// (step B). Drives <see cref="ScreenshotPurge"/> directly against a Sqlite-backed AppDbContext
/// (ExecuteUpdateAsync is relational-only) with a real <see cref="UploadSigner"/> — the URL shapes
/// stored in <c>comments.element-&gt;&gt;'ScreenshotUrl'</c> are signed, so the ownership check has to
/// decode them the same way production does.
/// </summary>
public class ScreenshotPurgeTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    /// <summary>Shared-cache Sqlite in-memory database (RetentionServiceTests.TestDb precedent).</summary>
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

        public AppDbContext MakeContext() =>
            new(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseSqlite(_connectionString)
                    .AddInterceptors(new SqliteBtrimFunctionInterceptor())
                    .Options,
                new FakeCurrentUser(),
                new ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

    /// <summary>
    /// DB-16 §6: an in-memory file map recording every call, extended (beyond the
    /// DeletionSemanticsTests shape) with the five default IFileStorage members.
    /// </summary>
    private sealed class RecordingFileStorage : IFileStorage
    {
        public sealed record Entry(long Bytes, DateTime Mtime);

        public Dictionary<string, Entry> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Deleted { get; } = new();
        public List<string> DeletedOwners { get; } = new();
        public List<string> ListedSegments { get; } = new();
        public List<string> EmptyFolderCalls { get; } = new();
        public List<string> Segments { get; } = new();

        /// <summary>When true, DeleteAsync records the call but leaves the file in place (simulates
        /// a storage failure — PurgeDeleted_StorageStillHasFile_LeavesUnstamped).</summary>
        public bool FailDelete { get; set; }

        /// <summary>DB-16 review fix #4: when set, ListOwnerFilesAsync throws partway through
        /// enumerating THIS segment (simulating a folder that throws on enumeration — a broken
        /// symlink, a permissions error deep in the tree) so OrphanSweepAsync's per-folder isolation
        /// can be exercised without touching real disk permissions.</summary>
        public string? ThrowForSegment { get; set; }

        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult($"uploads/{ownerSegment}/{project}/x");

        public Task DeleteAsync(string relativePathOrUrl)
        {
            Deleted.Add(relativePathOrUrl);
            if (!FailDelete)
                Files.Remove(relativePathOrUrl);
            return Task.CompletedTask;
        }

        public Task DeleteOwnerFilesAsync(string ownerSegment)
        {
            DeletedOwners.Add(ownerSegment);
            return Task.CompletedTask;
        }

        public Task<bool?> ExistsAsync(string relativePath) =>
            Task.FromResult<bool?>(Files.ContainsKey(relativePath));

        public Task<long> SizeAsync(string relativePath) =>
            Task.FromResult(Files.TryGetValue(relativePath, out var e) ? e.Bytes : 0L);

        public async IAsyncEnumerable<StoredFile> ListOwnerFilesAsync(string ownerSegment)
        {
            ListedSegments.Add(ownerSegment);
            await Task.Yield();
            if (ownerSegment == ThrowForSegment)
                throw new IOException("simulated enumeration failure");

            var prefix = $"uploads/{ownerSegment}/";
            foreach (
                var kv in Files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList()
            )
            {
                await Task.Yield();
                yield return new StoredFile(kv.Key, kv.Value.Bytes, kv.Value.Mtime);
            }
        }

        public Task<IReadOnlyList<string>> ListOwnerSegmentsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(Segments.ToArray());

        public Task<int> DeleteEmptyProjectFoldersAsync(string ownerSegment, DateTime olderThanUtc)
        {
            EmptyFolderCalls.Add(ownerSegment);
            return Task.FromResult(0);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose() { }
        }
    }

    private static UploadSigner RealSigner() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );

    private static RetentionOptions DefaultOptions() =>
        new()
        {
            Enabled = true,
            BatchSize = 5000,
            DeletedCommentScreenshotDays = 30,
            UploadOrphanGraceHours = 48,
            ScreenshotPurgeDryRun = false,
        };

    private static async Task<(Guid WorkspaceId, int ProjectId)> SeedTenantAsync(AppDbContext db)
    {
        var workspaceId = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = workspaceId,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();

        var project = new Project
        {
            Key = $"proj-{Guid.NewGuid():N}",
            Name = "Proj",
            OwnerId = workspaceId,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        return (workspaceId, project.Id);
    }

    private static async Task<int> AddCommentAsync(
        AppDbContext db,
        int projectId,
        Guid ownerId,
        string? screenshotUrl,
        DateTime? deletedAt,
        DateTime? createdAt = null,
        DateTime? screenshotPurgedAt = null
    )
    {
        var comment = new Comment
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "b",
            OwnerId = ownerId,
            Element = new ElementCapture { ScreenshotUrl = screenshotUrl },
            DeletedAt = deletedAt,
            DeletedBy = deletedAt != null ? Guid.NewGuid() : null,
            ScreenshotPurgedAt = screenshotPurgedAt,
        };
        if (createdAt.HasValue)
        {
            comment.CreatedAt = createdAt.Value;
            db.PreserveCreatedAtOnInsert(comment);
        }
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    // ───────────────────────────── Step A: PurgeDeletedAsync ─────────────────────────────

    [Fact]
    public async Task PurgeDeleted_AfterGrace_DeletesFile_AndStamps()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var url = signer.SignedUrl(rel);
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(12345, DateTime.UtcNow.AddDays(-40));

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            url,
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(new[] { rel }, recorder.Deleted);
        Assert.Equal(1, result.CommentsPurged);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(12345, result.BytesDeleted);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.NotNull(row.ScreenshotPurgedAt);
        Assert.Equal(url, row.Element.ScreenshotUrl);
    }

    [Fact]
    public async Task PurgeDeleted_InsideGrace_Untouched()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(10, DateTime.UtcNow);

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-29)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Empty(recorder.Deleted);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeleted_LiveComment_NeverTouched()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(10, DateTime.UtcNow.AddDays(-400));

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: null,
            createdAt: DateTime.UtcNow.AddDays(-400)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Empty(recorder.Deleted);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeleted_MissingFile_StampsWithoutCounting()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var recorder = new RecordingFileStorage(); // no entry — file already absent

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.CommentsPurged);
        Assert.Equal(0, result.FilesDeleted);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.NotNull(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeleted_IsIdempotent()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(10, DateTime.UtcNow.AddDays(-40));

        await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var first = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(1, first.CommentsPurged);

        using var db2 = testDb.MakeContext();
        var second = await ScreenshotPurge.PurgeDeletedAsync(
            db2,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, second.CommentsPurged);
        Assert.Equal(0, second.FilesDeleted);
        Assert.Equal(0, second.Failures);
        Assert.Equal(0, second.Skipped);
    }

    [Fact]
    public async Task PurgeDeleted_StorageStillHasFile_LeavesUnstamped()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.webp";
        var recorder = new RecordingFileStorage { FailDelete = true };
        recorder.Files[rel] = new RecordingFileStorage.Entry(10, DateTime.UtcNow.AddDays(-40));

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Failures);
        Assert.Equal(0, result.CommentsPurged);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task PurgeDeleted_UnrecognisedUrl_NeverDeleted_NeverStamped()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            "https://elsewhere.example/x.png",
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Skipped);
        Assert.Empty(recorder.Deleted);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task Purge_ForeignOwnerPath_NotDeleted()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db);
        var (ownerB, projectB) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var relA = $"uploads/{ownerA:N}/proj/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Files[relA] = new RecordingFileStorage.Entry(99, DateTime.UtcNow.AddDays(-40));

        // Comment lives in workspace B, but its (author-supplied) URL names A's folder.
        var commentId = await AddCommentAsync(
            db,
            projectB,
            ownerB,
            signer.SignedUrl(relA),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var logger = new ListLogger();
        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            logger,
            CancellationToken.None
        );

        Assert.Empty(recorder.Deleted);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.CommentsPurged);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains(commentId.ToString())
        );

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    [Fact]
    public async Task Purge_GlobalPath_OnlyForLegacyRows()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var relLegacy = $"uploads/global/p/{Guid.NewGuid():N}.png";
        var relNew = $"uploads/global/p/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Files[relLegacy] = new RecordingFileStorage.Entry(5, DateTime.UtcNow.AddDays(-60));
        recorder.Files[relNew] = new RecordingFileStorage.Entry(5, DateTime.UtcNow.AddDays(-60));

        var legacyId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(relLegacy),
            deletedAt: DateTime.UtcNow.AddDays(-31),
            createdAt: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        var newId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(relNew),
            deletedAt: DateTime.UtcNow.AddDays(-31),
            createdAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Contains(relLegacy, recorder.Deleted);
        Assert.DoesNotContain(relNew, recorder.Deleted);
        Assert.Equal(1, result.CommentsPurged);
        Assert.Equal(1, result.Skipped);

        using var verify = testDb.MakeContext();
        Assert.NotNull(
            (await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == legacyId)).ScreenshotPurgedAt
        );
        Assert.Null(
            (await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == newId)).ScreenshotPurgedAt
        );
    }

    [Fact]
    public async Task PurgeDeleted_DryRun_DeletesNothing_StampsNothing_Counts()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(42, DateTime.UtcNow.AddDays(-40));

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var options = DefaultOptions() with { ScreenshotPurgeDryRun = true };
        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            options,
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.WouldDelete);
        Assert.Equal(42, result.WouldBytes);
        Assert.Equal(0, result.CommentsPurged);
        Assert.Empty(recorder.Deleted);

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.Null(row.ScreenshotPurgedAt);
    }

    /// <summary>DB-16 review fix #10 (NIT): the dry-run log's file(s) count must reflect only rows
    /// whose file actually has bytes (size &gt; 0) — a row whose file is already missing (size 0)
    /// would-purge as a COMMENT but is not a FILE, matching the real (non-dry-run) branch's
    /// filesDeleted semantics, which only increments on size &gt; 0.</summary>
    [Fact]
    public async Task PurgeDeleted_DryRun_LogsFileCountSeparatelyFromRowCount()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();

        var relWithBytes = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var relMissing = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png"; // never added to recorder.Files
        var recorder = new RecordingFileStorage();
        recorder.Files[relWithBytes] = new RecordingFileStorage.Entry(42, DateTime.UtcNow.AddDays(-40));

        await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(relWithBytes),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );
        await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(relMissing),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        var logger = new ListLogger();
        var options = DefaultOptions() with { ScreenshotPurgeDryRun = true };
        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            options,
            DateTime.UtcNow,
            logger,
            CancellationToken.None
        );

        // Both rows "would purge" (WouldDelete counts rows), but only one names a file with bytes.
        Assert.Equal(2, result.WouldDelete);
        var infoEntry = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("DRY RUN")
        );
        Assert.Contains("2 comment(s)", infoEntry.Message);
        Assert.Contains("1 file(s)", infoEntry.Message);
    }

    [Fact]
    public async Task PurgeDeleted_DoesNotTouchUpdatedBy()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Files[rel] = new RecordingFileStorage.Entry(10, DateTime.UtcNow.AddDays(-40));

        var commentId = await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-31)
        );

        Guid? updatedByBefore;
        DateTime? updatedAtBefore;
        using (var check0 = testDb.MakeContext())
        {
            var row0 = await check0.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
            updatedByBefore = row0.UpdatedBy;
            updatedAtBefore = row0.UpdatedAt;
        }

        await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        using var verify = testDb.MakeContext();
        var row = await verify.Comments.IgnoreQueryFilters().SingleAsync(c => c.Id == commentId);
        Assert.NotNull(row.ScreenshotPurgedAt);
        Assert.Equal(updatedByBefore, row.UpdatedBy);
        Assert.Equal(updatedAtBefore, row.UpdatedAt);
    }

    [Fact]
    public async Task PurgeDeleted_PeriodZero_Skips()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        var options = DefaultOptions() with { DeletedCommentScreenshotDays = 0 };

        var result = await ScreenshotPurge.PurgeDeletedAsync(
            db,
            recorder,
            signer,
            options,
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, result.CommentsPurged);
        Assert.Empty(recorder.Deleted);
    }

    [Fact]
    public async Task PurgeDeleted_BatchSizeZero_Throws()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        var options = DefaultOptions() with { BatchSize = 0 };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () =>
                ScreenshotPurge.PurgeDeletedAsync(
                    db,
                    recorder,
                    signer,
                    options,
                    DateTime.UtcNow,
                    NullLogger.Instance,
                    CancellationToken.None
                )
        );
    }

    // ───────────────────────────── Step B: OrphanSweepAsync ─────────────────────────────

    [Fact]
    public async Task OrphanSweep_DeletesUnreferencedOldFile_KeepsReferenced_KeepsYoung()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();

        var relA = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var relB = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var relC = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";

        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));
        recorder.Files[relA] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));
        recorder.Files[relB] = new RecordingFileStorage.Entry(2, DateTime.UtcNow.AddDays(-3));
        recorder.Files[relC] = new RecordingFileStorage.Entry(3, DateTime.UtcNow.AddHours(-1));

        await AddCommentAsync(db, projectId, ownerId, signer.SignedUrl(relA), deletedAt: null);

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Orphans);
        Assert.Equal(1, result.Young);
        Assert.Equal(3, result.Files);
        Assert.Contains(relB, recorder.Deleted);
        Assert.DoesNotContain(relA, recorder.Deleted);
        Assert.DoesNotContain(relC, recorder.Deleted);
    }

    [Fact]
    public async Task OrphanSweep_SoftDeletedInsideGrace_StillProtectsFile()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));
        recorder.Files[rel] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-1)
        );

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, result.Orphans);
        Assert.Empty(recorder.Deleted);
    }

    [Fact]
    public async Task OrphanSweep_StampedPurgedRow_NoLongerProtects()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/proj/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));
        recorder.Files[rel] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        await AddCommentAsync(
            db,
            projectId,
            ownerId,
            signer.SignedUrl(rel),
            deletedAt: DateTime.UtcNow.AddDays(-40),
            screenshotPurgedAt: DateTime.UtcNow.AddDays(-39)
        );

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Orphans);
        Assert.Contains(rel, recorder.Deleted);
    }

    [Fact]
    public async Task OrphanSweep_ProcessesOnlyThatOwnersFolder_TenantIsolation()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, projectA) = await SeedTenantAsync(db);
        var (ownerB, _) = await SeedTenantAsync(db);
        var signer = RealSigner();

        var relAx = $"uploads/{ownerA:N}/p/{Guid.NewGuid():N}.png";
        var relBOld = $"uploads/{ownerB:N}/p/{Guid.NewGuid():N}.png";

        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerA.ToString("N"));
        recorder.Segments.Add(ownerB.ToString("N"));
        recorder.Files[relAx] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));
        recorder.Files[relBOld] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        await AddCommentAsync(db, projectA, ownerA, signer.SignedUrl(relAx), deletedAt: null);

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Contains(relBOld, recorder.Deleted);
        Assert.DoesNotContain(relAx, recorder.Deleted);
        Assert.Equal(
            new[] { ownerA.ToString("N"), ownerB.ToString("N") },
            recorder.ListedSegments
        );
        Assert.Equal(2, result.Segments);
    }

    /// <summary>
    /// DB-16 review fix #3 (MEDIUM — orchestrator 2026-09-23: protect by path across owners). The
    /// OLD per-folder design built each owner's reference set from `WHERE OwnerId == ownerA` only,
    /// so B's forged reference to A's file was invisible to A's set and the file was deleted as an
    /// "orphan" — exactly backwards: a forged/foreign reference must never make the sweep MORE
    /// aggressive against the real owner's file. The safer rule protects the path regardless of
    /// which row's OwnerId names it, and logs the mismatch instead of silently deleting.
    /// </summary>
    [Fact]
    public async Task OrphanSweep_ForgedForeignUrl_ProtectsAcrossOwners_LogsMismatch()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, _) = await SeedTenantAsync(db);
        var (ownerB, projectB) = await SeedTenantAsync(db);
        var signer = RealSigner();

        var relAOld = $"uploads/{ownerA:N}/p/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerA.ToString("N"));
        recorder.Segments.Add(ownerB.ToString("N"));
        recorder.Files[relAOld] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        // B's live comment forges a URL under A's folder.
        await AddCommentAsync(db, projectB, ownerB, signer.SignedUrl(relAOld), deletedAt: null);

        var logger = new ListLogger();
        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            logger,
            CancellationToken.None
        );

        // The path is protected (by any row, regardless of OwnerId) — never deleted.
        Assert.DoesNotContain(relAOld, recorder.Deleted);
        Assert.Equal(0, result.Orphans);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("outside their own row's OwnerId folder")
        );
    }

    [Fact]
    public async Task OrphanSweep_DryRun_DeletesNothing()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, _) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var rel = $"uploads/{ownerId:N}/p/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));
        recorder.Files[rel] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        var options = DefaultOptions() with { ScreenshotPurgeDryRun = true };
        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            options,
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Orphans);
        Assert.True(result.DryRun);
        Assert.Empty(recorder.Deleted);
    }

    [Fact]
    public async Task OrphanSweep_GlobalSegment_SkippedWhenFolderAbsent()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, _) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N")); // no "global" entry — folder absent on disk

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(1, result.Segments);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public async Task OrphanSweep_GlobalSegment_DecodesPercentEncodedUrls()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var relGlobal = $"uploads/global/p/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add("global");
        recorder.Files[relGlobal] = new RecordingFileStorage.Entry(7, DateTime.UtcNow.AddDays(-10));

        var signedUrl = signer.SignedUrl(relGlobal);
        Assert.Contains("uploads%2Fglobal%2F", signedUrl);
        await AddCommentAsync(db, projectId, ownerId, signedUrl, deletedAt: null);

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.DoesNotContain(relGlobal, recorder.Deleted);
        Assert.Equal(0, result.Orphans);
    }

    [Fact]
    public async Task OrphanSweep_SkipsBrandingAndUnknownSegments()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, _) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add("branding");
        recorder.Segments.Add("not-a-guid");
        recorder.Segments.Add(ownerId.ToString("N"));
        recorder.Segments.Add("global");
        recorder.Files["uploads/branding/logo.png"] = new RecordingFileStorage.Entry(
            1,
            DateTime.UtcNow.AddDays(-10)
        );
        recorder.Files["uploads/not-a-guid/x.png"] = new RecordingFileStorage.Entry(
            1,
            DateTime.UtcNow.AddDays(-10)
        );

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(2, result.Skipped);
        Assert.DoesNotContain("branding", recorder.ListedSegments);
        Assert.DoesNotContain("not-a-guid", recorder.ListedSegments);
    }

    /// <summary>
    /// DB-16 re-review (LOW): a non-canonical disk file — one LocalFileStorage.SaveAsync could never
    /// have written (wrong extension, no 32-hex file segment, etc.) — is never a real reference
    /// match (protectedPaths only ever holds canonical decoded paths) and must not be swept as an
    /// orphan either, even though it is old enough and its owner segment is a real workspace folder.
    /// It is skipped and counted separately (SkippedNonCanonical), not folded into Orphans/Failures.
    /// </summary>
    [Fact]
    public async Task OrphanSweep_NonCanonicalDiskFile_SkippedNotDeleted_CountedSeparately()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, _) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));
        var nonCanonical = $"uploads/{ownerId:N}/proj/readme.txt";
        recorder.Files[nonCanonical] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.DoesNotContain(nonCanonical, recorder.Deleted);
        Assert.Equal(0, result.Orphans);
        Assert.Equal(0, result.Failures);
        Assert.Equal(1, result.SkippedNonCanonical);
    }

    [Fact]
    public async Task OrphanSweep_GlobalSegment_UsesReferencedGlobalPaths()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, projectId) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var relProtected = $"uploads/global/p/{Guid.NewGuid():N}.png";
        var relOrphan = $"uploads/global/p/{Guid.NewGuid():N}.png";
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add("global");
        recorder.Files[relProtected] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));
        recorder.Files[relOrphan] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        await AddCommentAsync(db, projectId, ownerId, signer.SignedUrl(relProtected), deletedAt: null);

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.DoesNotContain(relProtected, recorder.Deleted);
        Assert.Contains(relOrphan, recorder.Deleted);
        Assert.Equal(1, result.Orphans);
    }

    /// <summary>
    /// Wiring proof: OrphanSweepAsync calls DeleteEmptyProjectFoldersAsync once per processed
    /// segment in real mode, and skips it entirely in dry-run. The actual empty/age semantics of
    /// the folder removal are proven on real disk by
    /// LocalFileStorageTests.DeleteEmptyProjectFoldersAsync_RemovesEmptyOldFolder_KeepsRecentAndNonEmpty.
    /// </summary>
    [Fact]
    public async Task OrphanSweep_RemovesEmptyOldProjectFolder_KeepsRecentAndNonEmpty()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerId, _) = await SeedTenantAsync(db);
        var signer = RealSigner();
        var recorder = new RecordingFileStorage();
        recorder.Segments.Add(ownerId.ToString("N"));

        await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Contains(ownerId.ToString("N"), recorder.EmptyFolderCalls);

        recorder.EmptyFolderCalls.Clear();
        using var db2 = testDb.MakeContext();
        var dryOptions = DefaultOptions() with { ScreenshotPurgeDryRun = true };
        await ScreenshotPurge.OrphanSweepAsync(
            db2,
            recorder,
            signer,
            dryOptions,
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Empty(recorder.EmptyFolderCalls);
    }

    /// <summary>
    /// DB-16 review fix #4 (MEDIUM): one owner folder that fails mid-enumeration (a broken symlink,
    /// a permissions error) must not abort the whole sweep — the OTHER owner's folder is still
    /// processed, and the broken one is counted as a failure rather than crashing the pass.
    /// </summary>
    [Fact]
    public async Task OrphanSweep_OneFolderThrowsMidEnumeration_OtherFolderStillProcessed()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (ownerA, _) = await SeedTenantAsync(db);
        var (ownerB, projectB) = await SeedTenantAsync(db);
        var signer = RealSigner();

        var relABroken1 = $"uploads/{ownerA:N}/proj/{Guid.NewGuid():N}.png";
        var relABroken2 = $"uploads/{ownerA:N}/proj/{Guid.NewGuid():N}.png";
        var relBOrphan = $"uploads/{ownerB:N}/proj/{Guid.NewGuid():N}.png";

        var recorder = new RecordingFileStorage { ThrowForSegment = ownerA.ToString("N") };
        recorder.Segments.Add(ownerA.ToString("N"));
        recorder.Segments.Add(ownerB.ToString("N"));
        recorder.Files[relABroken1] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));
        recorder.Files[relABroken2] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));
        recorder.Files[relBOrphan] = new RecordingFileStorage.Entry(1, DateTime.UtcNow.AddDays(-10));

        // ownerB's file is a genuine orphan (nothing references it) and must still be swept even
        // though ownerA's folder blew up.
        _ = projectB;

        var result = await ScreenshotPurge.OrphanSweepAsync(
            db,
            recorder,
            signer,
            DefaultOptions(),
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Contains(relBOrphan, recorder.Deleted);
        Assert.Equal(1, result.Orphans);
        Assert.True(result.Failures >= 1);
        // Both segments were attempted (segmentsProcessed counts entry into the loop body, not
        // successful completion).
        Assert.Equal(2, result.Segments);
    }
}
