using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.API.Hosted;

public class DemoCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<DemoCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Initial sweep ~30 s after startup so the DB is fully migrated/seeded.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await SweepAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
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

        List<Guid> expiredIds;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            expiredIds = await uow.Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(u =>
                    u.IsDemo &&
                    u.DeletedAt == null &&
                    u.ExpiresAt != null &&
                    u.ExpiresAt < DateTime.UtcNow)
                .Select(u => u.PublicId)
                .ToListAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DemoCleanupService: failed to query expired demo tenants");
            return;
        }

        if (expiredIds.Count == 0)
        {
            logger.LogInformation("DemoCleanupService: no expired demo tenants found");
            return;
        }

        logger.LogInformation("DemoCleanupService: found {Count} expired demo tenant(s) to delete", expiredIds.Count);

        int deleted = 0;
        foreach (var pid in expiredIds)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                // Each HardDeleteAsync call gets its own scope so that a single
                // failure doesn't poison the shared DbContext for other tenants.
                using var scope = scopeFactory.CreateScope();
                var tenantService = scope.ServiceProvider.GetRequiredService<ITenantService>();
                var result = await tenantService.HardDeleteAsync(pid);
                if (result.IsSuccess)
                {
                    deleted++;
                    logger.LogInformation("DemoCleanupService: hard-deleted demo tenant {PublicId}", pid);
                }
                else
                {
                    logger.LogWarning("DemoCleanupService: HardDeleteAsync returned failure for {PublicId}: {Message}", pid, result.Message);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "DemoCleanupService: demo cleanup failed for {Pid}", pid);
            }
        }

        logger.LogInformation("DemoCleanupService: sweep complete — {Deleted}/{Total} demo tenant(s) hard-deleted", deleted, expiredIds.Count);
    }
}
