using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-17 §3.5: the demo-expiry sweep, every 15 minutes (D17.8), in three isolated steps — a failure
/// in one must not block the others: (1) warn demos expiring within 2h, (2) hard-delete demos whose
/// TTL has passed (re-checked immediately before the delete — Gemini Pro #2), (3) sweep stale
/// `demo_email_*` throttle rows (see <see cref="IDemoService"/>).
/// </summary>
public class DemoCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<DemoCleanupService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Initial sweep ~30 s after startup so the DB is fully migrated/seeded.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await SweepAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
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
        logger.LogInformation("DemoCleanupService: starting sweep at {Time:u}", DateTime.UtcNow);

        // Step 1: the T-2h warning e-mail — its own scope, its own try/catch (D17.6; no audit row,
        // D17.9).
        try
        {
            using var scope = scopeFactory.CreateScope();
            var demoService = scope.ServiceProvider.GetRequiredService<IDemoService>();
            var warned = await demoService.WarnExpiringAsync(
                DateTime.UtcNow,
                TimeSpan.FromHours(2)
            );
            logger.LogInformation("DemoCleanupService: warned {Count} expiring demo(s)", warned);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DemoCleanupService: warn-expiring step failed");
        }

        // Step 2: expire + hard-delete — its own scope, its own try/catch.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
            var demoService = scope.ServiceProvider.GetRequiredService<IDemoService>();
            var deleted = await SweepOnceAsync(
                uow,
                tenantService,
                demoService,
                logger,
                stoppingToken
            );
            logger.LogInformation(
                "DemoCleanupService: sweep complete — {Deleted} demo tenant(s) hard-deleted",
                deleted
            );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DemoCleanupService: expiry sweep failed");
        }

        // Step 3: PII cleanup of the throttle rows — its own scope, its own try/catch.
        try
        {
            using var scope = scopeFactory.CreateScope();
            var demoService = scope.ServiceProvider.GetRequiredService<IDemoService>();
            var swept = await demoService.SweepThrottleRowsAsync(DateTime.UtcNow);
            logger.LogInformation("DemoCleanupService: throttle rows deleted {Count}", swept);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DemoCleanupService: throttle-row sweep failed");
        }
    }

    /// <summary>
    /// One expiry-delete pass (step 2 of <see cref="SweepAsync"/>). Internal + static so tests can
    /// drive it directly (ImpersonationSweepService precedent). <paramref name="demoService"/> is
    /// part of the signature for parity with the hosted job's dependencies even though this
    /// particular step does not call it. Returns the number of workspaces hard-deleted.
    /// </summary>
    internal static async Task<int> SweepOnceAsync(
        IUnitOfWork uow,
        ITenantService tenantService,
        IDemoService demoService,
        ILogger log,
        CancellationToken ct
    )
    {
        _ = demoService;
        var now = DateTime.UtcNow;

        List<Guid> expiredIds;
        try
        {
            // DB-17 §3.3: the WORKSPACE is the demo authority now, never `users.owner_id`.
            expiredIds = await uow
                .Workspaces.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(w => w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt < now)
                .Select(w => w.Id)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "DemoCleanupService: failed to query expired demo workspaces");
            return 0;
        }

        if (expiredIds.Count == 0)
        {
            log.LogInformation("DemoCleanupService: no expired demo tenants found");
            return 0;
        }

        log.LogInformation(
            "DemoCleanupService: found {Count} expired demo tenant(s) to delete",
            expiredIds.Count
        );

        var deleted = 0;
        foreach (var pid in expiredIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // DB-17 §3.5 (Gemini Pro #2): a conversion/extension that commits between the id
                // query above and this delete must not destroy the user's data — re-check
                // immediately before calling HardDeleteAsync (which re-checks again, under FOR
                // UPDATE, inside its own transaction).
                var stillExpired = await uow
                    .Workspaces.IgnoreQueryFilters()
                    .AnyAsync(
                        w =>
                            w.Id == pid
                            && w.DemoExpiresAt != null
                            && w.DemoExpiresAt < DateTime.UtcNow,
                        ct
                    );
                if (!stillExpired)
                {
                    log.LogInformation(
                        "DemoCleanupService: {Id} no longer an expired demo (converted/extended); skipped",
                        pid
                    );
                    continue;
                }

                var result = await tenantService.HardDeleteAsync(pid, reason: "demo_expired");
                if (result.IsSuccess)
                {
                    deleted++;
                    log.LogInformation(
                        "DemoCleanupService: hard-deleted demo tenant {WorkspaceId}",
                        pid
                    );
                }
                else
                {
                    log.LogWarning(
                        "DemoCleanupService: HardDeleteAsync returned failure for {WorkspaceId}: {Message}",
                        pid,
                        result.Message
                    );
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "DemoCleanupService: demo cleanup failed for {Pid}", pid);
            }
        }

        return deleted;
    }
}
