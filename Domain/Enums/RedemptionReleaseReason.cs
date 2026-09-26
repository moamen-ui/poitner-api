namespace Pointer.Domain.Enums;

/// <summary>DB-20. Why a <c>discount_redemptions</c> row moved to Released. Append-only ints (R10).</summary>
public enum RedemptionReleaseReason
{
    Replaced = 1,
    CancelledByWorkspace = 2,
    RejectedByOperator = 3,
    PaymentVoided = 4,
    OperatorPlanOverride = 5,
    WorkspaceDeleted = 6,
}
