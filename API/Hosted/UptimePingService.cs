namespace Pointer.API.Hosted;

/// <summary>
/// R5-58 §3.5 — dead-man's-switch uptime ping. Every 60s, GETs <c>/health</c> on the local process
/// (not through Caddy — this runs inside the api container) and, only if it reports healthy, GETs
/// <c>UPTIME_PING_URL</c> (a healthchecks.io check, same pattern as the existing
/// <c>OFFSITE_HEALTHCHECK_URL</c> dead-man's switch in scripts/offsite-backup.sh). The monitoring
/// service alerts when pings stop, not on any response this service sends itself.
/// </summary>
public class UptimePingService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<UptimePingService> logger
) : BackgroundService
{
    private static readonly Uri LocalHealthUri = new("http://localhost:8080/health");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pingUrl = config["UPTIME_PING_URL"];
        if (string.IsNullOrWhiteSpace(pingUrl))
        {
            // Disabled — no healthchecks.io check configured for this environment.
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            do
            {
                await TickAsync(pingUrl, stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — exit cleanly.
        }
    }

    private async Task TickAsync(string pingUrl, CancellationToken stoppingToken)
    {
        try
        {
            using var client = httpClientFactory.CreateClient(nameof(UptimePingService));
            client.Timeout = TimeSpan.FromSeconds(10);

            using var healthResponse = await client.GetAsync(LocalHealthUri, stoppingToken);
            if (!healthResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "UptimePingService: /health returned {StatusCode}; skipping ping",
                    (int)healthResponse.StatusCode
                );
                return;
            }

            using var pingResponse = await client.GetAsync(pingUrl, stoppingToken);
            if (!pingResponse.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "UptimePingService: ping to UPTIME_PING_URL returned {StatusCode}",
                    (int)pingResponse.StatusCode
                );
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a transient network failure crash the hosted service — the next tick
            // retries in 60s, and a missed ping is exactly what the dead-man's switch is for.
            logger.LogWarning(ex, "UptimePingService: tick failed");
        }
    }
}
