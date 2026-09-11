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

        o.AddPolicy("meta", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        static string UserOrIp(HttpContext ctx)
        {
            var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return !string.IsNullOrEmpty(userId) ? $"user:{userId}" : $"ip:{ClientIp(ctx)}";
        }

        o.AddPolicy("events", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                UserOrIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        o.AddPolicy("comments", CommentsPartition);
    }

    /// <summary>The "comments" policy's partitioning, public so tests can assert on it directly
    /// (RateLimiterOptions.PolicyMap is internal to ASP.NET Core).</summary>
    /// <remarks>
    /// Partitioned per authenticated user rather than per IP: a whole office behind one NAT
    /// address is a normal deployment, and an IP partition would let one enthusiastic tester
    /// throttle their colleagues.
    ///
    /// Sliding rather than fixed window. A fixed window lets a caller spend the full budget in the
    /// last second of one window and again in the first second of the next — a 60-burst across a
    /// window boundary, which is exactly the abuse shape this limits.
    /// </remarks>
    public static RateLimitPartition<string> CommentsPartition(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var key = !string.IsNullOrEmpty(userId)
            ? $"user:{userId}"
            : $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

        return RateLimitPartition.GetSlidingWindowLimiter(
            key,
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }
}
