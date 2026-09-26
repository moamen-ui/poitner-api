using Pointer.Application.DTOs.DiscountCode;

namespace Pointer.Application.DTOs.Billing;

/// <summary>DB-20 §3.9: <c>GET /api/admin/tenants/{workspaceId}/billing</c> — the super-admin tenant
/// billing drawer's one load: summary + the full (unredacted) payment history + this workspace's
/// discount redemptions.</summary>
public class OperatorBillingResponse
{
    public BillingSummaryResponse Summary { get; set; } = new();
    public List<OperatorPaymentResponse> Payments { get; set; } = new();
    public List<DiscountRedemptionResponse> Redemptions { get; set; } = new();
}
