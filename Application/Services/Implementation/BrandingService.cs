using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

using Pointer.Application.Common;

namespace Pointer.Application.Services.Implementation;

public class BrandingService(ISettingsService settings, IAuditWriter? audit = null) : IBrandingService
{
    private readonly IAuditWriter _audit = audit ?? NoopAuditWriter.Instance;

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
        var changedKeys = new List<string>();

        if (dto.ProductName != null)
        {
            await settings.SetStringAsync(ISettingsService.BrandProductName, dto.ProductName.Trim());
            changedKeys.Add("product_name");
        }

        if (dto.Tagline != null)
        {
            await settings.SetStringAsync(ISettingsService.BrandTagline, dto.Tagline.Trim());
            changedKeys.Add("tagline");
        }

        if (!string.IsNullOrWhiteSpace(dto.PrimaryColor))
        {
            await settings.SetStringAsync(ISettingsService.BrandPrimaryColor, dto.PrimaryColor.Trim());
            changedKeys.Add("primary_color");
        }

        if (dto.Urls != null)
        {
            if (dto.Urls.App     != null)
            {
                await settings.SetStringAsync(ISettingsService.BrandUrlApp,     dto.Urls.App.Trim());
                changedKeys.Add("url_app");
            }
            if (dto.Urls.Demo    != null)
            {
                await settings.SetStringAsync(ISettingsService.BrandUrlDemo,    dto.Urls.Demo.Trim());
                changedKeys.Add("url_demo");
            }
            if (dto.Urls.Docs    != null)
            {
                await settings.SetStringAsync(ISettingsService.BrandUrlDocs,    dto.Urls.Docs.Trim());
                changedKeys.Add("url_docs");
            }
            if (dto.Urls.Landing != null)
            {
                await settings.SetStringAsync(ISettingsService.BrandUrlLanding, dto.Urls.Landing.Trim());
                changedKeys.Add("url_landing");
            }
        }

        // Always written (even an empty patch) — [Audited] on the controller action requires a row
        // for every successful call, not just ones that actually changed a key.
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.BrandingUpdated,
                AuditTargets.Branding,
                "global",
                null,
                After: new Dictionary<string, string> { ["keys"] = string.Join(',', changedKeys) }
            )
        );

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
