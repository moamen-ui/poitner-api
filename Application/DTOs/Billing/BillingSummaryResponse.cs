namespace Pointer.Application.DTOs.Billing;

/// <summary>DB-20 §3.9: <c>GET /api/admin/billing</c> and <c>POST /api/admin/billing/request</c>'s
/// response — the workspace's own view of its billing state. Shared with the operator drawer as the
/// <c>Summary</c> half of <see cref="OperatorBillingResponse"/> (same shape; the operator's payment
/// list is the part that carries more).</summary>
public class BillingSummaryResponse
{
    public int PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? CurrentPeriodEnd { get; set; }

    public bool IsComplimentary { get; set; }
    public DateTime? CompEndsAt { get; set; }

    public int? RequestedPlanId { get; set; }
    public string? RequestedPlanName { get; set; }
    public DateTime? RequestedAt { get; set; }
    public decimal? QuotedPrice { get; set; }
    public string? QuotedCurrency { get; set; }

    /// <summary>What the NEXT renewal of the current plan would cost today (a Forever code's discount
    /// applied, recomputed on today's price) — null when there is nothing to renew (Free/None, or comp).</summary>
    public decimal? RenewalPrice { get; set; }
    public string? RenewalCurrency { get; set; }

    /// <summary>The grace deadline while <c>Status == PastDue</c> (period end + the grace window) —
    /// when the workspace downgrades to Free if unpaid. Null otherwise.</summary>
    public DateTime? GraceEndsAt { get; set; }
}
