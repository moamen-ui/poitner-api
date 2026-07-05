using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Pointer.API.Extensions;

/// <summary>
/// Resolves this server's public origin. Prefers an explicitly configured absolute base URL
/// (<c>Pointer:PublicUrl</c>) so a deployment behind a proxy / pre-vanity-domain can advertise the
/// canonical public origin regardless of the incoming request's scheme/host; otherwise falls back
/// to the request origin (scheme://host). Mirrors the inline pattern in DemoController.
/// </summary>
public static class PointerUrlResolver
{
    public static string ResolvePublicUrl(IConfiguration configuration, HttpRequest request)
    {
        var configured = configuration["Pointer:PublicUrl"];
        return !string.IsNullOrWhiteSpace(configured)
            ? configured.TrimEnd('/')
            : $"{request.Scheme}://{request.Host}";
    }
}
