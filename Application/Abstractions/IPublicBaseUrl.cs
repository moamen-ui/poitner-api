namespace Pointer.Application.Abstractions;

/// <summary>
/// Resolves this API's public origin (scheme://host, e.g. <c>https://api.pointer.moamen.work</c>) so
/// that a relative, server-produced path (a signed upload URL, in particular) can be turned into an
/// absolute URL before it is handed to a client. The widget runs on the customer's own origin and the
/// dashboard runs on its own origin, so a relative <c>/api/uploads/file?...</c> path resolves against
/// THEIR origin, not the API's — this abstraction is how Application-layer code (which cannot see
/// HttpContext) gets the right origin to prefix with.
/// </summary>
public interface IPublicBaseUrl
{
    /// <summary>
    /// Returns the API's public origin with no trailing slash (e.g. <c>https://api.example.com</c>),
    /// or null when there is no current HTTP request to resolve it from (a hosted background job such
    /// as the screenshot purge/retention sweeps, or any other non-request context) — callers must
    /// treat null as "leave the URL relative" rather than throwing.
    /// </summary>
    string? Get();
}

/// <summary>
/// Prepends an <see cref="IPublicBaseUrl"/>'s origin to a relative, server-produced URL. When the
/// origin cannot be resolved (no HttpContext) the relative URL is returned unchanged rather than
/// throwing, so hosted-job callers with no request in scope keep working.
/// </summary>
public static class PublicBaseUrlExtensions
{
    public static string Absolutize(this IPublicBaseUrl? publicBaseUrl, string relativeUrl)
    {
        var baseUrl = publicBaseUrl?.Get();
        return string.IsNullOrEmpty(baseUrl) ? relativeUrl : baseUrl + relativeUrl;
    }
}
