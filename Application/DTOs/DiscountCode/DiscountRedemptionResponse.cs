namespace Pointer.Application.DTOs.DiscountCode;

/// <summary>DB-20 §3.6h: one row of a code's redemption drill-down (or a workspace's own list, in
/// the operator billing drawer).</summary>
public class DiscountRedemptionResponse
{
    public long Id { get; set; }
    public Guid? WorkspaceId { get; set; }

    /// <summary>"Deleted workspace" when <see cref="WorkspaceId"/> is null (left join, §3.6h).</summary>
    public string WorkspaceName { get; set; } = string.Empty;
    public int PlanId { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CodeSnapshot { get; set; } = string.Empty;
    public string KindSnapshot { get; set; } = string.Empty;
    public string DurationSnapshot { get; set; } = string.Empty;
    public decimal ValueSnapshot { get; set; }
    public string? CurrencySnapshot { get; set; }
    public decimal OriginalPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FinalPrice { get; set; }
    public string PriceCurrency { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}
