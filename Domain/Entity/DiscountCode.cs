using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// DB-20. A super-admin-managed reference/discount code (global catalog like <see cref="Plan"/> —
/// no <c>owner_id</c>, no query filter, super-admin endpoints only). <see cref="Code"/> is immutable
/// after create (F-B8/F-B9); every other field may be edited — redemptions keep their own snapshots,
/// so an edit never rewrites history.
/// </summary>
public class DiscountCode : BaseEntity
{
    /// <summary>Stored upper-case (service normalises <c>Trim().ToUpperInvariant()</c>). Immutable after create.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Partner / marketer label — display only.</summary>
    public string? Label { get; set; }

    /// <summary>Operator note.</summary>
    public string? Note { get; set; }

    public DiscountKind Kind { get; set; }

    /// <summary>Percent (0 &lt; v &le; 100) or a fixed amount, per <see cref="Kind"/>.</summary>
    public decimal Value { get; set; }

    /// <summary>Required iff <see cref="Kind"/> is <see cref="DiscountKind.FixedAmount"/>.</summary>
    public string? Currency { get; set; }

    public DiscountDuration Duration { get; set; } = DiscountDuration.Once;

    /// <summary>Half-open validity window <c>[ValidFrom, ValidUntil)</c>; either bound may be null.</summary>
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }

    /// <summary>Null = unlimited.</summary>
    public int? MaxRedemptions { get; set; }

    /// <summary>Deactivate instead of delete (F-B8) — the code string is never removed.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Which plans this code applies to. No rows = valid for every plan.</summary>
    public List<DiscountCodePlan> PlanScopes { get; set; } = new();
}
