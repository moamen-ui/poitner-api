using Pointer.Application.DTOs.Billing;
using Pointer.Application.Response;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-20 §3.6 a-e, h1 (end comp). Owns every write to <c>subscriptions</c>' request/comp columns and
/// every <c>billing_payments</c>/<c>discount_redemptions</c> row a human (workspace admin or super
/// admin) causes — the period job's own writes (h1-h4) live on <see cref="IBillingPeriodService"/>.
/// The workspace-side methods (<see cref="RequestPlanAsync"/>, <see cref="CancelRequestAsync"/>,
/// <see cref="GetSummaryAsync"/>, <see cref="GetWorkspacePaymentsAsync"/>, <see cref="QuoteAsync"/>)
/// act on the CALLER's own current workspace (<c>ICurrentUser.TenantId</c>), gated by
/// <see cref="Common.WorkspaceLifecycleGuard.CanManageAsync"/> — never take a workspace id parameter.
/// The operator methods take an explicit <c>workspaceId</c> and assume the caller is already gated
/// by <c>Policies.SuperAdmin</c> at the controller.
/// </summary>
public interface IBillingService
{
    Task<Result<BillingSummaryResponse>> GetSummaryAsync();
    Task<Result<List<WorkspacePaymentResponse>>> GetWorkspacePaymentsAsync();

    /// <summary>The plans this workspace may quote/request — unlike <c>GET /api/plans</c> (the
    /// anonymous marketing catalog, which omits <c>Id</c> on purpose) or <c>GET /api/admin/plans</c>
    /// (SuperAdmin-only, the full catalog incl. hidden/internal), a Workspace Admin needs ids for the
    /// live, requestable subset only.</summary>
    Task<Result<List<BillablePlanResponse>>> ListRequestablePlansAsync();
    Task<Result<BillingQuoteResponse>> QuoteAsync(int planId, string? referenceCode);
    Task<Result<BillingSummaryResponse>> RequestPlanAsync(int planId, string? referenceCode);
    Task<Result> CancelRequestAsync();

    // ── Operator (super admin) ──
    Task<Result<OperatorBillingResponse>> GetOperatorBillingAsync(Guid workspaceId);
    Task<Result> RejectRequestAsync(Guid workspaceId);
    Task<Result<OperatorPaymentResponse>> RecordPaymentAsync(
        Guid workspaceId,
        decimal amount,
        string? currency,
        DateTime paidAt,
        PaymentMethod method,
        string? reference,
        string? note
    );
    Task<Result> VoidPaymentAsync(Guid workspaceId, long paymentId, string reason);
    Task<Result> EndCompAsync(Guid workspaceId);
}
