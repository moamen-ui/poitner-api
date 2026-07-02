namespace Pointer.Domain.Enums;

/// <summary>
/// Lifecycle of a tenant's subscription. <c>None</c> is the pre-billing default; <c>PendingActivation</c>
/// covers a paid plan chosen at signup (tenant created, awaiting super-admin activation); the rest map
/// to a future payment provider's states. The effective plan is still resolved from the row's PlanId
/// regardless of status (a missing row ⇒ Free).
/// </summary>
public enum SubscriptionStatus
{
    None = 0,
    PendingActivation = 1,
    Trialing = 2,
    Active = 3,
    PastDue = 4,
    Canceled = 5
}
