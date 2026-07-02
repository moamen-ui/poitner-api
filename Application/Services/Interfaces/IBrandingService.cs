using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IBrandingService
{
    /// <summary>Valid asset kinds for branding uploads.</summary>
    public static readonly HashSet<string> ValidKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "logo", "iconSquare", "favicon", "appleTouch", "pwa192", "pwa512"
    };

    /// <summary>
    /// Returns the current branding configuration. Asset URLs are built from
    /// <paramref name="publicBase"/> (scheme+host of the current request) and are
    /// present only when <paramref name="existingKinds"/> contains that kind.
    /// </summary>
    Task<Result<BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds);

    /// <summary>
    /// Updates text/url branding settings. Color must be a hex string; URLs must be http(s).
    /// Returns the updated branding response built with the supplied <paramref name="existingKinds"/>.
    /// </summary>
    Task<Result<BrandingResponse>> UpdateAsync(BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds);

    /// <summary>Bumps the assets version counter and returns the new version number.</summary>
    Task<int> BumpVersionAsync();

    /// <summary>Reads the current branding text/url settings without asset resolution.</summary>
    Task<BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds);
}
