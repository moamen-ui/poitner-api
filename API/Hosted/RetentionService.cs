using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;

namespace Pointer.API.Hosted;

/// <summary>
/// Config section "Retention" — see appsettings.json / docker-compose.prod.yml / .env.prod.example
/// for the defaults and overrides. A `*Days` value of 0 (or negative) disables retention for that
/// table only; <see cref="Enabled"/> = false disables the whole sweep.
/// </summary>
public sealed record RetentionOptions
{
    public bool Enabled { get; init; } = true;
    public int UsageEventsDays { get; init; } = 180;

    /// <summary>DB-15: how many recent UTC days the usage_daily rollup recomputes each pass. The
    /// whole retention window is backfilled on the first run; 0 = backfill-only.</summary>
    public int RollupDays { get; init; } = 3;

    public int NotificationsReadDays { get; init; } = 90;
    public int PageContextSnapshotDays { get; init; } = 30;
    public int InvitesDays { get; init; } = 90;
    public int BatchSize { get; init; } = 5000;
    public int IntervalHours { get; init; } = 24;
    public int InitialDelayMinutes { get; init; } = 5;

    /// <summary>DB-16: days a comment must have been soft-deleted before its screenshot file is
    /// purged. 0 disables the purge (files then leave only with the workspace).</summary>
    public int DeletedCommentScreenshotDays { get; init; } = 30;

    /// <summary>DB-16: age (by file mtime) before an unreferenced upload is treated as an orphan
    /// and deleted. 0 disables the orphan sweep.</summary>
    public int UploadOrphanGraceHours { get; init; } = 48;

    /// <summary>DB-16 (D16.6): true = both screenshot-purge steps only log what they would delete.
    /// Ships true; flip to false after reading the first production log lines and after at least one
    /// nightly uploads-&lt;ts&gt;.tgz has been taken since the deploy.</summary>
    public bool ScreenshotPurgeDryRun { get; init; } = true;
}

/// <summary>Row counts deleted by one <see cref="RetentionService.SweepOnceAsync"/> pass.</summary>
public sealed record RetentionSweepResult(
    int UsageEventsDeleted,
    int NotificationsDeleted,
    int SnapshotsDeleted,
    int InvitesDeleted
);

/// <summary>
/// DB-08: daily hosted job that prunes four unbounded tables by age, in batches. Modelled on
/// <see cref="DemoCleanupService"/> (initial delay, then a periodic sweep; all exceptions logged,
/// never thrown so one bad pass can't crash the process). Query filters are bypassed with
/// IgnoreQueryFilters() throughout — the job runs with no HTTP context, so ICurrentUser.TenantId is
/// null and IsSuperAdmin is false, which would otherwise make every strict-own query return nothing.
/// </summary>
public class RetentionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration config,
    ILogger<RetentionService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var initialDelayMinutes = config.GetValue("Retention:InitialDelayMinutes", 5);
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(0, initialDelayMinutes)), stoppingToken);
            await SweepAsync(stoppingToken);

            var intervalHours = config.GetValue("Retention:IntervalHours", 24);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, intervalHours)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — exit cleanly.
        }
    }

    /// <summary>
    /// Field-by-field bind (NOT IOptionsPattern-bind on the record): every option must have a line
    /// here or its env/config override is silently ignored (GLM DB-15 #1). Internal + static so
    /// tests can drive it directly with a ConfigurationBuilder.
    /// </summary>
    internal static RetentionOptions BindOptions(IConfiguration config) =>
        new()
        {
            Enabled = config.GetValue("Retention:Enabled", true),
            UsageEventsDays = config.GetValue("Retention:UsageEventsDays", 180),
            RollupDays = config.GetValue("Retention:RollupDays", 3),
            NotificationsReadDays = config.GetValue("Retention:NotificationsReadDays", 90),
            PageContextSnapshotDays = config.GetValue("Retention:PageContextSnapshotDays", 30),
            InvitesDays = config.GetValue("Retention:InvitesDays", 90),
            BatchSize = config.GetValue("Retention:BatchSize", 5000),
            IntervalHours = config.GetValue("Retention:IntervalHours", 24),
            InitialDelayMinutes = config.GetValue("Retention:InitialDelayMinutes", 5),
            DeletedCommentScreenshotDays = config.GetValue(
                "Retention:DeletedCommentScreenshotDays",
                30
            ),
            UploadOrphanGraceHours = config.GetValue("Retention:UploadOrphanGraceHours", 48),
            ScreenshotPurgeDryRun = config.GetValue("Retention:ScreenshotPurgeDryRun", true),
        };

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        var options = BindOptions(config);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var storage = scope.ServiceProvider.GetRequiredService<IFileStorage>();
            var signer = scope.ServiceProvider.GetRequiredService<IUploadSigner>();
            await SweepOnceAsync(db, options, logger, stoppingToken, storage, signer);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Retention: sweep failed");
        }
    }

    /// <summary>
    /// One retention pass over all four tables. Internal + static so tests can drive it directly
    /// against a Sqlite-backed AppDbContext (ExecuteDeleteAsync is relational-only — InMemory does
    /// not support it) without standing up the whole hosted service.
    /// </summary>
    internal static async Task<RetentionSweepResult> SweepOnceAsync(
        AppDbContext db,
        RetentionOptions o,
        ILogger log,
        CancellationToken ct,
        IFileStorage? storage = null,
        IUploadSigner? signer = null
    )
    {
        if (!o.Enabled)
        {
            log.LogInformation("Retention: disabled");
            return new RetentionSweepResult(0, 0, 0, 0);
        }

        var now = DateTime.UtcNow;

        // DB-15: roll usage_events up into usage_daily BEFORE the sweep, so volume counts survive
        // the delete. If the rollup fails, skip the usage_events delete this pass (an un-rolled row
        // is never lost); the other three sweeps run regardless.
        var rollupOk = true;
        try
        {
            await UsageRollup.RollupAsync(db, o, now, log, ct);
        }
        catch (Exception ex)
        {
            rollupOk = false;
            log.LogError(
                ex,
                "Retention: usage rollup failed; skipping usage_events sweep this pass"
            );
        }

        var usageEvents = rollupOk ? await SweepUsageEventsAsync(db, o, now, log, ct) : 0;
        var notifications = await SweepNotificationsAsync(db, o, now, log, ct);
        var snapshots = await SweepSnapshotsAsync(db, o, now, log, ct);
        var invites = await SweepInvitesAsync(db, o, now, log, ct);

        // DB-16: file purge runs LAST and is isolated — a storage failure never fails the row sweeps,
        // and vice versa.
        if (storage is null || signer is null)
        {
            log.LogWarning("Retention: screenshot purge skipped — storage/signer not supplied");
        }
        else
        {
            try
            {
                await ScreenshotPurge.PurgeDeletedAsync(db, storage, signer, o, now, log, ct);
                await ScreenshotPurge.OrphanSweepAsync(db, storage, signer, o, now, log, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Retention: screenshot purge failed; rows were swept normally");
            }
        }

        return new RetentionSweepResult(usageEvents, notifications, snapshots, invites);
    }

    private static async Task<int> SweepUsageEventsAsync(
        AppDbContext db,
        RetentionOptions o,
        DateTime now,
        ILogger log,
        CancellationToken ct
    )
    {
        if (o.UsageEventsDays <= 0)
        {
            log.LogInformation("Retention: usage_events skipped (period 0)");
            return 0;
        }

        var cutoff = now.AddDays(-o.UsageEventsDays);
        var total = 0;
        while (true)
        {
            var ids = await db
                .UsageEvents.IgnoreQueryFilters()
                .Where(e => e.CreatedAt < cutoff && !UsageEventTypes.OneShotFacts.Contains(e.Type))
                .OrderBy(e => e.Id)
                .Select(e => e.Id)
                .Take(o.BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
                break;

            var deleted = await db
                .UsageEvents.IgnoreQueryFilters()
                .Where(e => ids.Contains(e.Id))
                .ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < o.BatchSize)
                break;
        }

        log.LogInformation(
            "Retention: usage_events deleted {Count} rows older than {Cutoff:u}",
            total,
            cutoff
        );
        return total;
    }

    private static async Task<int> SweepNotificationsAsync(
        AppDbContext db,
        RetentionOptions o,
        DateTime now,
        ILogger log,
        CancellationToken ct
    )
    {
        if (o.NotificationsReadDays <= 0)
        {
            log.LogInformation("Retention: notifications skipped (period 0)");
            return 0;
        }

        var cutoff = now.AddDays(-o.NotificationsReadDays);
        var total = 0;
        while (true)
        {
            var ids = await db
                .Notifications.IgnoreQueryFilters()
                .Where(n => n.ReadAt != null && n.ReadAt < cutoff)
                .OrderBy(n => n.Id)
                .Select(n => n.Id)
                .Take(o.BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
                break;

            var deleted = await db
                .Notifications.IgnoreQueryFilters()
                .Where(n => ids.Contains(n.Id))
                .ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < o.BatchSize)
                break;
        }

        log.LogInformation(
            "Retention: notifications deleted {Count} rows older than {Cutoff:u}",
            total,
            cutoff
        );
        return total;
    }

    private static async Task<int> SweepSnapshotsAsync(
        AppDbContext db,
        RetentionOptions o,
        DateTime now,
        ILogger log,
        CancellationToken ct
    )
    {
        if (o.PageContextSnapshotDays <= 0)
        {
            log.LogInformation("Retention: page_context_snapshots skipped (period 0)");
            return 0;
        }

        var cutoff = now.AddDays(-o.PageContextSnapshotDays);
        var total = 0;
        while (true)
        {
            var ids = await db
                .PageContextSnapshots.IgnoreQueryFilters()
                .Where(s =>
                    s.LastEventAt < cutoff
                    && !db
                        .Comments.IgnoreQueryFilters()
                        .Any(c => c.PageContextSnapshotId == s.Id && c.DeletedAt == null)
                )
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(o.BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
                break;

            var deleted = await db
                .PageContextSnapshots.IgnoreQueryFilters()
                .Where(s => ids.Contains(s.Id))
                .ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < o.BatchSize)
                break;
        }

        log.LogInformation(
            "Retention: page_context_snapshots deleted {Count} rows older than {Cutoff:u}",
            total,
            cutoff
        );
        return total;
    }

    private static async Task<int> SweepInvitesAsync(
        AppDbContext db,
        RetentionOptions o,
        DateTime now,
        ILogger log,
        CancellationToken ct
    )
    {
        if (o.InvitesDays <= 0)
        {
            log.LogInformation("Retention: invites skipped (period 0)");
            return 0;
        }

        var cutoff = now.AddDays(-o.InvitesDays);
        var total = 0;
        while (true)
        {
            var ids = await db
                .Invites.IgnoreQueryFilters()
                .Where(i =>
                    i.Uses == 0
                    && (i.ExpiresAt < cutoff || (i.RevokedAt != null && i.RevokedAt < cutoff))
                    && !db.Set<QuickAccessLink>().IgnoreQueryFilters().Any(q => q.InviteId == i.Id)
                )
                .OrderBy(i => i.Id)
                .Select(i => i.Id)
                .Take(o.BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
                break;

            var deleted = await db
                .Invites.IgnoreQueryFilters()
                .Where(i => ids.Contains(i.Id))
                .ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < o.BatchSize)
                break;
        }

        log.LogInformation(
            "Retention: invites deleted {Count} rows older than {Cutoff:u}",
            total,
            cutoff
        );
        return total;
    }
}
