using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

/// <summary>
/// Payment-READY seam. No gateway calls today — the Noop implementation only flips local status. A
/// future Stripe/Paddle adapter replaces the implementation (+ a webhook controller) with NO schema
/// churn (Subscription already carries the external ids / status / period). Registered by MANUAL DI
/// (single-instance seam), NOT Scrutor.
/// </summary>
public interface IBillingProvider
{
    /// <summary>Activate a subscription (PendingActivation → Active). Noop: local flip only.</summary>
    Task ActivateAsync(Subscription sub);

    /// <summary>Route a plan change through the provider. Noop: no-op (caller persists PlanId).</summary>
    Task ChangePlanAsync(Subscription sub, int newPlanId);

    /// <summary>Cancel a subscription. Noop: local flip only.</summary>
    Task CancelAsync(Subscription sub);
}
