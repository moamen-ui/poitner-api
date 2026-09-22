using Microsoft.EntityFrameworkCore;
using Pointer.Infrastructure;

namespace Pointer.API.Extensions;

/// <summary>
/// R5-58 observability baseline: structured JSON console logging (§3.1), the DB-backed
/// <c>/health</c> check (§3.3), and optional Sentry error tracking (§3.4, no-op when
/// <c>SENTRY_DSN</c> is empty/unset).
/// </summary>
public static class ObservabilityExtensions
{
    public static IServiceCollection AddObservability(
        this IServiceCollection services,
        IConfiguration config,
        ILoggingBuilder logging
    )
    {
        // 3.1 — structured JSON console logging (replaces the default plain-text formatter).
        // Docker captures stdout and `docker compose logs` presents it; every line is one JSON
        // object, so a request id / user id / tenant id pushed as a logging scope (see
        // RequestIdMiddleware) appears on every line without a Serilog dependency.
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            o.UseUtcTimestamp = true;
        });

        // 3.3 — /health pings the database. Anonymous + rate-limited wiring lives in Program.cs
        // (MapHealthChecks + AllowAnonymous + RequireRateLimiting("meta")).
        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("db", tags: new[] { "ready" });

        return services;
    }

    /// <summary>
    /// 3.4 — wires Sentry only when SENTRY_DSN is a non-empty value. Local dev, tests, and any
    /// environment that leaves the DSN unset never initialize the Sentry SDK and never send
    /// events, so this is a true no-op (not just "no events sent") when the DSN is absent.
    /// </summary>
    public static WebApplicationBuilder AddSentryIfConfigured(this WebApplicationBuilder builder)
    {
        var sentryDsn = builder.Configuration["SENTRY_DSN"];
        if (!string.IsNullOrWhiteSpace(sentryDsn))
        {
            builder.WebHost.UseSentry(o =>
            {
                o.Dsn = sentryDsn;
                o.TracesSampleRate = 0.1;
                o.SendDefaultPii = false;
                o.Environment = builder.Environment.EnvironmentName;
            });
        }

        return builder;
    }
}
