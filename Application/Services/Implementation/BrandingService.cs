using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

using Pointer.Application.Common;

namespace Pointer.Application.Services.Implementation;

public class BrandingService(ISettingsService settings) : IBrandingService
{
    // Default values from spec
    private const string DefaultProductName = BrandingDefaults.ProductName;
    private const string DefaultTagline = BrandingDefaults.Tagline;
    private const string DefaultPrimaryColor = BrandingDefaults.PrimaryColor;
    private const string DefaultUrlApp = BrandingDefaults.UrlApp;
    private const string DefaultUrlDemo = BrandingDefaults.UrlDemo;
    private const string DefaultUrlDocs = BrandingDefaults.UrlDocs;
    private const string DefaultUrlLanding = BrandingDefaults.UrlLanding;
    private const string DefaultExtensionZipUrl = BrandingDefaults.ExtensionZipUrl;

    public async Task<Result<BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds)
    {
        var response = await BuildResponseAsync(publicBase, existingKinds);
        return Result<BrandingResponse>.Success(response);
    }

    public async Task<Result<BrandingResponse>> UpdateAsync(
        BrandingWriteDto dto,
        string publicBase,
        IReadOnlySet<string> existingKinds)
    {
        // PrimaryColor/Urls format is enforced upfront by BrandingWriteDtoValidator
        // (FluentValidation auto-validation) — not re-checked here.

        // Persist only non-null fields (patch semantics)
        if (dto.ProductName != null)
            await settings.SetStringAsync(ISettingsService.BrandProductName, dto.ProductName.Trim());

        if (dto.Tagline != null)
            await settings.SetStringAsync(ISettingsService.BrandTagline, dto.Tagline.Trim());

        if (!string.IsNullOrWhiteSpace(dto.PrimaryColor))
            await settings.SetStringAsync(ISettingsService.BrandPrimaryColor, dto.PrimaryColor.Trim());

        if (dto.Urls != null)
        {
            if (dto.Urls.App     != null)
                await settings.SetStringAsync(ISettingsService.BrandUrlApp,     dto.Urls.App.Trim());
            if (dto.Urls.Demo    != null)
                await settings.SetStringAsync(ISettingsService.BrandUrlDemo,    dto.Urls.Demo.Trim());
            if (dto.Urls.Docs    != null)
                await settings.SetStringAsync(ISettingsService.BrandUrlDocs,    dto.Urls.Docs.Trim());
            if (dto.Urls.Landing != null)
                await settings.SetStringAsync(ISettingsService.BrandUrlLanding, dto.Urls.Landing.Trim());
        }

        var response = await BuildResponseAsync(publicBase, existingKinds);
        return Result<BrandingResponse>.Success(response);
    }

    public async Task<int> BumpVersionAsync()
    {
        var current = await settings.GetIntAsync(ISettingsService.BrandAssetsVersion, 0);
        var next = current + 1;
        await settings.SetIntAsync(ISettingsService.BrandAssetsVersion, next);
        return next;
    }

    public async Task<BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds)
    {
        var productName  = await settings.GetStringAsync(ISettingsService.BrandProductName,  DefaultProductName);
        var tagline      = await settings.GetStringAsync(ISettingsService.BrandTagline,      DefaultTagline);
        var primaryColor = await settings.GetStringAsync(ISettingsService.BrandPrimaryColor, DefaultPrimaryColor);
        var urlApp       = await settings.GetStringAsync(ISettingsService.BrandUrlApp,       DefaultUrlApp);
        var urlDemo      = await settings.GetStringAsync(ISettingsService.BrandUrlDemo,      DefaultUrlDemo);
        var urlDocs      = await settings.GetStringAsync(ISettingsService.BrandUrlDocs,      DefaultUrlDocs);
        var urlLanding   = await settings.GetStringAsync(ISettingsService.BrandUrlLanding,   DefaultUrlLanding);
        var extStoreUrl  = await settings.GetStringAsync(ISettingsService.ExtensionStoreUrl, string.Empty);
        var extZipUrl    = await settings.GetStringAsync(ISettingsService.ExtensionZipUrl,   DefaultExtensionZipUrl);
        var version      = await settings.GetIntAsync(ISettingsService.BrandAssetsVersion, 0);

        var base_ = publicBase.TrimEnd('/');

        return new BrandingResponse
        {
            ProductName  = productName,
            Tagline      = tagline,
            PrimaryColor = primaryColor,
            Urls = new BrandingUrlsResponse
            {
                App     = urlApp,
                Demo    = urlDemo,
                Docs    = urlDocs,
                Landing = urlLanding,
            },
            Assets = new BrandingAssetsResponse
            {
                Logo       = BuildAssetUrl(base_, "logo",       existingKinds, version),
                IconSquare = BuildAssetUrl(base_, "iconSquare", existingKinds, version),
                Favicon    = BuildAssetUrl(base_, "favicon",    existingKinds, version),
                AppleTouch = BuildAssetUrl(base_, "appleTouch", existingKinds, version),
                Pwa192     = BuildAssetUrl(base_, "pwa192",     existingKinds, version),
                Pwa512     = BuildAssetUrl(base_, "pwa512",     existingKinds, version),
            },
            Extension = new BrandingExtensionResponse
            {
                StoreUrl = extStoreUrl,
                ZipUrl   = extZipUrl,
            },
            Version = version,
        };
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string? BuildAssetUrl(string publicBase, string kind, IReadOnlySet<string> existingKinds, int version)
    {
        if (!existingKinds.Contains(kind))
            return null;
        return $"{publicBase}/api/branding/asset/{kind}?v={version}";
    }
}
