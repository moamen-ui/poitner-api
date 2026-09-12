namespace Pointer.API.Extensions;

public static class HttpRequestExtensions
{
    /// <summary>
    /// The request's origin for the allow-list check: the <c>Origin</c> header, else the origin
    /// part of <c>Referer</c>. Null when neither is present — which is normal for the CLI and AI
    /// agents, and is handled by <c>IsOriginAllowedAsync</c> rather than treated as a rejection
    /// here.
    /// </summary>
    /// <remarks>
    /// Shared rather than duplicated per controller on purpose. It previously lived as a private
    /// method on CommentsController, and the reply endpoint — the other way to write into a
    /// project — simply never called the service's origin parameter, so replies bypassed the
    /// allow-list entirely. One implementation, reachable from every write path, is what stops
    /// that recurring.
    /// </remarks>
    public static string? RequestOrigin(this HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
            return origin;

        var referer = request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : null;
    }
}
