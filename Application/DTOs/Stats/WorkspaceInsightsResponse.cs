namespace Pointer.Application.DTOs.Stats;

/// <summary>
/// Workspace-admin insights, scoped to the caller's own tenant by the same EF query filters every
/// other tenant-scoped read already relies on (no extra <c>TenantId</c> filter needed here — see
/// <c>PlatformInsightsService.GetWorkspaceInsightsAsync</c>). Backs
/// <c>GET /api/admin/stats/workspace-insights</c> (<c>Policies.Admin</c>).
/// </summary>
public class WorkspaceInsightsResponse
{
    /// <summary><see cref="FunnelInsights.ByWorkspace"/> is always empty here — that list is the
    /// super-admin cross-tenant breakdown, meaningless for a single-tenant caller.</summary>
    public FunnelInsights Funnel { get; set; } = new();
    public List<ProjectFunnelStat> ByProject { get; set; } = new();
    public VerificationInsights Verification { get; set; } = new();
    public List<CountStat> CommentLanguages { get; set; } = new();
    public DeviceInsights Devices { get; set; } = new();

    /// <summary>Always exactly 8 entries, oldest first, ending with the current (in-progress) week —
    /// weeks with no activity still appear with zeros so a chart has a fixed 8 points.</summary>
    public List<WeekStat> Activity { get; set; } = new();
}

public class ProjectFunnelStat
{
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public int Open { get; set; }
    public int Ready { get; set; }
    public int Applied { get; set; }
    public int Verified { get; set; }
    public double? MedianHoursCreatedToApplied { get; set; }
}

/// <summary>One ISO week (Monday-start, UTC) of activity counts.</summary>
public class WeekStat
{
    public DateOnly WeekStart { get; set; }
    public int Created { get; set; }
    public int Applied { get; set; }
    public int Verified { get; set; }
}
