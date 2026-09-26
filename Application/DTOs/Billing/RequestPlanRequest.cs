namespace Pointer.Application.DTOs.Billing;

/// <summary>Body of <c>POST /api/admin/billing/quote</c> and <c>POST /api/admin/billing/request</c>.</summary>
public class RequestPlanRequest
{
    public int PlanId { get; set; }

    /// <summary>Optional reference/discount code (F-B13: in-app only, never at signup/invite accept).</summary>
    public string? ReferenceCode { get; set; }
}
