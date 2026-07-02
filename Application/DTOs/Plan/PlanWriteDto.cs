using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Plan;

/// <summary>Create/update payload for a plan (super-admin). Validated by PlanWriteDtoValidator.</summary>
public class PlanWriteDto
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }
    public string Currency { get; set; } = "USD";
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public PlanDisplayState DisplayState { get; set; } = PlanDisplayState.Visible;
    public List<string> FeatureBullets { get; set; } = new();
    public PlanEntitlementsDto Entitlements { get; set; } = new();
}
