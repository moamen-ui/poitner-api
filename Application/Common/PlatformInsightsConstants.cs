namespace Pointer.Application.Common;

/// <summary>
/// Thresholds and rounding for <c>GET /api/public/stats</c> — kept in one place so the
/// "never reveal a small, potentially tenant-identifying number" rule has a single source of truth.
/// See <c>PlatformInsightsService.GetPublicStatsAsync</c> and <c>Tests/PublicStatsTests.cs</c>.
/// </summary>
public static class PlatformInsightsConstants
{
    public const int MinAppliedComments = 50;
    public const int MinProjects = 10;
    public const int MinWorkspaces = 5;

    /// <summary>Minimum applied-comment sample before a median is published.</summary>
    public const int MinAppliedForMedian = 20;

    /// <summary>A language/AI-tool value is only surfaced once it appears in at least this many
    /// distinct projects.</summary>
    public const int MinDistinctProjectsForListEntry = 3;

    /// <summary>The Languages/AiTools lists are hidden entirely (returned empty) unless at least
    /// this many entries clear <see cref="MinDistinctProjectsForListEntry"/> — publishing one or two
    /// exact values can fingerprint a tenant more than an aggregate count does.</summary>
    public const int MinListEntriesToPublish = 3;

    /// <summary>Rounds a count down for public display: to the nearest 10 below 1 000, to the
    /// nearest 100 at or above 1 000. Never rounds up — that would overstate usage.</summary>
    public static int RoundDown(int value)
    {
        var step = value >= 1000 ? 100 : 10;
        return value / step * step;
    }
}
