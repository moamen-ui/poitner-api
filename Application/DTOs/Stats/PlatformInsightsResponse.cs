namespace Pointer.Application.DTOs.Stats;

/// <summary>
/// Super-admin, cross-tenant usage insights — one composite call mirroring
/// <see cref="Pointer.Application.DTOs.AiRule.AiInsightsResponse"/>'s "one call, many lists" shape.
/// Backs <c>GET /api/admin/stats/insights</c> (<c>Policies.SuperAdmin</c>).
/// </summary>
public class PlatformInsightsResponse
{
    public LanguageInsights Languages { get; set; } = new();
    public FunnelInsights Funnel { get; set; } = new();
    public VerificationInsights Verification { get; set; } = new();
    public DeviceInsights Devices { get; set; } = new();
    public FeatureInsights Features { get; set; } = new();
}

/// <summary>A single (key, count) bucket — e.g. a language tag or a device type and how many rows
/// carried it.</summary>
public class CountStat
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>
/// Language usage across the four independent surfaces that can each carry a different language:
/// the account's own UI preference, the text of a comment (detected client-side, never derived
/// from the widget's UI language), the host app's <c>&lt;html lang&gt;</c>, and the visiting
/// browser's own locale. Buckets use <c>"unset"</c>/<c>"unknown"</c> rather than dropping rows, so
/// the counts still sum to the total population.
/// </summary>
public class LanguageInsights
{
    /// <summary><see cref="Pointer.Domain.Entity.User.Language"/>, grouped; null becomes "unset".</summary>
    public List<CountStat> UserLanguages { get; set; } = new();

    /// <summary><see cref="Pointer.Domain.Entity.Comment.Language"/>, grouped; null becomes "unknown".</summary>
    public List<CountStat> CommentLanguages { get; set; } = new();

    /// <summary>Distinct-project counts of the host page's <c>document.documentElement.lang</c>,
    /// from <c>widget_language</c> usage events' <c>meta.page</c>.</summary>
    public List<CountStat> AppLanguages { get; set; } = new();

    /// <summary>Distinct-project counts of the visiting browser's <c>navigator.language</c>, from
    /// <c>widget_language</c> usage events' <c>meta.browser</c>.</summary>
    public List<CountStat> BrowserLanguages { get; set; } = new();
}

/// <summary>
/// Status funnel + the two timing medians the data actually supports. There is no "ready"
/// timestamp on <see cref="Pointer.Domain.Entity.Comment"/> (only <c>Status == ReadyToApply</c> at
/// read time), so a created→ready median is deliberately not reported here.
/// </summary>
public class FunnelInsights
{
    public int Open { get; set; }
    public int Ready { get; set; }
    public int Applied { get; set; }
    public int Archived { get; set; }
    public int Verified { get; set; }

    /// <summary>Median of (AppliedAt - CreatedAt) in hours, over comments that have an AppliedAt.
    /// Null when there are none. Computed in memory — fine at current volume; would need a DB-side
    /// percentile function to scale past a few hundred thousand rows.</summary>
    public double? MedianHoursCreatedToApplied { get; set; }

    /// <summary>Median of (VerifiedAt - AppliedAt) in hours, over comments that have both. Null when
    /// there are none.</summary>
    public double? MedianHoursAppliedToVerified { get; set; }

    /// <summary>Cross-tenant breakdown — populated only by the super-admin platform view; the
    /// workspace-admin view leaves this empty (it's already scoped to one tenant).</summary>
    public List<WorkspaceFunnelStat> ByWorkspace { get; set; } = new();
}

public class WorkspaceFunnelStat
{
    public Guid TenantId { get; set; }
    public string TenantName { get; set; } = string.Empty;
    public int Open { get; set; }
    public int Ready { get; set; }
    public int Applied { get; set; }
    public int Verified { get; set; }
}

/// <summary>
/// Fix-quality signal. <c>NotFixed</c> is a proxy, not a direct field: a comment that was applied
/// and is once again <c>Status == Open</c> (the verify-reject flow re-opens it; a manual re-open
/// after an apply reads the same way for this purpose) is counted as "applied but not actually
/// fixed." <c>AwaitingVerification</c> is applied and still open-ended (no VerifiedAt yet, and not
/// re-opened). <c>NotFixedRate</c> is null when there's nothing to divide by yet.
/// </summary>
public class VerificationInsights
{
    public int Verified { get; set; }
    public int NotFixed { get; set; }
    public int AwaitingVerification { get; set; }
    public double? NotFixedRate { get; set; }
}

public class DeviceInsights
{
    public List<CountStat> DeviceTypes { get; set; } = new();
    public List<CountStat> Browsers { get; set; } = new();
}

public class FeatureInsights
{
    public int Total { get; set; }
    public int WithScreenshot { get; set; }
    public int BugReports { get; set; }
    public int WithPredefinedActions { get; set; }
    public int Private { get; set; }
}
