namespace Pointer.Application.DTOs.Billing;

/// <summary>DB-20 §3.7: the workspace-facing payment shape — omits <c>note</c> and
/// <c>recordedBy</c> (R17 operator redaction). See <see cref="OperatorPaymentResponse"/> for the
/// operator's full view of the same row.</summary>
public class WorkspacePaymentResponse
{
    public long Id { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string? Method { get; set; }
    public string? Reference { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
}
