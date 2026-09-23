using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-18 §3.4/§5 task 14: the self-service workspace-deletion job. Structure copies
/// <see cref="DemoCleanupService"/> — 30 s initial delay, then every <c>WorkspaceDeletion:SweepMinutes</c>
/// (default 15; clamp 1..60): step 1 sends due T-24h reminders, step 2 hard-deletes workspaces whose
/// grace period has elapsed, one at a time with a FRESH DI scope per item (same rationale as
/// <see cref="DemoCleanupService"/>'s remarks — a failed item's tracked entities must never leak into
/// the next one's transaction).
/// </summary>
public class WorkspaceDeletionService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<WorkspaceDeletionService> logger
) : BackgroundService
{
    /// <summary>Consecutive-failure counter per workspace id (Opus LOW: a stuck row must not retry
    /// forever silently — the 3rd consecutive non-skip failure logs Critical, watched at deploy).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid,
        int
    > FailureCounts = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await SweepAsync(stoppingToken);

            var sweepMinutes = WorkspaceDeletionConfig.SweepMinutes(configuration);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(sweepMinutes));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — exit cleanly.
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "WorkspaceDeletionService: starting sweep at {Time:u}",
            DateTime.UtcNow
        );

        // Step 1: T-24h reminders — its own scope, its own try/catch.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var lifecycle = scope.ServiceProvider.GetRequiredService<IWorkspaceLifecycleService>();
            await lifecycle.SendDueRemindersAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WorkspaceDeletionService: reminder step failed");
        }

        // Step 2: the grace-period sweep.
        try
        {
            var deleted = await SweepOnceAsync(scopeFactory, logger, stoppingToken);
            logger.LogInformation(
                "WorkspaceDeletionService: sweep complete — {Deleted} workspace(s) hard-deleted",
                deleted
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WorkspaceDeletionService: deletion sweep failed");
        }
    }

    /// <summary>Internal + static so tests can drive it directly (DemoCleanupService precedent).
    /// Returns the number of workspaces hard-deleted.</summary>
    internal static async Task<int> SweepOnceAsync(
        IServiceScopeFactory scopeFactory,
        ILogger log,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;

        List<Guid> dueIds;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            dueIds = await uow
                .Workspaces.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w =>
                    w.DeletedAt == null
                    && w.DeletionScheduledFor != null
                    && w.DeletionScheduledFor <= now
                    && !w.PausedByOperator
                )
                .Select(w => w.Id)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "WorkspaceDeletionService: failed to query due workspaces");
            return 0;
        }

        if (dueIds.Count == 0)
        {
            log.LogInformation("WorkspaceDeletionService: no due workspace deletions found");
            return 0;
        }

        var deleted = 0;
        foreach (var id in dueIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // A fresh scope PER ITEM — same rationale as DemoCleanupService.
                using var scope = scopeFactory.CreateScope();
                var lifecycle =
                    scope.ServiceProvider.GetRequiredService<IWorkspaceLifecycleService>();

                var result = await lifecycle.ExecuteDueDeletionAsync(id);
                if (result.IsSuccess)
                {
                    deleted++;
                    FailureCounts.TryRemove(id, out _);
                    log.LogInformation(
                        "WorkspaceDeletionService: hard-deleted {Id} (owner_requested)",
                        id
                    );
                }
                else
                {
                    FailureCounts.TryRemove(id, out _);
                    if (
                        string.Equals(
                            result.Message,
                            "held by operator pause",
                            StringComparison.Ordinal
                        )
                    )
                        log.LogInformation(
                            "WorkspaceDeletionService: {Id} held by operator pause",
                            id
                        );
                    else
                        log.LogInformation(
                            "WorkspaceDeletionService: {Id} skipped (cancelled): {Message}",
                            id,
                            result.Message
                        );
                }
            }
            catch (InvalidOperationException ex)
            {
                // The locked re-check inside HardDeleteAsync aborted — a cancel/pause raced the job.
                FailureCounts.TryRemove(id, out _);
                log.LogInformation(
                    "WorkspaceDeletionService: {Id} skipped ({Reason})",
                    id,
                    ex.Message
                );
            }
            catch (Exception ex)
            {
                log.LogError(ex, "WorkspaceDeletionService: FAILED {Id}", id);
                var count = FailureCounts.AddOrUpdate(id, 1, (_, c) => c + 1);
                if (count >= 3)
                    log.LogCritical("WorkspaceDeletionService: {Id} failing repeatedly", id);
            }
        }

        return deleted;
    }
}
