using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-15. Recomputes usage_daily for every UTC day in [today-RollupDays, yesterday] (always
/// recomputed, so late-arriving events are picked up) plus every earlier day, back to the retention
/// window, that has never been rolled (first run backfills the whole window). Provider-agnostic
/// upsert in memory (no ON CONFLICT): rows per (day, owner, type) are tiny. Idempotent: a re-run
/// produces identical counts. Runs INSIDE RetentionService.SweepOnceAsync before
/// SweepUsageEventsAsync; if it throws, that pass skips the usage_events delete so no un-rolled row
/// is lost.
/// </summary>
internal static class UsageRollup
{
    /// <summary>
    /// app_settings key: the last UTC day (yyyy-MM-dd) that is fully rolled AND has fallen out of
    /// the always-recomputed recent window as of the last successful pass. Review fix #4/#6: without
    /// this mark, "which days still need backfilling" was answered by scanning
    /// SELECT DISTINCT day FROM usage_daily on every pass — unbounded (usage_daily is kept forever,
    /// D15.4) and, worse, wrong: an eventless day never gets a usage_daily row at all (no group, no
    /// row), so it would never show up as "rolled" and would be rescheduled — and re-scanned — on
    /// every single pass forever. The high-water mark records "already considered" independently of
    /// whether a row was written.
    /// </summary>
    internal const string HighWaterMarkKey = "usage_rollup_high_water";

    internal static async Task<int> RollupAsync(
        AppDbContext db,
        RetentionOptions o,
        DateTime nowUtc,
        ILogger log,
        CancellationToken ct
    )
    {
        // Step 0 — argument validation (also the failure-injection seam the
        // Sweep_SkipsUsageDeleteWhenRollupFails test relies on): must run BEFORE any query.
        if (o.RollupDays < 0 || o.UsageEventsDays < 0 || o.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(o),
                "Retention: RollupDays/UsageEventsDays must be >= 0 and BatchSize > 0"
            );

        var today = DateOnly.FromDateTime(nowUtc);
        var yesterday = today.AddDays(-1);
        var recentStart = today.AddDays(-o.RollupDays); // always recomputed, late events included
        var retentionStart = today.AddDays(-o.UsageEventsDays); // never backfill before this

        var highWaterSetting = await db
            .AppSettings.Where(s => s.DeletedAt == null && s.Key == HighWaterMarkKey)
            .FirstOrDefaultAsync(ct);
        DateOnly? highWater =
            highWaterSetting != null && DateOnly.TryParse(highWaterSetting.Value, out var hw)
                ? hw
                : null;

        // Backfill resumes right after the high-water mark (or the whole retention window on the
        // very first run / after it was never set). Never before the retention window itself — it
        // may have shrunk since the mark was last written.
        var backfillStart = highWater.HasValue ? highWater.Value.AddDays(1) : retentionStart;
        if (backfillStart < retentionStart)
            backfillStart = retentionStart;

        var windowStartDay = backfillStart < recentStart ? backfillStart : recentStart;

        if (windowStartDay > yesterday)
        {
            // Nothing to (re)compute this pass (e.g. already caught up and RollupDays is 0).
            return 0;
        }

        // Bounds must be Utc-kinded — Npgsql refuses Unspecified for timestamptz (review blocker #1).
        var windowStart = DateTime.SpecifyKind(
            windowStartDay.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Utc
        );
        var windowEndExclusive = DateTime.SpecifyKind(
            yesterday.AddDays(1).ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Utc
        );

        // ONE grouped query over the WHOLE window (review fix #4) — was one DISTINCT-day query plus
        // one additional seq scan PER scheduled day.
        var raw = await db
            .UsageEvents.IgnoreQueryFilters()
            .Where(e => e.CreatedAt >= windowStart && e.CreatedAt < windowEndExclusive)
            .Select(e => new
            {
                e.OwnerId,
                e.Type,
                e.CreatedAt,
            })
            .ToListAsync(ct);

        var groups = raw
            .GroupBy(e => (Day: DateOnly.FromDateTime(e.CreatedAt), e.OwnerId, e.Type))
            .Select(g => new
            {
                g.Key.Day,
                g.Key.OwnerId,
                g.Key.Type,
                Count = g.Count(),
            })
            .ToList();

        var existing = await db
            .UsageDaily.IgnoreQueryFilters()
            .Where(x => x.Day >= windowStartDay && x.Day <= yesterday)
            .ToListAsync(ct);

        var groupsByDay = groups.ToLookup(g => g.Day);
        var existingByDay = existing.ToLookup(x => x.Day);
        var daysTouched = 0;

        for (var d = windowStartDay; d <= yesterday; d = d.AddDays(1))
        {
            var dayGroups = groupsByDay[d].ToList();
            var dayExisting = existingByDay[d].ToList();
            if (dayGroups.Count == 0 && dayExisting.Count == 0)
                continue; // an eventless day that never had a row — nothing to upsert

            daysTouched++;

            foreach (var g in dayGroups)
            {
                // Null-safe (OwnerId, Type) match — the upsert. NULL-owner groups land on the one
                // NULL-owner row the unique index (NULLS NOT DISTINCT) allows for this key.
                var row = dayExisting.FirstOrDefault(x => x.OwnerId == g.OwnerId && x.Type == g.Type);
                if (row != null)
                {
                    row.Count = g.Count;
                    row.ComputedAt = nowUtc;
                }
                else
                {
                    db.UsageDaily.Add(
                        new UsageDaily
                        {
                            Day = d,
                            OwnerId = g.OwnerId,
                            Type = g.Type,
                            Count = g.Count,
                            ComputedAt = nowUtc,
                        }
                    );
                }
            }

            // Events were deleted/moved since the last pass — keep the row at zero so a re-run is
            // stable (a day that once had activity is never silently dropped from the series).
            foreach (
                var stale in dayExisting.Where(x =>
                    dayGroups.All(g => g.OwnerId != x.OwnerId || g.Type != x.Type)
                )
            )
            {
                stale.Count = 0;
                stale.ComputedAt = nowUtc;
            }
        }

        // Advance the high-water mark through the last day that falls OUTSIDE the always-recomputed
        // recent window: everything from windowStartDay up to there was just rolled, contiguously,
        // in this same pass, so it is safe to mark "done" and never look at again via a full scan.
        var candidateHighWater = recentStart.AddDays(-1);
        if (candidateHighWater > yesterday)
            candidateHighWater = yesterday;
        if (
            candidateHighWater >= windowStartDay.AddDays(-1)
            && (!highWater.HasValue || candidateHighWater > highWater.Value)
        )
        {
            var value = candidateHighWater.ToString("yyyy-MM-dd");
            if (highWaterSetting != null)
                highWaterSetting.Value = value;
            else
                db.AppSettings.Add(new AppSetting { Key = HighWaterMarkKey, Value = value });
        }

        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        if (daysTouched > 0)
            log.LogInformation("Retention: usage rollup wrote {Days} day(s)", daysTouched);
        return daysTouched;
    }
}
