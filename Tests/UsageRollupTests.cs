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
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-15: the usage_daily rollup (UsageRollup.RollupAsync), driven directly against a Sqlite-backed
/// AppDbContext — same fixture shape as RetentionServiceTests. Idempotency, window/backfill
/// behaviour, and the step-0 argument validation that Sweep_SkipsUsageDeleteWhenRollupFails
/// relies on.
/// </summary>
public class UsageRollupTests
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

    private static async Task<Guid> SeedWorkspaceAsync(AppDbContext db)
    {
        var id = Guid.NewGuid();
        db.Workspaces.Add(
            new Workspace
            {
                Id = id,
                Name = "Workspace",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();
        return id;
    }

    private static RetentionOptions Options(int rollupDays = 3, int usageEventsDays = 4) =>
        new()
        {
            Enabled = true,
            UsageEventsDays = usageEventsDays,
            RollupDays = rollupDays,
            BatchSize = 5000,
        };

    private static async Task<List<UsageDaily>> RowsAsync(TestDb testDb) =>
        await testDb
            .MakeContext()
            .UsageDaily.IgnoreQueryFilters()
            .OrderBy(r => r.Day)
            .ThenBy(r => r.OwnerId)
            .ThenBy(r => r.Type)
            .ToListAsync();

    [Fact]
    public async Task Rollup_GroupsByDayOwnerType()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;
        var day = now.AddDays(-2);

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            UsageEvent Ev(string type, Guid? ownerId) =>
                new()
                {
                    Type = type,
                    Source = "test",
                    OwnerId = ownerId,
                    CreatedAt = day,
                };
            db.UsageEvents.AddRange(
                Ev("installed", owner),
                Ev("installed", owner),
                Ev("installed", owner),
                Ev("doctor_run", owner),
                Ev("doctor_run", owner),
                Ev("installed", null)
            );
            await db.SaveChangesAsync();

            await UsageRollup.RollupAsync(
                db,
                Options(),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        var rows = await RowsAsync(testDb);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(DateOnly.FromDateTime(day), r.Day));
        Assert.Equal(3, rows.Single(r => r.OwnerId != null && r.Type == "installed").Count);
        Assert.Equal(2, rows.Single(r => r.OwnerId != null && r.Type == "doctor_run").Count);
        Assert.Equal(1, rows.Single(r => r.OwnerId == null && r.Type == "installed").Count);
    }

    [Fact]
    public async Task Rollup_IsIdempotent()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;
        var day = now.AddDays(-2);

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            db.UsageEvents.AddRange(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = day,
                },
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = day,
                }
            );
            await db.SaveChangesAsync();

            await UsageRollup.RollupAsync(
                db,
                Options(),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        var afterFirst = await RowsAsync(testDb);

        using (var db = testDb.MakeContext())
        {
            // Second run over the same data — days with no events never produce a usage_daily row,
            // so the backfill set is never strictly empty; what matters is the row outcome below.
            await UsageRollup.RollupAsync(
                db,
                Options(),
                now.AddMinutes(5),
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        var afterSecond = await RowsAsync(testDb);
        var first = Assert.Single(afterFirst);
        var second = Assert.Single(afterSecond);
        Assert.Equal(first.Day, second.Day);
        Assert.Equal(first.OwnerId, second.OwnerId);
        Assert.Equal(first.Type, second.Type);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(2, second.Count);
        Assert.True(second.ComputedAt > first.ComputedAt, "a re-run advances ComputedAt");
    }

    [Fact]
    public async Task Rollup_RecomputesWindow_UpdatesCount()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;
        var day = now.AddDays(-2); // inside the RollupDays=3 recompute window

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = day,
                }
            );
            await db.SaveChangesAsync();
            await UsageRollup.RollupAsync(
                db,
                Options(),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        Assert.Equal(1, (await RowsAsync(testDb)).Single().Count);

        using (var db = testDb.MakeContext())
        {
            var owner = db.Workspaces.IgnoreQueryFilters().Single().Id;
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = day,
                }
            );
            await db.SaveChangesAsync();
            await UsageRollup.RollupAsync(
                db,
                Options(),
                now.AddMinutes(5),
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        Assert.Equal(2, (await RowsAsync(testDb)).Single().Count);
    }

    [Fact]
    public async Task Rollup_NeverTouchesToday()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = now.AddHours(-1),
                }
            );
            await db.SaveChangesAsync();

            // The whole window is scheduled (first run backfills), but every scheduled day is
            // yesterday-or-older: today's event must NOT be rolled into any row.
            await UsageRollup.RollupAsync(
                db,
                Options(),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        Assert.Empty(await RowsAsync(testDb));
    }

    [Fact]
    public async Task Rollup_FirstRun_BackfillsWholeWindow()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;
        var old = now.AddDays(-100); // outside RollupDays, inside the 180-day retention window

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = old,
                }
            );
            await db.SaveChangesAsync();

            await UsageRollup.RollupAsync(
                db,
                Options(usageEventsDays: 180),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        var row = Assert.Single(await RowsAsync(testDb));
        Assert.Equal(DateOnly.FromDateTime(old), row.Day);
        Assert.Equal(1, row.Count);
    }

    [Fact]
    public async Task Rollup_NegativeRollupDays_Throws()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;

        using (var db = testDb.MakeContext())
        {
            var owner = await SeedWorkspaceAsync(db);
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = owner,
                    CreatedAt = now.AddDays(-2),
                }
            );
            await db.SaveChangesAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                UsageRollup.RollupAsync(
                    db,
                    Options(rollupDays: -1),
                    now,
                    NullLogger.Instance,
                    CancellationToken.None
                )
            );
        }

        // Before any query: nothing was written despite the seeded events.
        Assert.Empty(await RowsAsync(testDb));
    }

    [Fact]
    public async Task Rollup_ZeroRollupDays_OnlyBackfillsMissingDays()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;
        var yesterday = now.AddDays(-1);

        using (var db = testDb.MakeContext())
        {
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = null,
                    CreatedAt = yesterday,
                }
            );
            db.UsageEvents.Add(
                new UsageEvent
                {
                    Type = "installed",
                    Source = "test",
                    OwnerId = null,
                    CreatedAt = now.AddHours(-1),
                }
            );
            await db.SaveChangesAsync();

            await UsageRollup.RollupAsync(
                db,
                Options(rollupDays: 0),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
        }

        var rows = await RowsAsync(testDb);
        var row = Assert.Single(rows);
        Assert.Equal(DateOnly.FromDateTime(yesterday), row.Day); // backfill rolled yesterday…
        Assert.Equal(1, row.Count); // …but today's event was NOT rolled into it
    }

    /// <summary>Review finding #4/#6: an eventless window must not be rescheduled — and
    /// re-scanned — forever. The high-water mark (app_settings key) advances past the
    /// always-recomputed recent window even when nothing was written, so the next pass's
    /// backfill starts from there instead of a DISTINCT-day scan of the whole retention window.</summary>
    [Fact]
    public async Task Rollup_EventlessWindow_AdvancesHighWaterMark_NotRescheduledForever()
    {
        using var testDb = new TestDb();
        var now = DateTime.UtcNow;

        using (var db = testDb.MakeContext())
        {
            await SeedWorkspaceAsync(db); // seeded, but zero usage_events
            var touched = await UsageRollup.RollupAsync(
                db,
                Options(rollupDays: 3, usageEventsDays: 30),
                now,
                NullLogger.Instance,
                CancellationToken.None
            );
            Assert.Equal(0, touched); // nothing to upsert — no usage_daily row for an eventless day
        }

        Assert.Empty(await RowsAsync(testDb));

        string? mark;
        using (var db = testDb.MakeContext())
        {
            mark = await db
                .AppSettings.Where(s => s.Key == UsageRollup.HighWaterMarkKey)
                .Select(s => s.Value)
                .SingleOrDefaultAsync();
        }
        var expected = DateOnly.FromDateTime(now).AddDays(-4); // today - RollupDays(3) - 1
        Assert.Equal(expected.ToString("yyyy-MM-dd"), mark);

        // Second pass, a day later: still nothing to touch, mark advances (never regresses), no
        // exception from re-scanning the whole 30-day retention window every time.
        using (var db = testDb.MakeContext())
        {
            var touched = await UsageRollup.RollupAsync(
                db,
                Options(rollupDays: 3, usageEventsDays: 30),
                now.AddDays(1),
                NullLogger.Instance,
                CancellationToken.None
            );
            Assert.Equal(0, touched);
        }

        using (var db = testDb.MakeContext())
        {
            var markAfter = await db
                .AppSettings.Where(s => s.Key == UsageRollup.HighWaterMarkKey)
                .Select(s => s.Value)
                .SingleOrDefaultAsync();
            Assert.Equal(expected.AddDays(1).ToString("yyyy-MM-dd"), markAfter);
        }
    }

    [Fact]
    public void BindOptions_ReadsRollupDaysFromConfiguration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new System.Collections.Generic.Dictionary<string, string?>
                {
                    ["Retention:RollupDays"] = "7",
                }
            )
            .Build();

        var options = RetentionService.BindOptions(config);

        Assert.Equal(7, options.RollupDays);
        // The bind is field-by-field: every sibling keeps its default when unset.
        Assert.Equal(180, options.UsageEventsDays);
        Assert.Equal(5000, options.BatchSize);
    }
}
