namespace Pointer.Application.DTOs.Billing;

/// <summary>Body of <c>POST /api/admin/tenants/{workspaceId}/payments/{paymentId}/void</c> (super admin).</summary>
public class VoidPaymentRequest
{
    /// <summary>1-500 chars, free text — not audited (§3.6d).</summary>
    public string Reason { get; set; } = string.Empty;
}
