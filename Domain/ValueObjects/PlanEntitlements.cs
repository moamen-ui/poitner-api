namespace Pointer.Domain.ValueObjects;

/// <summary>
/// Typed, compile-safe entitlement bag for a <see cref="Entity.Plan"/>. One named property per fixed
/// entitlement key — adding a lever is a property here (no per-key migration, since it is stored as a
/// single JSON column via <c>OwnsOne(...).ToJson("entitlements")</c>, mirroring <c>Comment.Element</c>).
///
/// CRITICAL (G7): every int property is <b>nullable</b>. A missing/unset value resolves to the CATALOG
/// default (see <c>EntitlementCatalog</c>), NEVER <c>0</c> — a 0 would silently lock a tenant out. The
/// convention is <c>-1</c> = unlimited. Booleans default to <c>null</c> too so a missing flag resolves to
/// its catalog default rather than <c>false</c>.
///
/// The property set MUST stay in sync with <c>EntitlementCatalog.All</c>; a unit test asserts this.
/// </summary>
public class PlanEntitlements
{
    // ── Enforced (P1) ──
    public int? MaxProjects { get; set; }
    public int? MaxSeats { get; set; }
    public int? MaxCommentsPerMonth { get; set; }
    public bool? ExtensionEnabled { get; set; }
    public int? MaxExtensionSites { get; set; }
    public int? MaxPredefinedActionsPerProject { get; set; }
    public int? MaxTenantWidePredefinedActions { get; set; }

    // ── Display-only (P1) ──
    public int? RetentionDays { get; set; }
    public int? MaxEnvironments { get; set; }
    public int? MaxActiveInvites { get; set; }
    public int? EmailsPerMonth { get; set; }
    public int? ExtensionCommentsPerMonth { get; set; }
    public int? MaxPendingSuggestions { get; set; }
    public bool? ExportImportEnabled { get; set; }
    public bool? PromptSuggestionsEnabled { get; set; }
    public bool? CustomStatusesEnabled { get; set; }
    public bool? PrioritySupport { get; set; }
}
