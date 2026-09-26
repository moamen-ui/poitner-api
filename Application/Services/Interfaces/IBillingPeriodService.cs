namespace Pointer.Application.Services.Interfaces;

/// <summary>DB-20 §3.6f. The period job's four passes (h1 comp end, h2 reminder, h3 past due, h4
/// downgrade) — hosted by <c>API/Hosted/BillingPeriodJob.cs</c> every <c>Billing:IntervalMinutes</c>.
/// Each candidate row is processed in its own transaction: lock its workspace, reload the row,
/// re-check the predicate, then act — so a race with a human write (payment, comp, plan change)
/// never double-acts on a stale row.</summary>
public interface IBillingPeriodService
{
    Task RunOnceAsync(DateTime now);
}
