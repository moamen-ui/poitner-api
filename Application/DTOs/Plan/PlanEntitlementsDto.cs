namespace Pointer.Application.DTOs.Plan;

/// <summary>
/// Mirrors <c>PlanEntitlements</c> (all nullable). A missing key resolves to the catalog default at
/// enforcement time — never 0/false. Super-admins edit these values; the key set is fixed in code.
/// </summary>
public class PlanEntitlementsDto
{
    public int? MaxProjects { get; set; }
    public int? MaxSeats { get; set; }
    public int? MaxCommentsPerMonth { get; set; }
    public bool? ExtensionEnabled { get; set; }
    public int? MaxExtensionSites { get; set; }
    public int? MaxPredefinedActionsPerProject { get; set; }
    public int? MaxTenantWidePredefinedActions { get; set; }

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
