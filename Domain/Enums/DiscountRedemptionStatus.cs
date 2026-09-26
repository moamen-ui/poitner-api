namespace Pointer.Domain.Enums;

/// <summary>
/// DB-20. Lifecycle of a <c>discount_redemptions</c> row. <c>Pending</c> = quoted, not yet paid;
/// <c>Applied</c> = a payment used it; <c>Released</c> = it was replaced/cancelled/rejected/voided
/// without ever being applied, or its Applied benefit ended (workspace deleted, payment voided).
/// Append-only ints (R10).
/// </summary>
public enum DiscountRedemptionStatus
{
    Pending = 1,
    Applied = 2,
    Released = 3,
}
