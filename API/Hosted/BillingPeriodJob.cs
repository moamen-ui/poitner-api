using Microsoft.Extensions.Configuration;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-20 §3.6f/§5 task 20. The billing period job — structure copies
/// <see cref="WorkspaceDeletionService"/> (30 s initial delay, then every
/// <c>Billing:IntervalMinutes</c>, default 60, clamp 1..1440), a fresh DI scope per pass so a failed
/// pass's tracked entities never leak into the next one.
/// </summary>
public class BillingPeriodJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<BillingPeriodJob> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            await RunAsync(stoppingToken);

            var minutes = IntervalMinutes(configuration);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — exit cleanly.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var period = scope.ServiceProvider.GetRequiredService<IBillingPeriodService>();
            await period.RunOnceAsync(DateTime.UtcNow);
            logger.LogInformation("BillingPeriodJob: pass complete at {Time:u}", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BillingPeriodJob: pass failed");
        }
    }

    /// <summary>Clamp 1..1440 (once a minute to once a day); default 60.</summary>
    internal static int IntervalMinutes(IConfiguration? config)
    {
        var raw = config?["Billing:IntervalMinutes"];
        return int.TryParse(raw, out var parsed) ? Math.Clamp(parsed, 1, 1440) : 60;
    }
}
