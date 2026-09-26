using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.DiscountCode;

/// <summary>Body of <c>POST /api/admin/discount-codes</c>. <see cref="Code"/> is immutable after
/// create (F-B8/F-B9) — there is no Code property on <see cref="UpdateDiscountCodeRequest"/>.</summary>
public class CreateDiscountCodeRequest
{
    /// <summary>Normalised (Trim + ToUpperInvariant) by the service before validation/storage.</summary>
    public string Code { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Note { get; set; }
    public DiscountKind Kind { get; set; }
    public decimal Value { get; set; }
    public string? Currency { get; set; }
    public DiscountDuration Duration { get; set; } = DiscountDuration.Once;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public int? MaxRedemptions { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Plans this code applies to. Empty/null = valid for every plan.</summary>
    public List<int>? PlanIds { get; set; }
}
