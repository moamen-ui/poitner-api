namespace Pointer.Application.DTOs.Billing;

/// <summary>DB-20 §3.7: the operator's full view of a <c>billing_payments</c> row — everything,
/// including <c>note</c> and <c>recordedBy</c> (a <c>public_id</c> content reference, R14).</summary>
public class OperatorPaymentResponse
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal? QuotedAmount { get; set; }
    public string? Method { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    public int? PreviousPlanId { get; set; }
    public string? PreviousStatus { get; set; }
    public DateTime? PreviousPeriodEnd { get; set; }
    public long? DiscountRedemptionId { get; set; }
    public bool DiscountFirstApplied { get; set; }
    public long? VoidsPaymentId { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid RecordedBy { get; set; }
}
