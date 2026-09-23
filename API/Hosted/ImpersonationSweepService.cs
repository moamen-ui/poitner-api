using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;

namespace Pointer.API.Hosted;

/// <summary>
/// DB-13 §3.6 — closes every impersonation session whose ExpiresAt has passed but was never ended
/// manually. Modelled on <see cref="DemoCleanupService"/> (initial delay, then a periodic sweep; all
/// exceptions logged, never thrown). So every session has exactly one `started` and one `ended` row
/// within 5 minutes of its end.
/// </summary>
public class ImpersonationSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<ImpersonationSweepService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            await SweepAsync(stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
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
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            var closed = await SweepOnceAsync(db, audit, logger, stoppingToken);
            if (closed > 0)
                logger.LogInformation(
                    "ImpersonationSweepService: closed {Count} expired session(s)",
                    closed
                );
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ImpersonationSweepService: sweep failed");
        }
    }

    /// <summary>
    /// One sweep pass. Internal + static so tests can drive it directly against a Sqlite-backed
    /// AppDbContext, mirroring RetentionService.SweepOnceAsync. Returns the number of sessions closed.
    /// </summary>
    internal static async Task<int> SweepOnceAsync(
        AppDbContext db,
        IAuditWriter audit,
        ILogger log,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        var expired = await db
            .ImpersonationSessions.IgnoreQueryFilters()
            .Where(s => s.EndedAt == null && s.ExpiresAt <= now)
            .ToListAsync(ct);

        var closed = 0;
        foreach (var session in expired)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                session.EndedAt = session.ExpiresAt;
                session.EndReason = ImpersonationEndReason.Expired;
                await db.SaveChangesAsync(ct);

                var durationSeconds = (int)(session.EndedAt.Value - session.StartedAt).TotalSeconds;
                await audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.ImpersonationEnded,
                        AuditTargets.ImpersonationSession,
                        session.Id.ToString(),
                        session.OwnerId,
                        After: new Dictionary<string, string>
                        {
                            ["session_id"] = session.Id.ToString(),
                            ["request_count"] = session.RequestCount.ToString(),
                            ["duration_seconds"] = durationSeconds.ToString(),
                            ["reason"] = "expired",
                        },
                        ActorKindOverride: AuditActorKind.System,
                        // DB-13 review fix #3: the sweep runs with no ICurrentUser at all (a
                        // background service, not a request) — without the override this row's
                        // impersonation_session_id would be NULL.
                        ImpersonationSessionIdOverride: session.Id
                    ),
                    ct
                );
                closed++;
            }
            catch (Exception ex)
            {
                log.LogError(
                    ex,
                    "ImpersonationSweepService: failed to close session {SessionId}",
                    session.Id
                );
            }
        }

        return closed;
    }
}
