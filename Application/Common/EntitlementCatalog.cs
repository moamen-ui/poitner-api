using System.Reflection;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Common;

public enum EntitlementKind { Int, Bool }

/// <summary>
/// Metadata for one entitlement key. <see cref="Key"/> matches the <see cref="PlanEntitlements"/>
/// property name exactly (a unit test asserts the sets are equal). <see cref="Default"/> is what an
/// unset value resolves to — NEVER 0/false (G7). For ints, <c>-1</c> = unlimited.
/// </summary>
public sealed record EntitlementSpec(
    string Key,
    string Label,
    EntitlementKind Kind,
    bool Enforced,
    int DefaultInt,
    bool DefaultBool);

/// <summary>
/// The single source of truth for entitlement keys, consumed by the plan-write validator, the
/// enforcement service, seeding, and the landing labels. Keys are FIXED in code (adding one = editing
/// this catalog + the VO); super-admins edit only the values.
/// </summary>
public static class EntitlementCatalog
{
    // ── Enforced key names (mirror PlanEntitlements property names) ──
    public const string MaxProjects = nameof(PlanEntitlements.MaxProjects);
    public const string MaxSeats = nameof(PlanEntitlements.MaxSeats);
    public const string MaxCommentsPerMonth = nameof(PlanEntitlements.MaxCommentsPerMonth);
    public const string ExtensionEnabled = nameof(PlanEntitlements.ExtensionEnabled);
    public const string MaxExtensionSites = nameof(PlanEntitlements.MaxExtensionSites);
    public const string MaxPredefinedActionsPerProject = nameof(PlanEntitlements.MaxPredefinedActionsPerProject);
    public const string MaxTenantWidePredefinedActions = nameof(PlanEntitlements.MaxTenantWidePredefinedActions);

    // ── Display-only key names ──
    public const string RetentionDays = nameof(PlanEntitlements.RetentionDays);
    public const string MaxEnvironments = nameof(PlanEntitlements.MaxEnvironments);
    public const string MaxActiveInvites = nameof(PlanEntitlements.MaxActiveInvites);
    public const string EmailsPerMonth = nameof(PlanEntitlements.EmailsPerMonth);
    public const string ExtensionCommentsPerMonth = nameof(PlanEntitlements.ExtensionCommentsPerMonth);
    public const string MaxPendingSuggestions = nameof(PlanEntitlements.MaxPendingSuggestions);
    public const string ExportImportEnabled = nameof(PlanEntitlements.ExportImportEnabled);
    public const string PromptSuggestionsEnabled = nameof(PlanEntitlements.PromptSuggestionsEnabled);
    public const string CustomStatusesEnabled = nameof(PlanEntitlements.CustomStatusesEnabled);
    public const string PrioritySupport = nameof(PlanEntitlements.PrioritySupport);

    private static EntitlementSpec Int(string key, string label, bool enforced, int def) =>
        new(key, label, EntitlementKind.Int, enforced, def, false);

    private static EntitlementSpec Bool(string key, string label, bool enforced, bool def) =>
        new(key, label, EntitlementKind.Bool, enforced, 0, def);

    /// <summary>
    /// The complete, closed catalog. The <c>DefaultInt</c>/<c>DefaultBool</c> here double as the
    /// <b>Free plan defaults</b> for seeding — the one AppSetting-backed override (emailsPerMonth) is
    /// applied by the seeder.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, EntitlementSpec> All =
        new EntitlementSpec[]
        {
            // Enforced
            Int(MaxProjects, "Projects", enforced: true, def: 3),
            Int(MaxSeats, "Seats", enforced: true, def: 5),
            Int(MaxCommentsPerMonth, "Comments / month", enforced: true, def: 100),
            Bool(ExtensionEnabled, "Browser extension", enforced: true, def: false),
            Int(MaxExtensionSites, "Extension sites", enforced: true, def: 1),
            Int(MaxPredefinedActionsPerProject, "Predefined actions / project", enforced: true, def: 10),
            Int(MaxTenantWidePredefinedActions, "Tenant-wide predefined actions", enforced: true, def: 10),
            // Display-only
            Int(RetentionDays, "Retention (days)", enforced: false, def: 90),
            Int(MaxEnvironments, "Environments", enforced: false, def: 3),
            Int(MaxActiveInvites, "Active invites", enforced: false, def: 5),
            Int(EmailsPerMonth, "Emails / month", enforced: false, def: 100),
            Int(ExtensionCommentsPerMonth, "Extension comments / month", enforced: false, def: 100),
            Int(MaxPendingSuggestions, "Pending suggestions", enforced: false, def: 20),
            Bool(ExportImportEnabled, "Export / import", enforced: false, def: false),
            Bool(PromptSuggestionsEnabled, "Prompt suggestions", enforced: false, def: false),
            Bool(CustomStatusesEnabled, "Custom statuses", enforced: false, def: false),
            Bool(PrioritySupport, "Priority support", enforced: false, def: false),
        }.ToDictionary(s => s.Key);

    public static bool IsKnown(string key) => All.ContainsKey(key);

    public static IEnumerable<EntitlementSpec> Enforced => All.Values.Where(s => s.Enforced);

    /// <summary>
    /// Resolves the effective int value for a key from an entitlements bag: the stored value if present,
    /// otherwise the catalog default. NEVER returns 0 for a missing key (G7).
    /// </summary>
    public static int ResolveInt(PlanEntitlements e, string key)
    {
        var spec = All[key];
        var stored = (int?)GetProp(e, key);
        return stored ?? spec.DefaultInt;
    }

    /// <summary>Resolves the effective bool value for a key: stored if present, else catalog default.</summary>
    public static bool ResolveBool(PlanEntitlements e, string key)
    {
        var spec = All[key];
        var stored = (bool?)GetProp(e, key);
        return stored ?? spec.DefaultBool;
    }

    private static object? GetProp(PlanEntitlements e, string key)
    {
        var prop = typeof(PlanEntitlements).GetProperty(key,
            BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(e);
    }

    /// <summary>The set of VO property names — used by the sync test and validators.</summary>
    public static IReadOnlySet<string> VoPropertyNames { get; } =
        typeof(PlanEntitlements)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToHashSet();
}
