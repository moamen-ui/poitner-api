namespace Pointer.Domain.Enums;

/// <summary>DB-20. How a manual payment was received. Append-only ints (R10) — a future gateway
/// appends a new value rather than reusing one.</summary>
public enum PaymentMethod
{
    Cash = 1,
    BankTransfer = 2,
    Other = 3,
}
