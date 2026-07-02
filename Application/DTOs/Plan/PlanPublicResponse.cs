using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Plan;

/// <summary>
/// Marketing-only plan shape for the anonymous public endpoint. NO entitlement values, no IsActive,
/// no ids beyond the stable slug.
/// </summary>
public class PlanPublicResponse
{
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public string Currency { get; set; } = string.Empty;
    public BillingInterval Interval { get; set; }
    public List<string> FeatureBullets { get; set; } = new();
    public PlanDisplayState DisplayState { get; set; }
    public int SortOrder { get; set; }
}
