using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Plan;

/// <summary>Full plan shape for super-admin CRUD (includes entitlement values + state).</summary>
public class PlanAdminResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public string Currency { get; set; } = string.Empty;
    public BillingInterval Interval { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public PlanDisplayState DisplayState { get; set; }
    public List<string> FeatureBullets { get; set; } = new();
    public PlanEntitlementsDto Entitlements { get; set; } = new();

    /// <summary>Number of active (non-deleted) subscriptions on this plan.</summary>
    public int ActiveSubscriptions { get; set; }
}
