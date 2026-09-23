using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Backs the three usage-insights endpoints (super-admin platform, workspace-admin own-tenant,
/// anonymous public). All three share the same funnel/verification/device/language building blocks
/// over an in-memory materialized comment list — matching the approach
/// <see cref="AiRuleService.GetInsightsAsync"/> already uses for its own tool-usage/tenant summaries.
/// Fine at current volume; a DB-side aggregation (and a DB-side percentile for the medians) would be
/// needed to scale much past a few hundred thousand comments.
/// </summary>
public class PlatformInsightsService : IPlatformInsightsService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public PlatformInsightsService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<PlatformInsightsResponse>> GetPlatformInsightsAsync()
    {
        if (!_currentUser.IsSuperAdmin)
            return Result<PlatformInsightsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-13 (F2): the six-filter narrowing means a plain operator's query filter no longer
        // bypasses Comment/UsageEvent — IgnoreQueryFilters() explicitly for this cross-tenant view.
        // Still metadata-only: CommentRow below carries no body/element/custom_fields.
        var comments = await LoadCommentsAsync(ignoreFilters: true);
        var users = await _unitOfWork
            .Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => u.DeletedAt == null)
            .Select(u => new { u.Language })
            .ToListAsync();
        var langEvents = await LoadWidgetLanguageEventsAsync(ignoreFilters: true);

        var tenantNames = await BuildTenantNameMapAsync(comments.Select(c => c.OwnerId));

        return Result<PlatformInsightsResponse>.Success(
            new PlatformInsightsResponse
            {
                Languages = BuildLanguageInsights(
                    comments,
                    users.Select(u => u.Language),
                    langEvents
                ),
                Funnel = BuildFunnel(comments, includeByWorkspace: true, tenantNames),
                Verification = BuildVerification(comments),
                Devices = BuildDevices(comments),
                Features = BuildFeatures(comments),
            }
        );
    }

    public async Task<Result<WorkspaceInsightsResponse>> GetWorkspaceInsightsAsync()
    {
        if (!_currentUser.IsAdmin && !_currentUser.IsSuperAdmin)
            return Result<WorkspaceInsightsResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // Already tenant-scoped by the EF query filters (or, for a super admin calling this
        // endpoint, the all-tenants view — acceptable per the plan; the dashboard only shows this
        // block to workspace admins).
        var comments = await LoadCommentsAsync(ignoreFilters: false);

        return Result<WorkspaceInsightsResponse>.Success(
            new WorkspaceInsightsResponse
            {
                Funnel = BuildFunnel(
                    comments,
                    includeByWorkspace: false,
                    tenantNames: new Dictionary<Guid, string>()
                ),
                ByProject = BuildByProject(comments),
                Verification = BuildVerification(comments),
                CommentLanguages = GroupCount(
                    comments.Select(c => c.Language),
                    unsetLabel: "unknown"
                ),
                Devices = BuildDevices(comments),
                Activity = BuildActivity(comments),
            }
        );
    }

    public async Task<Result<PublicStatsResponse>> GetPublicStatsAsync()
    {
        // Anonymous — no tenant on the caller, so the default query filter would hide every
        // tenant-owned row. This is the one path that deliberately reaches across all tenants,
        // explicitly re-applying the soft-delete check IgnoreQueryFilters() also lifts.
        var comments = await LoadCommentsAsync(ignoreFilters: true);

        var projects = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Select(p => new
            {
                p.Id,
                p.OwnerId,
                p.AiToolsUsed,
            })
            .ToListAsync();

        var response = new PublicStatsResponse();

        var appliedCount = comments.Count(c => c.AppliedAt != null);
        response.AppliedComments =
            appliedCount >= PlatformInsightsConstants.MinAppliedComments
                ? PlatformInsightsConstants.RoundDown(appliedCount)
                : null;

        var projectCount = projects.Count;
        response.Projects =
            projectCount >= PlatformInsightsConstants.MinProjects
                ? PlatformInsightsConstants.RoundDown(projectCount)
                : null;

        var workspaceCount = projects
            .Where(p => p.OwnerId.HasValue)
            .Select(p => p.OwnerId!.Value)
            .Distinct()
            .Count();
        response.Workspaces =
            workspaceCount >= PlatformInsightsConstants.MinWorkspaces
                ? PlatformInsightsConstants.RoundDown(workspaceCount)
                : null;

        response.MedianHoursToApply =
            appliedCount >= PlatformInsightsConstants.MinAppliedForMedian
                ? Median(
                    comments
                        .Where(c => c.AppliedAt != null)
                        .Select(c => (c.AppliedAt!.Value - c.CreatedAt).TotalHours)
                )
                : null;

        // Languages: distinct comment languages seen in >= N distinct projects.
        var langByProjectCount = comments
            .Where(c => !string.IsNullOrWhiteSpace(c.Language))
            .GroupBy(c => c.Language!)
            .Select(g => new
            {
                Lang = g.Key,
                Projects = g.Select(c => c.ProjectId).Distinct().Count(),
            })
            .Where(g => g.Projects >= PlatformInsightsConstants.MinDistinctProjectsForListEntry)
            .Select(g => g.Lang)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();
        response.Languages =
            langByProjectCount.Count >= PlatformInsightsConstants.MinListEntriesToPublish
                ? langByProjectCount
                : new List<string>();

        // AI tools: distinct tool names seen in >= N distinct projects (a project can list several).
        var toolProjectCounts = new Dictionary<string, HashSet<int>>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var p in projects)
        {
            foreach (var tool in ParseStringList(p.AiToolsUsed))
            {
                if (!toolProjectCounts.TryGetValue(tool, out var set))
                    toolProjectCounts[tool] = set = new HashSet<int>();
                set.Add(p.Id);
            }
        }
        var tools = toolProjectCounts
            .Where(kv =>
                kv.Value.Count >= PlatformInsightsConstants.MinDistinctProjectsForListEntry
            )
            .Select(kv => kv.Key)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        response.AiTools =
            tools.Count >= PlatformInsightsConstants.MinListEntriesToPublish
                ? tools
                : new List<string>();

        return Result<PublicStatsResponse>.Success(response);
    }

    // ---- shared building blocks -------------------------------------------------------------

    /// <summary>Lightweight projection of the comment fields every insights view needs — avoids
    /// materializing full entities (owned Element/PickedActions JSON columns) for a plain count.</summary>
    private sealed record CommentRow(
        int ProjectId,
        string ProjectKey,
        string ProjectName,
        Guid? OwnerId,
        CommentStatus Status,
        DateTime CreatedAt,
        DateTime? AppliedAt,
        DateTime? VerifiedAt,
        string? Language,
        string? DeviceType,
        string? UserAgent,
        bool HasScreenshot,
        bool IsBugReport,
        bool HasPredefinedActions,
        bool IsPrivate
    );

    private async Task<List<CommentRow>> LoadCommentsAsync(bool ignoreFilters)
    {
        // Materialize the full entity (Include(Project) for the key/name join) rather than
        // projecting the owned Element/PickedActions JSON columns server-side — this codebase
        // doesn't project into those owned collections anywhere else, and Postgres/JSON-column
        // translation for a nested collection Count() is not something to gamble on. Mapping down
        // to the lean CommentRow happens after materialization, entirely in memory.
        IQueryable<Comment> query = _unitOfWork
            .Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Include(c => c.Project);
        if (ignoreFilters)
            query = query.IgnoreQueryFilters();

        var entities = await query.Where(c => c.DeletedAt == null).ToListAsync();

        return entities
            .Select(c => new CommentRow(
                c.ProjectId,
                c.Project.Key,
                c.Project.Name,
                c.OwnerId,
                c.Status,
                c.CreatedAt,
                c.AppliedAt,
                c.VerifiedAt,
                c.Language,
                c.Element.DeviceType,
                c.Element.UserAgent,
                c.Element.ScreenshotUrl != null,
                c.IsBugReport,
                c.PickedActions.Count > 0,
                c.IsPrivate
            ))
            .ToList();
    }

    private async Task<List<UsageEvent>> LoadWidgetLanguageEventsAsync(bool ignoreFilters)
    {
        var query = _unitOfWork.UsageEvents.AsNoTracking().Where(e => e.Type == "widget_language");
        if (ignoreFilters)
            query = query.IgnoreQueryFilters();
        return await query.ToListAsync();
    }

    private async Task<Dictionary<Guid, string>> BuildTenantNameMapAsync(
        IEnumerable<Guid?> ownerIds
    )
    {
        var ids = ownerIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name);
    }

    private sealed class WidgetLanguageMeta
    {
        [JsonPropertyName("ui")]
        public string? Ui { get; set; }

        [JsonPropertyName("browser")]
        public string? Browser { get; set; }

        [JsonPropertyName("page")]
        public string? Page { get; set; }
    }

    private static LanguageInsights BuildLanguageInsights(
        List<CommentRow> comments,
        IEnumerable<string?> userLanguages,
        List<UsageEvent> langEvents
    )
    {
        var parsed = new List<(int? ProjectId, WidgetLanguageMeta Meta)>();
        foreach (var e in langEvents)
        {
            if (string.IsNullOrWhiteSpace(e.Meta))
                continue;
            try
            {
                var meta = JsonSerializer.Deserialize<WidgetLanguageMeta>(e.Meta);
                if (meta != null)
                    parsed.Add((e.ProjectId, meta));
            }
            catch
            { /* malformed meta — skip, never fail the whole insights call over one bad row */
            }
        }

        List<CountStat> DistinctProjectsByLang(Func<WidgetLanguageMeta, string?> pick) =>
            parsed
                .Where(p => p.ProjectId.HasValue && !string.IsNullOrWhiteSpace(pick(p.Meta)))
                .GroupBy(p => pick(p.Meta)!)
                .Select(g => new CountStat
                {
                    Key = g.Key,
                    Count = g.Select(p => p.ProjectId!.Value).Distinct().Count(),
                })
                .OrderByDescending(s => s.Count)
                .ToList();

        return new LanguageInsights
        {
            UserLanguages = GroupCount(userLanguages, unsetLabel: "unset"),
            CommentLanguages = GroupCount(comments.Select(c => c.Language), unsetLabel: "unknown"),
            AppLanguages = DistinctProjectsByLang(m => m.Page),
            BrowserLanguages = DistinctProjectsByLang(m => m.Browser),
        };
    }

    /// <summary>Groups raw values into (key, count) buckets. <paramref name="normalizeCase"/> lower-
    /// cases the value first — right for freeform language tags (a stored "EN" and "en" are the same
    /// bucket), wrong for the already-canonical device-type/browser-family labels this same helper
    /// also builds, so those callers pass false.</summary>
    private static List<CountStat> GroupCount(
        IEnumerable<string?> values,
        string unsetLabel,
        bool normalizeCase = true
    ) =>
        values
            .Select(v =>
                string.IsNullOrWhiteSpace(v)
                    ? unsetLabel
                    : (normalizeCase ? v!.Trim().ToLowerInvariant() : v!.Trim())
            )
            .GroupBy(v => v)
            .Select(g => new CountStat { Key = g.Key, Count = g.Count() })
            .OrderByDescending(s => s.Count)
            .ToList();

    private static FunnelInsights BuildFunnel(
        List<CommentRow> comments,
        bool includeByWorkspace,
        Dictionary<Guid, string> tenantNames
    )
    {
        var funnel = new FunnelInsights
        {
            Open = comments.Count(c => c.Status == CommentStatus.Open),
            Ready = comments.Count(c => c.Status == CommentStatus.ReadyToApply),
            Applied = comments.Count(c => c.Status == CommentStatus.Applied),
            Archived = comments.Count(c => c.Status == CommentStatus.Archived),
            Verified = comments.Count(c => c.VerifiedAt != null),
            MedianHoursCreatedToApplied = Median(
                comments
                    .Where(c => c.AppliedAt != null)
                    .Select(c => (c.AppliedAt!.Value - c.CreatedAt).TotalHours)
            ),
            MedianHoursAppliedToVerified = Median(
                comments
                    .Where(c => c.AppliedAt != null && c.VerifiedAt != null)
                    .Select(c => (c.VerifiedAt!.Value - c.AppliedAt!.Value).TotalHours)
            ),
        };

        if (includeByWorkspace)
        {
            funnel.ByWorkspace = comments
                .Where(c => c.OwnerId.HasValue)
                .GroupBy(c => c.OwnerId!.Value)
                .Select(g => new WorkspaceFunnelStat
                {
                    TenantId = g.Key,
                    TenantName = tenantNames.TryGetValue(g.Key, out var name)
                        ? name
                        : "Workspace " + g.Key.ToString()[..8],
                    Open = g.Count(c => c.Status == CommentStatus.Open),
                    Ready = g.Count(c => c.Status == CommentStatus.ReadyToApply),
                    Applied = g.Count(c => c.Status == CommentStatus.Applied),
                    Verified = g.Count(c => c.VerifiedAt != null),
                })
                .OrderByDescending(w => w.Applied)
                .ToList();
        }

        return funnel;
    }

    private static List<ProjectFunnelStat> BuildByProject(List<CommentRow> comments) =>
        comments
            .GroupBy(c => new
            {
                c.ProjectId,
                c.ProjectKey,
                c.ProjectName,
            })
            .Select(g => new ProjectFunnelStat
            {
                ProjectKey = g.Key.ProjectKey,
                ProjectName = g.Key.ProjectName,
                Open = g.Count(c => c.Status == CommentStatus.Open),
                Ready = g.Count(c => c.Status == CommentStatus.ReadyToApply),
                Applied = g.Count(c => c.Status == CommentStatus.Applied),
                Verified = g.Count(c => c.VerifiedAt != null),
                MedianHoursCreatedToApplied = Median(
                    g.Where(c => c.AppliedAt != null)
                        .Select(c => (c.AppliedAt!.Value - c.CreatedAt).TotalHours)
                ),
            })
            .OrderByDescending(p => p.Open + p.Ready + p.Applied)
            .ToList();

    /// <summary>
    /// NotFixed/AwaitingVerification are proxies, not direct fields — see
    /// <see cref="VerificationInsights"/>'s doc comment. "Applied" for AwaitingVerification means the
    /// comment's CURRENT status is still Applied (never re-opened); a re-opened comment is counted
    /// under NotFixed instead, so the two buckets don't double-count the same row.
    /// </summary>
    private static VerificationInsights BuildVerification(List<CommentRow> comments)
    {
        var verified = comments.Count(c => c.VerifiedAt != null);
        var notFixed = comments.Count(c => c.AppliedAt != null && c.Status == CommentStatus.Open);
        var awaiting = comments.Count(c =>
            c.Status == CommentStatus.Applied && c.VerifiedAt == null
        );
        var denom = verified + notFixed;

        return new VerificationInsights
        {
            Verified = verified,
            NotFixed = notFixed,
            AwaitingVerification = awaiting,
            NotFixedRate = denom > 0 ? (double)notFixed / denom : null,
        };
    }

    private static DeviceInsights BuildDevices(List<CommentRow> comments) =>
        new()
        {
            DeviceTypes = GroupCount(
                comments.Select(c => c.DeviceType),
                unsetLabel: "unknown",
                normalizeCase: false
            ),
            Browsers = GroupCount(
                comments.Select(c => BrowserFamily(c.UserAgent)),
                unsetLabel: "Other",
                normalizeCase: false
            ),
        };

    private static string BrowserFamily(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return "Other";
        if (
            userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Edge/", StringComparison.OrdinalIgnoreCase)
        )
            return "Edge";
        if (userAgent.Contains("Chrome/", StringComparison.OrdinalIgnoreCase))
            return "Chrome";
        if (userAgent.Contains("Firefox/", StringComparison.OrdinalIgnoreCase))
            return "Firefox";
        if (userAgent.Contains("Safari/", StringComparison.OrdinalIgnoreCase))
            return "Safari";
        return "Other";
    }

    private static FeatureInsights BuildFeatures(List<CommentRow> comments) =>
        new()
        {
            Total = comments.Count,
            WithScreenshot = comments.Count(c => c.HasScreenshot),
            BugReports = comments.Count(c => c.IsBugReport),
            WithPredefinedActions = comments.Count(c => c.HasPredefinedActions),
            Private = comments.Count(c => c.IsPrivate),
        };

    private static DateOnly MondayOf(DateTime utc)
    {
        var date = DateOnly.FromDateTime(utc);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7; // Sunday=0 -> 6, Monday=1 -> 0, ...
        return date.AddDays(-daysSinceMonday);
    }

    private static List<WeekStat> BuildActivity(List<CommentRow> comments)
    {
        var currentMonday = MondayOf(DateTime.UtcNow);
        var weekStarts = Enumerable
            .Range(0, 8)
            .Select(i => currentMonday.AddDays(-7 * (7 - i)))
            .ToList();

        return weekStarts
            .Select(weekStart =>
            {
                var weekEnd = weekStart.AddDays(7);
                bool InWeek(DateTime dt)
                {
                    var d = DateOnly.FromDateTime(dt);
                    return d >= weekStart && d < weekEnd;
                }

                return new WeekStat
                {
                    WeekStart = weekStart,
                    Created = comments.Count(c => InWeek(c.CreatedAt)),
                    Applied = comments.Count(c =>
                        c.AppliedAt.HasValue && InWeek(c.AppliedAt.Value)
                    ),
                    Verified = comments.Count(c =>
                        c.VerifiedAt.HasValue && InWeek(c.VerifiedAt.Value)
                    ),
                };
            })
            .ToList();
    }

    private static double? Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(v => v).ToList();
        if (list.Count == 0)
            return null;
        var mid = list.Count / 2;
        return list.Count % 2 == 0 ? (list[mid - 1] + list[mid]) / 2.0 : list[mid];
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json);
            return list?.Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
