using System.Collections;
using System.Security.Claims;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// DB-12: the resolved request id on <c>HttpContext.Items</c>, so downstream components in
    /// this request (the audit writer, which stamps it on every audit row) can read it without
    /// re-parsing headers. Infrastructure's AuditWriter repeats the key string ("RequestId") — the
    /// two must stay equal (Infrastructure cannot reference the API assembly).
    /// </summary>
    public const string ItemKey = "RequestId";

    // GLM review F6 — only accept a client-supplied id that is a short, plain token; anything
    // else (empty, oversized, or carrying characters a JSON-log consumer wouldn't expect) is
    // replaced with a freshly generated one rather than echoed/logged verbatim.
    private static readonly Regex ValidRequestId = new(
        "^[A-Za-z0-9._-]{8,64}$",
        RegexOptions.Compiled
    );

    public async Task InvokeAsync(HttpContext context, ILogger<RequestIdMiddleware> logger)
    {
        var requestId =
            context.Request.Headers.TryGetValue(HeaderName, out var incoming)
            && ValidRequestId.IsMatch(incoming.ToString())
                ? incoming.ToString()
                : Guid.NewGuid().ToString("N");

        context.Response.Headers[HeaderName] = requestId;
        context.Items[ItemKey] = requestId;

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
