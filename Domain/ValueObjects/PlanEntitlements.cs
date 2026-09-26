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
/// Add optional keys only (R12): a stored JSON that lacks a key materialises as <c>null</c> and
/// resolves to the catalog default — never change the meaning of an existing key; write a new key.
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

    /// <summary>
    /// Max workspaces the caller may OWN (DB-19 §3.2) before a new one is created; <c>-1</c> =
    /// unlimited; <c>0</c> = the endpoint is disabled for callers governed by this plan.
    /// Governed by the plan of the caller's CURRENT workspace; DB-19.
    /// </summary>
    public int? MaxOwnedWorkspaces { get; set; }

    /// <summary>
    /// <c>true</c> ⇒ the new workspace's admin membership is Pending/inactive (the self-signup
    /// state); <c>false</c> ⇒ Approved/active immediately. Note the restrictive-polarity bool:
    /// <c>true</c> is the restrictive value (DB-19 §3.1). Governed by the plan of the caller's
    /// CURRENT workspace; DB-19.
    /// </summary>
    public bool? NewWorkspaceRequiresApproval { get; set; }

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
