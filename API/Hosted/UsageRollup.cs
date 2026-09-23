using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-15. Recomputes usage_daily for every UTC day in [today-RollupDays, yesterday] plus every day
/// in the retention window that has no rows yet (first run backfills the whole window).
/// Provider-agnostic upsert in memory (no ON CONFLICT): rows per (day, owner, type) are tiny.
/// Idempotent: a re-run produces identical counts. Runs INSIDE RetentionService.SweepOnceAsync
/// before SweepUsageEventsAsync; if it throws, that pass skips the usage_events delete so no
/// un-rolled row is lost.
/// </summary>
internal static class UsageRollup
{
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

        // Days to (re)compute: the recent recompute window, plus every retention-window day that
        // has no usage_daily rows yet. Today is never rolled — it is still incomplete.
        var scheduled = new HashSet<DateOnly>();
        for (var d = today.AddDays(-o.RollupDays); d <= yesterday; d = d.AddDays(1))
            scheduled.Add(d);

        var rolledDays = (
            await db.UsageDaily.IgnoreQueryFilters().Select(x => x.Day).Distinct().ToListAsync(ct)
        ).ToHashSet();
        for (var d = today.AddDays(-o.UsageEventsDays); d <= yesterday; d = d.AddDays(1))
        {
            if (!rolledDays.Contains(d))
                scheduled.Add(d);
        }

        var days = scheduled.OrderBy(d => d).ToList();
        foreach (var d in days)
        {
            var dayStart = d.ToDateTime(TimeOnly.MinValue);
            var dayEndExclusive = dayStart.AddDays(1);

            var groups = await db
                .UsageEvents.IgnoreQueryFilters()
                .Where(e => e.CreatedAt >= dayStart && e.CreatedAt < dayEndExclusive)
                .GroupBy(e => new { e.OwnerId, e.Type })
                .Select(g => new
                {
                    g.Key.OwnerId,
                    g.Key.Type,
                    Count = g.Count(),
                })
                .ToListAsync(ct);

            var existing = await db
                .UsageDaily.IgnoreQueryFilters()
                .Where(x => x.Day == d)
                .ToListAsync(ct);

            foreach (var g in groups)
            {
                // Null-safe (OwnerId, Type) match — the upsert. NULL-owner groups land on the one
                // NULL-owner row the unique index (NULLS NOT DISTINCT) allows for this key.
                var row = existing.FirstOrDefault(x => x.OwnerId == g.OwnerId && x.Type == g.Type);
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
                var stale in existing.Where(x =>
                    groups.All(g => g.OwnerId != x.OwnerId || g.Type != x.Type)
                )
            )
            {
                stale.Count = 0;
                stale.ComputedAt = nowUtc;
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        if (days.Count > 0)
            log.LogInformation("Retention: usage rollup wrote {Days} day(s)", days.Count);
        return days.Count;
    }
}
