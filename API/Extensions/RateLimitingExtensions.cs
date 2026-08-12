using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Pointer.API.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(Configure);

    // Public (not folded into AddApiRateLimiting) so tests can assert on the configured options.
    public static void Configure(RateLimiterOptions o)
    {
        // The framework default is 503, which reads as an outage to clients (and to anyone
        // debugging with curl). Throttled callers must see 429 + Retry-After instead.
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = (ctx, _) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                ctx.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return ValueTask.CompletedTask;
        };

        // Per-IP fixed-window limiters (partition by client IP, honoring X-Forwarded-For
        // via ForwardedHeaders) so one abuser can't exhaust the limit for everyone.
        static string ClientIp(HttpContext ctx) =>
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        o.AddPolicy("signup", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                }));

        o.AddPolicy("demo", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                }));

        // Light limit for the anonymous public plans endpoint (landing hits it on every page load).
        o.AddPolicy("plans", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    }
}
