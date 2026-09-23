namespace Pointer.Application.DTOs.Stats;

/// <summary>
/// DB-15: the F4 activation funnel across every workspace — five one-shot steps, the weekly
/// "activated workspaces" series (D15.3) and the most recent workspaces on the ladder. Backs
/// <c>GET /api/admin/stats/funnel</c> (<c>Policies.SuperAdmin</c>); metadata only (counts, no
/// content), so it works for a plain operator without an impersonation session (DB-13).
/// </summary>
public class ActivationFunnelResponse
{
    /// <summary>Monday 00:00 UTC of the oldest week in <see cref="Weeks"/>.</summary>
    public DateTime From { get; set; }

    /// <summary>Now (UTC) — the series runs to the current in-progress week.</summary>
    public DateTime To { get; set; }

    /// <summary>Exactly five entries, funnel order. All-time distinct workspaces per step.</summary>
    public List<ActivationStepStat> Steps { get; set; } = new();

    /// <summary>Exactly <c>weeks</c> entries, oldest first, ending with the current week — weeks
    /// with no activity still appear with zeros so a chart has fixed points.</summary>
    public List<ActivationWeekStat> Weeks { get; set; } = new();

    /// <summary>Workspaces whose EARLIEST first_apply falls in the current ISO week (D15.3).</summary>
    public int ActivatedThisWeek { get; set; }

    public int ActivatedLastWeek { get; set; }

    /// <summary>Last 50 workspaces by when they reached their deepest step. A hard-deleted
    /// workspace keeps its place with <c>WorkspaceId = null</c> and the name "Deleted workspace".</summary>
    public List<ActivationWorkspaceRow> Recent { get; set; } = new();
}

public class ActivationStepStat
{
    /// <summary>A <c>UsageEventTypes</c> one-shot fact key — the dashboard maps keys to i18n.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>English fallback label; see <see cref="ActivationStepLabels"/>.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>All-time distinct workspaces with a row of this type. A NULL-owner row (hard-deleted
    /// workspace) counts — each such row is its own pseudo-workspace, never merged (DB-15 §3.1).</summary>
    public int Workspaces { get; set; }

    /// <summary>Of those, the ones that entered via a demo (a demo_started row exists for the same
    /// workspace).</summary>
    public int DemoPathWorkspaces { get; set; }

    /// <summary>Workspaces[step] / Workspaces[step-1]; null for the first step or a zero denominator.</summary>
    public double? RateFromPrevious { get; set; }
}

/// <summary>One ISO week (Monday-start, UTC) of funnel counts. <see cref="DemosStarted"/> and
/// <see cref="Converted"/> count raw rows of that type created in the week (one-shot per program
/// flow, but not deduplicated by workspace). <see cref="WidgetInstalled"/>, <see cref="FirstComment"/>
/// and <see cref="Activated"/> are "first per workspace": a workspace counts in the week of its
/// EARLIEST row of that type.</summary>
public class ActivationWeekStat
{
    public DateOnly WeekStart { get; set; }

    /// <summary>Count of demo_started rows created in the week (not first-per-workspace).</summary>
    public int DemosStarted { get; set; }

    /// <summary>Count of workspace_converted rows created in the week (not first-per-workspace).</summary>
    public int Converted { get; set; }
    public int WidgetInstalled { get; set; }
    public int FirstComment { get; set; }

    /// <summary>Workspaces activated this week — earliest first_apply in the week (D15.3).</summary>
    public int Activated { get; set; }
}

public class ActivationWorkspaceRow
{
    /// <summary>Null for a hard-deleted workspace (the row's owner was detached, FK SET NULL).</summary>
    public Guid? WorkspaceId { get; set; }
    public string WorkspaceName { get; set; } = string.Empty;
    public string StepReached { get; set; } = string.Empty;
    public DateTime ReachedAt { get; set; }
}

/// <summary>English fallback labels for the step keys — the dashboard maps keys to its own i18n
/// bundles and uses these only as fallbacks (DB-15 §3.5).</summary>
public static class ActivationStepLabels
{
    public const string DemoStarted = "Demo started";
    public const string WorkspaceConverted = "Converted to workspace";
    public const string WidgetInstalled = "Widget installed";
    public const string FirstComment = "First comment";
    public const string FirstApply = "First applied comment";
}
