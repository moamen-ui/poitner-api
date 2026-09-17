namespace Pointer.Application.DTOs.Stats;

/// <summary>
/// Anonymous, anonymized stats for the landing page (<c>GET /api/public/stats</c>). Every numeric
/// field is null until its underlying count clears a minimum threshold, and the ones that clear it
/// are rounded down (never up — never implies more usage than there is). Nothing here is
/// per-tenant, no names, no dates — see <c>PlatformInsightsConstants</c> for the exact thresholds
/// and rounding, and <c>PlatformInsightsService.GetPublicStatsAsync</c> for where they're applied.
/// </summary>
public class PublicStatsResponse
{
    public int? AppliedComments { get; set; }
    public int? Projects { get; set; }
    public int? Workspaces { get; set; }

    /// <summary>Distinct comment languages seen in at least 3 distinct projects. Empty below that.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Distinct AI tools (<see cref="Pointer.Domain.Entity.Project.AiToolsUsed"/>) seen in
    /// at least 3 distinct projects. Empty below that.</summary>
    public List<string> AiTools { get; set; } = new();

    public double? MedianHoursToApply { get; set; }
}
