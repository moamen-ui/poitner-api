namespace Pointer.Domain.Enums;

/// <summary>DB-20. How a discount code's <c>value</c> is applied. Append-only ints (R10).</summary>
public enum DiscountKind
{
    Percent = 1,
    FixedAmount = 2,
}
