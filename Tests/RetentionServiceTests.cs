using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-08: the daily retention sweep (usage_events / read notifications / orphaned
/// page_context_snapshots / dead invites). Drives RetentionService.SweepOnceAsync directly against a
/// Sqlite-backed AppDbContext — ExecuteDeleteAsync is relational-only (InMemory doesn't support it),
/// so this follows UsageEventFirstCommentTests' fixture rather than CommentFieldsTests'.
/// </summary>
public class RetentionServiceTests
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

    /// <summary>
    /// Shared-cache Sqlite in-memory database; each context opens its OWN connection to it (see
    /// UsageEventFirstCommentTests.TestDb for the full rationale — a shared connection object is not
    /// thread-safe and this fixture wants fresh, untracked reads for verification anyway).
    /// </summary>
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
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
            );

        public void Dispose() => _keepAlive.Dispose();
    }

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

    private static RetentionOptions DefaultOptions() =>
        new()
        {
            Enabled = true,
            UsageEventsDays = 180,
            NotificationsReadDays = 90,
            PageContextSnapshotDays = 30,
            InvitesDays = 90,
            BatchSize = 5000,
        };

    [Fact]
    public async Task Sweep_KeepsAllFiveOneShotFacts()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();

        var old = DateTime.UtcNow.AddDays(-200);
        var recent = DateTime.UtcNow.AddDays(-1);
        // ProjectId left null — DB-06 made it a real (nullable) FK to projects, and this test only
        // exercises the age/type predicate, not project association.
        var oneShotFacts = new[]
        {
            "demo_started",
            "workspace_converted",
            "widget_installed",
            "first_comment",
            "first_apply",
        };
        var events = oneShotFacts
            .Select(t => new UsageEvent
            {
                ProjectId = null,
                Type = t,
                Source = "test",
                CreatedAt = old,
            })
            .ToList();
        events.Add(
            new UsageEvent
            {
                ProjectId = null,
                Type = "x",
                Source = "test",
                CreatedAt = old,
            }
        );
        events.Add(
            new UsageEvent
            {
                ProjectId = null,
                Type = "x",
                Source = "test",
                CreatedAt = recent,
            }
        );
        db.UsageEvents.AddRange(events);
        await db.SaveChangesAsync();

        var result = await RetentionService.SweepOnceAsync(
            db,
            DefaultOptions(),
            NullLogger.Instance,
            CancellationToken.None
        );
        // Only the old volume row was deleted; all five funnel facts survive the sweep forever.
        Assert.Equal(1, result.UsageEventsDeleted);

        using var verify = testDb.MakeContext();
        var remainingTypes = await verify
            .UsageEvents.IgnoreQueryFilters()
            .Select(e => e.Type)
            .OrderBy(t => t)
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                "demo_started",
                "first_apply",
                "first_comment",
                "widget_installed",
                "workspace_converted",
                "x",
            },
            remainingTypes
        );
    }

    [Fact]
    public async Task Sweep_RollsUpBeforeDeleting()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, _) = await SeedTenantAsync(db);

        // A volume event old enough to be swept, inside the rollup window: the rollup must run
        // FIRST, so its count survives in usage_daily after the raw row is deleted.
        var day = DateTime.UtcNow.AddDays(-2);
        db.UsageEvents.AddRange(
            new UsageEvent
            {
                ProjectId = null,
                Type = "installed",
                Source = "test",
                OwnerId = workspaceId,
                CreatedAt = day,
            },
            new UsageEvent
            {
                ProjectId = null,
                Type = "installed",
                Source = "test",
                OwnerId = workspaceId,
                CreatedAt = day,
            }
        );
        await db.SaveChangesAsync();

        var options = DefaultOptions() with { UsageEventsDays = 1 };
        var result = await RetentionService.SweepOnceAsync(
            db,
            options,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(2, result.UsageEventsDeleted);

        using var verify = testDb.MakeContext();
        Assert.Equal(0, await verify.UsageEvents.IgnoreQueryFilters().CountAsync());
        var row = Assert.Single(await verify.UsageDaily.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(DateOnly.FromDateTime(day), row.Day);
        Assert.Equal(workspaceId, row.OwnerId);
        Assert.Equal("installed", row.Type);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public async Task Sweep_SkipsUsageDeleteWhenRollupFails()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var old = DateTime.UtcNow.AddDays(-200);
        db.UsageEvents.Add(
            new UsageEvent
            {
                ProjectId = projectId,
                Type = "x",
                Source = "test",
                OwnerId = workspaceId,
                CreatedAt = old,
            }
        );
        db.Notifications.Add(
            new Notification
            {
                OwnerId = workspaceId,
                UserId = Guid.NewGuid(),
                Type = NotificationType.CommentApplied,
                ProjectId = projectId,
                ReadAt = old,
            }
        );
        await db.SaveChangesAsync();

        // RollupDays = -1 makes the rollup throw before any query — the usage_events sweep must be
        // skipped (no un-rolled row lost) while the other three sweeps still run.
        var options = DefaultOptions() with
        {
            RollupDays = -1,
        };
        var result = await RetentionService.SweepOnceAsync(
            db,
            options,
            NullLogger.Instance,
            CancellationToken.None
        );

        Assert.Equal(0, result.UsageEventsDeleted);
        Assert.Equal(1, result.NotificationsDeleted);

        using var verify = testDb.MakeContext();
        Assert.Equal(1, await verify.UsageEvents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await verify.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Notifications_ReadLongAgo_Deleted_UnreadKept()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var oldRead = new Notification
        {
            OwnerId = workspaceId,
            UserId = Guid.NewGuid(),
            Type = NotificationType.CommentApplied,
            ProjectId = projectId,
            ReadAt = DateTime.UtcNow.AddDays(-100),
        };
        var oldUnread = new Notification
        {
            OwnerId = workspaceId,
            UserId = Guid.NewGuid(),
            Type = NotificationType.CommentApplied,
            ProjectId = projectId,
            ReadAt = null,
        };
        var recentRead = new Notification
        {
            OwnerId = workspaceId,
            UserId = Guid.NewGuid(),
            Type = NotificationType.CommentApplied,
            ProjectId = projectId,
            ReadAt = DateTime.UtcNow.AddDays(-1),
        };
        db.Notifications.AddRange(oldRead, oldUnread, recentRead);
        await db.SaveChangesAsync();

        var result = await RetentionService.SweepOnceAsync(
            db,
            DefaultOptions(),
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(1, result.NotificationsDeleted);

        using var verify = testDb.MakeContext();
        var remainingIds = await verify
            .Notifications.IgnoreQueryFilters()
            .Select(n => n.Id)
            .ToListAsync();
        Assert.DoesNotContain(oldRead.Id, remainingIds);
        Assert.Contains(oldUnread.Id, remainingIds);
        Assert.Contains(recentRead.Id, remainingIds);
    }

    [Fact]
    public async Task Snapshots_ReferencedByLiveComment_Kept()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var old = DateTime.UtcNow.AddDays(-60);
        var liveSnap = new PageContextSnapshot
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Route = "/a",
            SessionId = "s1",
            OwnerId = workspaceId,
            LastEventAt = old,
        };
        var deadSnap = new PageContextSnapshot
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Route = "/b",
            SessionId = "s2",
            OwnerId = workspaceId,
            LastEventAt = old,
        };
        db.PageContextSnapshots.AddRange(liveSnap, deadSnap);
        await db.SaveChangesAsync();

        var liveComment = new Comment
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "live",
            OwnerId = workspaceId,
            PageContextSnapshotId = liveSnap.Id,
        };
        var deletedComment = new Comment
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = Guid.NewGuid(),
            Body = "gone",
            OwnerId = workspaceId,
            PageContextSnapshotId = deadSnap.Id,
            DeletedAt = DateTime.UtcNow,
            DeletedBy = Guid.NewGuid(),
        };
        db.Comments.AddRange(liveComment, deletedComment);
        await db.SaveChangesAsync();

        var result = await RetentionService.SweepOnceAsync(
            db,
            DefaultOptions(),
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(1, result.SnapshotsDeleted);

        using var verify = testDb.MakeContext();
        var remainingSnapIds = await verify
            .PageContextSnapshots.IgnoreQueryFilters()
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Contains(liveSnap.Id, remainingSnapIds);
        Assert.DoesNotContain(deadSnap.Id, remainingSnapIds);

        var reloadedDeletedComment = await verify
            .Comments.IgnoreQueryFilters()
            .FirstAsync(c => c.Id == deletedComment.Id);
        Assert.Null(reloadedDeletedComment.PageContextSnapshotId);
    }

    [Fact]
    public async Task Invites_DeadAndUnused_Deleted_UsedOrLinkedOrFreshKept()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var longAgo = DateTime.UtcNow.AddDays(-100); // older than the 90-day cutoff
        var yesterday = DateTime.UtcNow.AddDays(-1);

        var expiredUnused = new Invite
        {
            OwnerId = workspaceId,
            Code = "code-1",
            ExpiresAt = longAgo,
            Uses = 0,
        };
        var revokedUnused = new Invite
        {
            OwnerId = workspaceId,
            Code = "code-2",
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            Uses = 0,
            RevokedAt = longAgo,
        };
        var expiredUsed = new Invite
        {
            OwnerId = workspaceId,
            Code = "code-3",
            ExpiresAt = longAgo,
            Uses = 1,
        };
        var expiredLinked = new Invite
        {
            OwnerId = workspaceId,
            Code = "code-4",
            ExpiresAt = longAgo,
            Uses = 0,
        };
        var freshExpired = new Invite
        {
            OwnerId = workspaceId,
            Code = "code-5",
            ExpiresAt = yesterday,
            Uses = 0,
        };
        db.Invites.AddRange(expiredUnused, revokedUnused, expiredUsed, expiredLinked, freshExpired);
        await db.SaveChangesAsync();

        db.QuickAccessLinks.Add(
            new QuickAccessLink
            {
                OwnerId = workspaceId,
                UserId = Guid.NewGuid(),
                ProjectId = projectId,
                InviteId = expiredLinked.Id,
                TokenHash = "hash",
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            }
        );
        await db.SaveChangesAsync();

        var result = await RetentionService.SweepOnceAsync(
            db,
            DefaultOptions(),
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(2, result.InvitesDeleted);

        using var verify = testDb.MakeContext();
        var remainingCodes = await verify
            .Invites.IgnoreQueryFilters()
            .Select(i => i.Code)
            .ToListAsync();
        Assert.DoesNotContain("code-1", remainingCodes);
        Assert.DoesNotContain("code-2", remainingCodes);
        Assert.Contains("code-3", remainingCodes);
        Assert.Contains("code-4", remainingCodes);
        Assert.Contains("code-5", remainingCodes);
    }

    [Fact]
    public async Task Sweep_Disabled_DeletesNothing()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var old = DateTime.UtcNow.AddDays(-200);
        db.UsageEvents.Add(
            new UsageEvent
            {
                ProjectId = projectId,
                Type = "x",
                Source = "test",
                CreatedAt = old,
            }
        );
        db.Notifications.Add(
            new Notification
            {
                OwnerId = workspaceId,
                UserId = Guid.NewGuid(),
                Type = NotificationType.CommentApplied,
                ProjectId = projectId,
                ReadAt = old,
            }
        );
        db.PageContextSnapshots.Add(
            new PageContextSnapshot
            {
                ProjectId = projectId,
                Environment = EnvironmentTag.Local,
                Route = "/a",
                SessionId = "s1",
                OwnerId = workspaceId,
                LastEventAt = old,
            }
        );
        db.Invites.Add(
            new Invite
            {
                OwnerId = workspaceId,
                Code = "code-x",
                ExpiresAt = old,
                Uses = 0,
            }
        );
        await db.SaveChangesAsync();

        var options = DefaultOptions() with { Enabled = false };
        var result = await RetentionService.SweepOnceAsync(
            db,
            options,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(new RetentionSweepResult(0, 0, 0, 0), result);

        using var verify = testDb.MakeContext();
        Assert.Equal(1, await verify.UsageEvents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await verify.Notifications.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await verify.PageContextSnapshots.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await verify.Invites.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Sweep_PeriodZero_SkipsThatTableOnly()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();
        var (workspaceId, projectId) = await SeedTenantAsync(db);

        var old = DateTime.UtcNow.AddDays(-200);
        db.UsageEvents.Add(
            new UsageEvent
            {
                ProjectId = projectId,
                Type = "x",
                Source = "test",
                CreatedAt = old,
            }
        );
        db.Notifications.Add(
            new Notification
            {
                OwnerId = workspaceId,
                UserId = Guid.NewGuid(),
                Type = NotificationType.CommentApplied,
                ProjectId = projectId,
                ReadAt = old,
            }
        );
        await db.SaveChangesAsync();

        var options = DefaultOptions() with { NotificationsReadDays = 0 };
        var result = await RetentionService.SweepOnceAsync(
            db,
            options,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(1, result.UsageEventsDeleted);
        Assert.Equal(0, result.NotificationsDeleted);

        using var verify = testDb.MakeContext();
        Assert.Equal(0, await verify.UsageEvents.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await verify.Notifications.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Sweep_Batches()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();

        var old = DateTime.UtcNow.AddDays(-200);
        // ProjectId left null — see UsageEvents_OlderThanCutoff_AreDeleted_ExceptFirstFacts.
        for (var i = 0; i < 5; i++)
        {
            db.UsageEvents.Add(
                new UsageEvent
                {
                    ProjectId = null,
                    Type = "x",
                    Source = "test",
                    CreatedAt = old,
                }
            );
        }
        await db.SaveChangesAsync();

        var options = DefaultOptions() with { BatchSize = 2 };
        var result = await RetentionService.SweepOnceAsync(
            db,
            options,
            NullLogger.Instance,
            CancellationToken.None
        );
        Assert.Equal(5, result.UsageEventsDeleted);

        using var verify = testDb.MakeContext();
        Assert.Equal(0, await verify.UsageEvents.IgnoreQueryFilters().CountAsync());
    }

    // ── DB-16: screenshot purge wiring ───────────────────────────────────────────────────────

    private sealed class FakeUploadSigner : IUploadSigner
    {
        public string SignedUrl(string relPath) => relPath;

        public bool Validate(string relPath, long exp, string sig) => true;

        public string ExtractRelPath(string stored) => stored;
    }

    /// <summary>Simulates a storage failure inside the screenshot-purge steps (DB-16 §3.5/§3.6:
    /// a storage error must never fail the four row sweeps).</summary>
    private sealed class ThrowingFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("uploads/x");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListOwnerSegmentsAsync() =>
            throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Sweep_RunsPurge_AfterRowSweeps_AndStorageFailureDoesNotFailRows()
    {
        using var testDb = new TestDb();
        using var db = testDb.MakeContext();

        var old = DateTime.UtcNow.AddDays(-200);
        db.UsageEvents.Add(
            new UsageEvent
            {
                ProjectId = null,
                Type = "x",
                Source = "test",
                CreatedAt = old,
            }
        );
        await db.SaveChangesAsync();

        var result = await RetentionService.SweepOnceAsync(
            db,
            DefaultOptions(),
            NullLogger.Instance,
            CancellationToken.None,
            new ThrowingFileStorage(),
            new FakeUploadSigner()
        );

        // The row sweeps still ran and returned their normal counts — a storage exception in the
        // screenshot-purge steps is caught and logged, never rethrown, never loses the row results.
        Assert.Equal(1, result.UsageEventsDeleted);
    }

    [Fact]
    public void BindOptions_ReadsDeletedCommentScreenshotDays_AndOrphanGrace()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Retention:DeletedCommentScreenshotDays"] = "7",
                    ["Retention:UploadOrphanGraceHours"] = "12",
                }
            )
            .Build();

        var options = RetentionService.BindOptions(config);

        Assert.Equal(7, options.DeletedCommentScreenshotDays);
        Assert.Equal(12, options.UploadOrphanGraceHours);
    }

    /// <summary>DB-16 review fix #9 (LOW): unset defaults to true (D16.6 — first release ships in
    /// dry-run) — must not accidentally default to false or start deleting on a fresh deploy.</summary>
    [Fact]
    public void BindOptions_ScreenshotPurgeDryRun_UnsetDefaultsTrue()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()
        ).Build();

        var options = RetentionService.BindOptions(config);

        Assert.True(options.ScreenshotPurgeDryRun);
    }

    [Fact]
    public void BindOptions_ScreenshotPurgeDryRun_ExplicitFalse_IsHonored()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Retention:ScreenshotPurgeDryRun"] = "false" }
            )
            .Build();

        var options = RetentionService.BindOptions(config);

        Assert.False(options.ScreenshotPurgeDryRun);
    }
}
