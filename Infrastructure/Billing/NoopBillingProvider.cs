using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Infrastructure.Billing;

/// <summary>
/// The default <see cref="IBillingProvider"/>: zero HTTP. Every method mutates only local subscription
/// state so the payment seam is exercised end-to-end without a real gateway. Swap for a Stripe adapter
/// later via config in Infrastructure/DependencyInjection.cs — no schema changes needed.
/// </summary>
public class NoopBillingProvider : IBillingProvider
{
    public Task ActivateAsync(Subscription sub)
    {
        // Only advance a pending subscription; already-active/other states are left untouched.
        if (sub.Status == SubscriptionStatus.PendingActivation || sub.Status == SubscriptionStatus.None)
            sub.Status = SubscriptionStatus.Active;
        return Task.CompletedTask;
    }

    public Task ChangePlanAsync(Subscription sub, int newPlanId)
    {
        // The caller persists the new PlanId; the Noop provider has nothing to call.
        return Task.CompletedTask;
    }

    public Task CancelAsync(Subscription sub)
    {
        sub.Status = SubscriptionStatus.Canceled;
        return Task.CompletedTask;
    }
}
