using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// DB-15: the activation funnel (F4) computed in memory from the five one-shot facts — the
/// <see cref="PlatformInsightsService"/> approach (materialize the tiny one-shot set, aggregate in
/// memory; ≤ 5 rows per workspace/project). The NULL-owner rule of §3.1 applies everywhere: a
/// one-shot row whose workspace was hard-deleted counts toward its step via its own created_at and
/// never merges with another NULL-owner row (key on the row's own id).
/// </summary>
public class ActivationStatsService : IActivationStatsService
{
    private const int RecentLimit = 50;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public ActivationStatsService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<ActivationFunnelResponse>> GetFunnelAsync(int weeks = 12)
    {
        if (!_currentUser.IsSuperAdmin)
            return Result<ActivationFunnelResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (weeks is < 1 or > 52)
            return Result<ActivationFunnelResponse>.Failure(MessageKeys.Stats.WeeksOutOfRange);

        // DB-13: counts only, no content — a plain operator reads across every workspace WITHOUT an
        // impersonation session (the StatsService shape). An impersonating operator is pinned to
        // the token's target workspace instead.
        IQueryable<UsageEvent> query = _unitOfWork.UsageEvents.AsNoTracking();
        if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating)
            query = query.IgnoreQueryFilters();
        else if (_currentUser.TenantId is Guid target)
            query = query.Where(e => e.OwnerId == target);

        var facts = await query
            .Where(e => UsageEventTypes.OneShotFacts.Contains(e.Type))
            .Select(e => new
            {
                e.Id,
                e.OwnerId,
                e.Type,
                e.CreatedAt,
            })
            .ToListAsync();

        static string KeyOf(Guid? ownerId, int rowId) => ownerId?.ToString() ?? $"deleted:{rowId}";

        var byType = facts.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.ToList());

        // Earliest created_at per workspace key, per step — every weekly series is "first per
        // workspace" so a workspace is counted exactly once, in the week it reached the step.
        var firstPerKey = UsageEventTypes.OneShotFacts.ToDictionary(
            step => step,
            step =>
                byType
                    .GetValueOrDefault(step)
                    ?.GroupBy(e => KeyOf(e.OwnerId, e.Id))
                    .ToDictionary(g => g.Key, g => g.Min(e => e.CreatedAt))
                ?? new Dictionary<string, DateTime>()
        );

        var demoKeys = firstPerKey[UsageEventTypes.DemoStarted].Keys.ToHashSet();

        // All-time steps, funnel order, with step-over-step ratios.
        var steps = new List<ActivationStepStat>();
        int? previous = null;
        foreach (var step in UsageEventTypes.OneShotFacts)
        {
            var count = firstPerKey[step].Count;
            steps.Add(
                new ActivationStepStat
                {
                    Key = step,
                    Label = LabelFor(step),
                    Workspaces = count,
                    DemoPathWorkspaces = firstPerKey[step].Keys.Count(demoKeys.Contains),
                    RateFromPrevious = previous is > 0 ? (double)count / previous.Value : null,
                }
            );
            previous = count;
        }

        // The weekly window: `weeks` ISO weeks ending with the current in-progress one.
        var currentMonday = MondayOf(DateTime.UtcNow);
        var weekStarts = Enumerable
            .Range(0, weeks)
            .Select(i => currentMonday.AddDays(-7 * (weeks - 1 - i)))
            .ToList();

        var weekly = weekStarts
            .Select(weekStart =>
            {
                var weekEnd = weekStart.AddDays(7);
                bool InWeek(DateTime dt)
                {
                    var d = DateOnly.FromDateTime(dt);
                    return d >= weekStart && d < weekEnd;
                }

                return new ActivationWeekStat
                {
                    WeekStart = weekStart,
                    DemosStarted =
                        byType
                            .GetValueOrDefault(UsageEventTypes.DemoStarted)
                            ?.Count(e => InWeek(e.CreatedAt))
                        ?? 0,
                    Converted =
                        byType
                            .GetValueOrDefault(UsageEventTypes.WorkspaceConverted)
                            ?.Count(e => InWeek(e.CreatedAt))
                        ?? 0,
                    WidgetInstalled = firstPerKey[UsageEventTypes.WidgetInstalled]
                        .Count(kv => InWeek(kv.Value)),
                    FirstComment = firstPerKey[UsageEventTypes.FirstComment]
                        .Count(kv => InWeek(kv.Value)),
                    Activated = firstPerKey[UsageEventTypes.FirstApply]
                        .Count(kv => InWeek(kv.Value)),
                };
            })
            .ToList();

        // D15.3's weekly number, pulled out for the headline.
        var thisWeek = weekly[^1];
        var lastWeek = weekly.Count > 1 ? weekly[^2] : null;

        // Recent: the deepest step each workspace reached, by when it reached it. Iterating the
        // steps in funnel order makes the last write per key the deepest step.
        var rowsByStep = facts
            .GroupBy(e => e.Type)
            .ToDictionary(
                g => g.Key,
                g =>
                    g.GroupBy(e => KeyOf(e.OwnerId, e.Id))
                        .Select(g2 => new
                        {
                            Key = g2.Key,
                            ReachedAt = g2.Min(e => e.CreatedAt),
                            OwnerId = g2.First().OwnerId,
                        })
                        .ToList()
            );
        var deepest = new Dictionary<string, (string Step, DateTime ReachedAt, Guid? OwnerId)>();
        foreach (var step in UsageEventTypes.OneShotFacts)
        {
            foreach (var w in rowsByStep.GetValueOrDefault(step) ?? new())
                deepest[w.Key] = (step, w.ReachedAt, w.OwnerId);
        }

        var recentKeys = deepest
            .OrderByDescending(kv => kv.Value.ReachedAt)
            .Take(RecentLimit)
            .ToList();
        var names = await BuildWorkspaceNameMapAsync(
            recentKeys
                .Select(kv => kv.Value.OwnerId)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
        );

        return Result<ActivationFunnelResponse>.Success(
            new ActivationFunnelResponse
            {
                From = weekStarts[0].ToDateTime(TimeOnly.MinValue),
                To = DateTime.UtcNow,
                Steps = steps,
                Weeks = weekly,
                ActivatedThisWeek = thisWeek.Activated,
                ActivatedLastWeek = lastWeek?.Activated ?? 0,
                Recent = recentKeys
                    .Select(kv => new ActivationWorkspaceRow
                    {
                        WorkspaceId = kv.Value.OwnerId,
                        WorkspaceName = kv.Value.OwnerId.HasValue
                            ? names.GetValueOrDefault(kv.Value.OwnerId.Value, "Workspace")
                            : "Deleted workspace",
                        StepReached = kv.Value.Step,
                        ReachedAt = kv.Value.ReachedAt,
                    })
                    .ToList(),
            }
        );
    }

    public async Task<Result<WorkspaceActivationResponse>> GetActivationAsync()
    {
        Guid owner;
        if (_currentUser.IsImpersonating && _currentUser.TenantId is Guid target)
        {
            // DB-13: an impersonating operator owns nothing — the token names the workspace.
            owner = target;
        }
        else if (!TenantStamp.TryRequireOwner(_currentUser, out owner))
        {
            return Result<WorkspaceActivationResponse>.Forbidden(MessageKeys.Common.Forbidden);
        }

        // Query filter (strict-own) + the explicit owner predicate, per §3.5.
        var facts = await _unitOfWork
            .UsageEvents.AsNoTracking()
            .Where(e => e.OwnerId == owner && UsageEventTypes.OneShotFacts.Contains(e.Type))
            .Select(e => new
            {
                e.Type,
                e.ProjectId,
                e.CreatedAt,
            })
            .ToListAsync();

        var projectIds = facts
            .Where(f => f.ProjectId.HasValue)
            .Select(f => f.ProjectId!.Value)
            .Distinct()
            .ToList();
        var projectKeys = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Key);

        var response = new WorkspaceActivationResponse
        {
            IsDemo = facts.Any(f => f.Type == UsageEventTypes.DemoStarted),
        };

        // Demo steps only when this workspace actually started as a demo.
        foreach (
            var step in new[] { UsageEventTypes.DemoStarted, UsageEventTypes.WorkspaceConverted }
        )
        {
            var reached = facts.Where(f => f.Type == step).OrderBy(f => f.CreatedAt).ToList();
            if (reached.Count == 0)
                continue;
            response.Steps.Add(
                new ActivationStepStatus
                {
                    Key = step,
                    Label = LabelFor(step),
                    Done = true,
                    ReachedAt = reached[0].CreatedAt,
                }
            );
        }

        // The core three are always listed — done or not.
        foreach (
            var step in new[]
            {
                UsageEventTypes.WidgetInstalled,
                UsageEventTypes.FirstComment,
                UsageEventTypes.FirstApply,
            }
        )
        {
            var reached = facts.Where(f => f.Type == step).OrderBy(f => f.CreatedAt).ToList();
            var first = reached.FirstOrDefault();
            response.Steps.Add(
                new ActivationStepStatus
                {
                    Key = step,
                    Label = LabelFor(step),
                    Done = first != null,
                    ReachedAt = first?.CreatedAt,
                    ProjectKey = first?.ProjectId is int pid
                        ? projectKeys.GetValueOrDefault(pid)
                        : null,
                }
            );
        }

        response.NextStepKey = response
            .Steps.Where(s =>
                !s.Done
                && (
                    s.Key == UsageEventTypes.WidgetInstalled
                    || s.Key == UsageEventTypes.FirstComment
                    || s.Key == UsageEventTypes.FirstApply
                )
            )
            .Select(s => s.Key)
            .FirstOrDefault();

        return Result<WorkspaceActivationResponse>.Success(response);
    }

    private async Task<Dictionary<Guid, string>> BuildWorkspaceNameMapAsync(
        IEnumerable<Guid> ownerIds
    )
    {
        var ids = ownerIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();
        return await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => ids.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name);
    }

    internal static string LabelFor(string step) =>
        step switch
        {
            UsageEventTypes.DemoStarted => ActivationStepLabels.DemoStarted,
            UsageEventTypes.WorkspaceConverted => ActivationStepLabels.WorkspaceConverted,
            UsageEventTypes.WidgetInstalled => ActivationStepLabels.WidgetInstalled,
            UsageEventTypes.FirstComment => ActivationStepLabels.FirstComment,
            UsageEventTypes.FirstApply => ActivationStepLabels.FirstApply,
            _ => step,
        };

    private static DateOnly MondayOf(DateTime utc)
    {
        var date = DateOnly.FromDateTime(utc);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7; // Sunday=0 -> 6, Monday=1 -> 0, ...
        return date.AddDays(-daysSinceMonday);
    }
}
