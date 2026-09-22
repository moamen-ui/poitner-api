using System.Collections;
using System.Security.Claims;

namespace Pointer.API.Middleware;

/// <summary>
/// R5-58 §3.2 — assigns/echoes <c>X-Request-Id</c> and pushes a
/// <c>{ RequestId, UserId, TenantId }</c> logging scope around the rest of the pipeline, so every
/// JSON log line written while handling this request carries all three.
/// </summary>
/// <remarks>
/// Registered right after <c>app.UseForwardedHeaders(fwd)</c>, before the exception handler — so
/// even an unhandled-exception log line gets the request id. That is also before
/// <c>UseAuthentication</c>/<c>UseAuthorization</c> run, which means <c>HttpContext.User</c> has no
/// claims yet at the point this middleware executes. Reading them eagerly here would log
/// <c>null</c> for every authenticated request. <see cref="RequestLogScope"/> instead defers the
/// claim lookup to log-write time (the formatter enumerates the scope object when it actually
/// writes a line, which happens deep inside <c>next()</c>, after authentication has populated
/// <c>HttpContext.User</c>) — so UserId/TenantId are correct without moving this middleware later
/// and losing request-id coverage of everything upstream of auth.
/// </remarks>
public class RequestIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context, ILogger<RequestIdMiddleware> logger)
    {
        var requestId =
            context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && !string.IsNullOrWhiteSpace(incoming)
                ? incoming.ToString()
                : Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = requestId;

        using (logger.BeginScope(new RequestLogScope(context, requestId)))
        {
            await next(context);
        }
    }

    /// <summary>
    /// Claim lookups mirror <c>RateLimitingExtensions.PartitionKeyFor</c> /
    /// <c>HttpCurrentUser</c>: <c>sub</c> (auth is configured with <c>MapInboundClaims = false</c>
    /// and <c>JwtTokenService</c> mints <c>sub</c>, so <c>ClaimTypes.NameIdentifier</c> is checked
    /// first for parity with the rate limiter, then the raw claim types) for the user id, and the
    /// separate <c>"tenant"</c> claim (<c>User.OwnerId</c>, minted by <c>JwtTokenService</c>) for the
    /// tenant id. Both are <c>null</c> for anonymous requests.
    /// </summary>
    private sealed class RequestLogScope(HttpContext context, string requestId)
        : IEnumerable<KeyValuePair<string, object?>>
    {
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            yield return new KeyValuePair<string, object?>("RequestId", requestId);

            var userId =
                context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context
                    .User.FindFirst(
                        Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub
                    )
                    ?.Value
                ?? context.User.FindFirst("sub")?.Value;
            yield return new KeyValuePair<string, object?>("UserId", userId);

            yield return new KeyValuePair<string, object?>(
                "TenantId",
                context.User.FindFirst("tenant")?.Value
            );
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
