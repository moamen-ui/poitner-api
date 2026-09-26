namespace Pointer.Domain.Enums;

/// <summary>DB-20. A <c>billing_payments</c> row is either a recorded payment or a void of one.
/// Append-only ints (R10).</summary>
public enum BillingPaymentKind
{
    Payment = 1,
    Void = 2,
}
