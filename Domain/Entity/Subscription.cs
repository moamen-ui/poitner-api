using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

/// <summary>
/// A tenant's subscription to a <see cref="Plan"/>. TENANT-SCOPED (strict-own query filter like
/// <see cref="Invite"/>), one per tenant (<see cref="OwnerId"/> unique). This row IS the tenant→plan
/// link AND the payment-ready shape (external ids/status/period). PlanId = the GRANTED plan (the
/// only input to entitlements); a request lives in the Requested* columns — DB-20. A missing row ⇒
/// Free. The billing fields carry no gateway calls today (Noop provider).
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

    // ── DB-20: the workspace's request for a paid plan (all five null together) ──

    /// <summary>The plan requested (not yet granted). No navigation is added — the FK is configured
    /// without one (§3.3): a request is a request, not a second "current plan".</summary>
    public int? RequestedPlanId { get; set; }

    /// <summary>When the request was made.</summary>
    public DateTime? RequestedAt { get; set; }

    /// <summary>Requesting admin's <c>User.PublicId</c> — content reference, no FK (R14).</summary>
    public Guid? RequestedBy { get; set; }

    /// <summary>The price shown to the workspace, after any discount — snapshotted (R20).</summary>
    public decimal? QuotedPrice { get; set; }

    /// <summary>ISO 4217, upper case.</summary>
    public string? QuotedCurrency { get; set; }

    // ── DB-20: complimentary (VIP / sales-led) marker ──

    /// <summary>Comp ⇒ Status = Active, never billed, never period-expired.</summary>
    public bool IsComplimentary { get; set; }

    public DateTime? CompedAt { get; set; }

    /// <summary>Operator's <c>User.PublicId</c> — content reference, no FK (R14).</summary>
    public Guid? CompedBy { get; set; }

    /// <summary>Operator-typed; no personal data (R8.9).</summary>
    public string? CompReason { get; set; }

    /// <summary>Optional: hands the row to the normal period machinery (job h1) when it arrives.</summary>
    public DateTime? CompEndsAt { get; set; }

    /// <summary>Idempotency stamp for the T-N-days renewal e-mail; cleared whenever a period is extended.</summary>
    public DateTime? RenewalReminderSentAt { get; set; }
}
