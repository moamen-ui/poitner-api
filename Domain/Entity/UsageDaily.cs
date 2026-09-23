using System;

namespace Pointer.Domain.Entity;

/// <summary>
/// DB-15: one row per (UTC day, owner, event type) — the daily rollup of usage_events volume rows,
/// written by the retention job BEFORE the usage sweep so counts survive the 180-day delete. Kept
/// forever (D15.4). Not a <see cref="BaseEntity"/> (like <see cref="UsageEvent"/>).
/// </summary>
public class UsageDaily
{
    public long Id { get; set; }
    public DateOnly Day { get; set; }

    /// <summary>NULL = events with no workspace, or a hard-deleted one (FK SET NULL, R8.8).</summary>
    public Guid? OwnerId { get; set; }

    public string Type { get; set; } = string.Empty;
    public int Count { get; set; }
    public DateTime ComputedAt { get; set; }
}
