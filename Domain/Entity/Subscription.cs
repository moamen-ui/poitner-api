using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// A tenant's subscription to a <see cref="Plan"/>. TENANT-SCOPED (strict-own query filter like
/// <see cref="Invite"/>), one per tenant (<see cref="OwnerId"/> unique). This row IS the tenant→plan
/// link AND the payment-ready shape (external ids/status/period). Effective plan = this row's Plan;
/// a missing row ⇒ Free. The billing fields carry no gateway calls today (Noop provider).
/// </summary>
public class Subscription : BaseEntity
{
    /// <summary>Tenant boundary — the self-owning admin's <c>User.PublicId</c>. Strict-own, never null.</summary>
    public Guid OwnerId { get; set; }

    public int PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.None;

    // ── Payment-ready fields (no gateway calls now) ──
    public string? BillingProvider { get; set; }
    public string? ExternalCustomerId { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public DateTime? CurrentPeriodEnd { get; set; }
    public DateTime? TrialEndsAt { get; set; }
}
