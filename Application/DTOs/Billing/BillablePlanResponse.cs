namespace Pointer.Application.DTOs.Billing;

/// <summary>
/// A plan a Workspace Admin may request/quote via <c>POST /api/admin/billing/{quote,request}</c> —
/// unlike the public marketing catalog (<c>GET /api/plans</c>, which deliberately omits <c>Id</c>),
/// this carries the id those two endpoints need. Backs <c>GET /api/admin/billing/plans</c>.
/// </summary>
public class BillablePlanResponse
{
    public int Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Currency { get; set; } = string.Empty;
    public string Interval { get; set; } = string.Empty;
    public List<string> FeatureBullets { get; set; } = new();

    /// <summary>True when this is the workspace's current plan (its Subscription.PlanId) — free of
    /// any in-flight Requested/comp state, just "what they're on right now".</summary>
    public bool IsCurrent { get; set; }
}
