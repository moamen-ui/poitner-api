using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;

namespace Pointer.API.Extensions;

/// <summary>
/// API-layer implementation of <see cref="IPublicBaseUrl"/>. Application/Infrastructure code cannot
/// touch <see cref="HttpContext"/> directly (Clean Architecture), so this lives here and is wired
/// into the DI container as the concrete implementation of the Application-facing abstraction.
/// Delegates to <see cref="PointerUrlResolver"/> — the same rule used by DemoController,
/// MetaController and BrandingController — so behavior (honouring Pointer:PublicUrl, then
/// X-Forwarded-Proto/For behind Caddy) is identical everywhere.
/// </summary>
public class HttpContextPublicBaseUrl(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration
) : IPublicBaseUrl
{
    public string? Get()
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return request is null ? null : PointerUrlResolver.ResolvePublicUrl(configuration, request);
    }
}
