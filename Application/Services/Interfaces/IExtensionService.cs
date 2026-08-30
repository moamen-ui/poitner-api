using Pointer.Application.DTOs.Extension;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IExtensionService
{
    /// <summary>
    /// Resolves the project by key, checks ExtensionEnabled, records/looks-up the origin, and enforces
    /// MaxExtensionSites (grandfather-safe). Enforced-but-inert until the real extension calls it.
    /// </summary>
    Task<Result<ExtensionActivateResponse>> ActivateAsync(ExtensionActivateRequest request);

    /// <summary>
    /// Finds the caller's own tenant's project whose AppUrl matches the given origin. Unlike
    /// ProjectService.ListAsync, this is NOT blocked for quick-access (Client) accounts — it is a
    /// single scoped lookup ("what's my project on this site"), not a browse/manage operation, so it's
    /// exactly what a client needs to activate the extension on their invited site without ever seeing
    /// the tenant's full project list.
    /// </summary>
    Task<Result<ExtensionProjectLookupResponse>> FindProjectForOriginAsync(string origin);
}
