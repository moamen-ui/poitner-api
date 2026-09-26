using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// DB-20. R8.9 financial ledger table: FK SET NULL, survives the workspace (never deleted with it,
/// excluded from <c>HardDeleteOrder</c> by name). Append-only at three layers (R17, the second such
/// table after <see cref="AuditEvent"/>): every property is <c>init</c>-only in C#, the Postgres
/// trigger <c>trg_billing_payments_append_only</c> refuses UPDATE/DELETE at the DB, and
/// <c>AppDbContext.EnforceBillingPaymentsAppendOnly</c> refuses it at SaveChanges. A correction is a
/// new <see cref="BillingPaymentKind.Void"/> row, never an edit.
/// </summary>
public class BillingPayment
{
    public long Id { get; init; }

    /// <summary>Tenant boundary. NULL only after the owning workspace was hard-deleted (FK SET NULL —
    /// the single permitted UPDATE shape the trigger allows).</summary>
    public Guid? OwnerId { get; init; }

    /// <summary>The plan this payment paid for (Void: the plan of the payment it voids).</summary>
    public int PlanId { get; init; }

    public BillingPaymentKind Kind { get; init; }

    /// <summary>Received (Void: the voided amount, positive).</summary>
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;

    /// <summary>What the system quoted when this was recorded.</summary>
    public decimal? QuotedAmount { get; init; }

    /// <summary>Required for Payment.</summary>
    public PaymentMethod? Method { get; init; }

    /// <summary>Receipt / transfer reference.</summary>
    public string? Reference { get; init; }

    /// <summary>Operator note (Payment) or the void reason (Void, required by the service).</summary>
    public string? Note { get; init; }

    /// <summary>Operator-entered; required for Payment.</summary>
    public DateTime? PaidAt { get; init; }

    /// <summary>Granted period; required for Payment.</summary>
    public DateTime? PeriodStart { get; init; }
    public DateTime? PeriodEnd { get; init; }

    /// <summary>Snapshot of the subscription immediately before this row — what a Void restores.</summary>
    public int? PreviousPlanId { get; init; }
    public SubscriptionStatus? PreviousStatus { get; init; }
    public DateTime? PreviousPeriodEnd { get; init; }

    public long? DiscountRedemptionId { get; init; }

    /// <summary>True when this payment moved the redemption Pending → Applied.</summary>
    public bool DiscountFirstApplied { get; init; }

    /// <summary>Void only: the payment this row voids.</summary>
    public long? VoidsPaymentId { get; init; }

    public DateTime RecordedAt { get; init; }

    /// <summary>Operator's <c>User.PublicId</c> — content reference, no FK (R14).</summary>
    public Guid RecordedBy { get; init; }
}
