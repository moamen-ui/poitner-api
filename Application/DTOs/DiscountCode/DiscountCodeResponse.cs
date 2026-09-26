namespace Pointer.Application.DTOs.DiscountCode;

/// <summary>DB-20 §3.6h: a super-admin reference/discount code, including usage counts on the list
/// view (0/0 on a freshly-created code).</summary>
public class DiscountCodeResponse
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Label { get; set; }
    public string? Note { get; set; }
    public string Kind { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public string? Currency { get; set; }
    public string Duration { get; set; } = string.Empty;
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public int? MaxRedemptions { get; set; }
    public bool IsActive { get; set; }
    public List<int> PlanIds { get; set; } = new();

    /// <summary>Count of Applied redemptions.</summary>
    public int AppliedCount { get; set; }

    /// <summary>Count of Pending (open-quote) redemptions.</summary>
    public int PendingCount { get; set; }
}
