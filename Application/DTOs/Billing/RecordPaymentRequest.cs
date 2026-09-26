using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Billing;

/// <summary>Body of <c>POST /api/admin/tenants/{workspaceId}/payments</c> (super admin).</summary>
public class RecordPaymentRequest
{
    public decimal Amount { get; set; }

    /// <summary>Null = defaults to the quote's currency (§3.6c step 4).</summary>
    public string? Currency { get; set; }

    public DateTime PaidAt { get; set; }
    public PaymentMethod Method { get; set; }

    /// <summary>Receipt / transfer reference, ≤ 128 chars.</summary>
    public string? Reference { get; set; }

    /// <summary>Operator note, ≤ 500 chars — no personal data (R8.9).</summary>
    public string? Note { get; set; }
}
