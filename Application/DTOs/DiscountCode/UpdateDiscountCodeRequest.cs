using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.DiscountCode;

/// <summary>Body of <c>PATCH /api/admin/discount-codes/{id}</c>. Every field but <c>code</c> may be
/// edited (F-B9) — redemptions keep their own snapshots, so an edit never rewrites history.</summary>
public class UpdateDiscountCodeRequest
{
    public string? Label { get; set; }
    public string? Note { get; set; }
    public DiscountKind Kind { get; set; }
    public decimal Value { get; set; }
    public string? Currency { get; set; }
    public DiscountDuration Duration { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public int? MaxRedemptions { get; set; }
    public bool IsActive { get; set; }
    public List<int>? PlanIds { get; set; }
}
