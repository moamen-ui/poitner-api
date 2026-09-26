namespace Pointer.Domain.Enums;

/// <summary>
/// DB-20. Whether a discount code's Applied redemption benefits only the first paid period
/// (<c>Once</c>) or every renewal of the same plan (<c>Forever</c>). Append-only ints (R10).
/// </summary>
public enum DiscountDuration
{
    Once = 1,
    Forever = 2,
}
