using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Domain.Entity;

/// <summary>
/// A subscription plan (GLOBAL catalog). Like <see cref="AppSetting"/>, a Plan has NO <c>OwnerId</c> and
/// NO query filter — it is not tenant data; it is guarded by endpoint authorization (super-admin CRUD;
/// anonymous marketing read). A tenant's effective plan is resolved via its <see cref="Subscription"/>
/// (missing ⇒ Free). <see cref="Entitlements"/> is a typed owned VO serialized to one JSON column,
/// exactly like <c>Comment.Element</c>.
/// </summary>
public class Plan : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Stable machine id used by <c>?plan=…</c> / signup. Unique.</summary>
    public string Slug { get; set; } = string.Empty;

    public decimal PriceMonthly { get; set; }
    public string Currency { get; set; } = "USD";
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;
    public int SortOrder { get; set; }

    /// <summary>false = no NEW subscriptions (e.g. the internal Legacy plan).</summary>
    public bool IsActive { get; set; } = true;

    public PlanDisplayState DisplayState { get; set; } = PlanDisplayState.Visible;

    /// <summary>Marketing bullets (display-only). Stored as a JSON list; order preserved.</summary>
    public List<string> FeatureBullets { get; set; } = new();

    /// <summary>Typed entitlement values (owned VO → one JSON column). Missing key ⇒ catalog default.</summary>
    public PlanEntitlements Entitlements { get; set; } = new();
}
