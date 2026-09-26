namespace Pointer.Application.DTOs.Billing;

/// <summary>DB-20 §3.6 Quote: pure, no writes — used by the preview endpoint, and folded into
/// <c>RequestPlanAsync</c>'s own quoting step.</summary>
public class BillingQuoteResponse
{
    public int PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal Discount { get; set; }
    public decimal Final { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int? DiscountCodeId { get; set; }
    public string? DiscountCodeLabel { get; set; }
}
