using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// DB-20. R8.9 financial ledger table: FK SET NULL, survives the workspace (never deleted with it,
/// excluded from <c>HardDeleteOrder</c> by name). Not a <see cref="BaseEntity"/> (<c>long Id</c>,
/// identity-by-default, no soft delete — its lifecycle is <see cref="Status"/>). Every field the
/// quote relied on is snapshotted at creation (R20) — an edit to the code or the plan afterwards
/// never rewrites what this row already recorded.
/// </summary>
public class DiscountRedemption
{
    public long Id { get; init; }

    /// <summary>Tenant boundary. NULL only after the owning workspace was hard-deleted (FK SET NULL).</summary>
    public Guid? OwnerId { get; init; }

    public int DiscountCodeId { get; init; }
    public int PlanId { get; init; }

    public DiscountRedemptionStatus Status { get; set; }

    // ── Snapshots (init-only — never recomputed from the editable catalog row, R20) ──
    public string CodeSnapshot { get; init; } = string.Empty;
    public DiscountKind KindSnapshot { get; init; }
    public DiscountDuration DurationSnapshot { get; init; }
    public decimal ValueSnapshot { get; init; }
    public string? CurrencySnapshot { get; init; }
    public decimal OriginalPrice { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal FinalPrice { get; init; }
    public string PriceCurrency { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    /// <summary>Requesting admin's <c>User.PublicId</c> — content reference, no FK (R14).</summary>
    public Guid CreatedBy { get; init; }

    public DateTime? AppliedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public RedemptionReleaseReason? ReleaseReason { get; set; }
}
