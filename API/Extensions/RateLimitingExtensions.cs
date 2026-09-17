using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Pointer.API.Extensions;

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration? configuration = null) =>
        services.AddRateLimiter(o => Configure(o, configuration));

    // Public (not folded into AddApiRateLimiting) so tests can assert on the configured options.
    public static void Configure(RateLimiterOptions o) => Configure(o, null);

    /// <param name="configuration">
    /// Optional overrides under <c>Security:RateLimits</c>. Only the signup budget is overridable,
    /// and only upward-in-practice: an end-to-end suite legitimately registers and accepts dozens
    /// of invitations from one address in a few minutes, which the production budget of 5/hour is
    /// meant to stop. Weakening the shipped default to make tests pass would remove the protection
    /// for everyone; making it configurable lets the local compose stack raise it and leaves
    /// production alone.
    /// </param>
    public static void Configure(RateLimiterOptions o, IConfiguration? configuration)
    {
        var signupPermitLimit = 5;
        var configured = configuration?["Security:RateLimits:SignupPerHour"];
        if (int.TryParse(configured, out var parsed) && parsed > 0)
            signupPermitLimit = parsed;

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
                    PermitLimit = signupPermitLimit,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0
                }));

        // Device-code sign-in (`pointer login`). `start` mints a code — a handful per network is
        // plenty. `poll` is the CLI asking "approved yet?" every ~3s for up to 10 minutes; sharing the
        // 5-per-hour "signup" budget with it (the original wiring) exhausted the budget 15 seconds
        // into the very first sign-in and every later attempt from that IP was a 429.
        o.AddPolicy("device-start", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0
                }));
        o.AddPolicy("device-poll", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
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

        static string UserOrIp(HttpContext ctx) => PartitionKeyFor(ctx);

        o.AddPolicy("events", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                UserOrIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        // Magic-link redemption. Per IP, and deliberately NOT the signup budget (5/hour): a whole
        // agency behind one NAT address would be locked out after five clients opened their links.
        // 60/minute still makes brute-forcing a 256-bit token pointless.
        o.AddPolicy("login", ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ClientIp(ctx),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));

        o.AddPolicy("comments", CommentsPartition);

        // Build reports: same per-user partition and budget as comments. A deploy reports once, so
        // 30/min is far above any legitimate use — the limit exists because the endpoint scans and
        // updates comments, and an unbounded caller could make that expensive.
        o.AddPolicy("builds", BuildsPartition);
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
    /// <summary>The "builds" policy's partitioning — per authenticated user, same as comments.</summary>
    public static RateLimitPartition<string> BuildsPartition(HttpContext ctx)
    {
        return RateLimitPartition.GetSlidingWindowLimiter(
            PartitionKeyFor(ctx),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }

    public static RateLimitPartition<string> CommentsPartition(HttpContext ctx)
    {
        return RateLimitPartition.GetSlidingWindowLimiter(
            PartitionKeyFor(ctx),
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
    }

    /// <summary>
    /// "user:{id}" for an authenticated caller, else "ip:{address}".
    /// </summary>
    /// <remarks>
    /// The claim lookup MUST mirror ClaimsPrincipalExtensions.GetIdOrNull. Authentication is
    /// configured with <c>MapInboundClaims = false</c> and JwtTokenService mints <c>sub</c>, so a
    /// partition that looks only at ClaimTypes.NameIdentifier finds nothing and silently falls
    /// back to the IP bucket — every user behind one address then shares one budget, which is
    /// precisely the behaviour the per-user partition exists to avoid. It fails open-ish and
    /// invisibly: nothing errors, the limit is just wrong.
    /// </remarks>
    public static string PartitionKeyFor(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? ctx.User.FindFirst(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub)?.Value
                     ?? ctx.User.FindFirst("sub")?.Value;

        return !string.IsNullOrEmpty(userId)
            ? $"user:{userId}"
            : $"ip:{ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }
}
