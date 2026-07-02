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
}
