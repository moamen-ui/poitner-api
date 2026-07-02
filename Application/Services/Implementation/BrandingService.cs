using System.Text.RegularExpressions;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Application.Services.Implementation;

public class BrandingService(ISettingsService settings) : IBrandingService
{
    // Default values from spec
    private const string DefaultProductName  = "Pointer";
    private const string DefaultTagline      = "Point at the UI. Ship it with AI.";
    private const string DefaultPrimaryColor = "#2563eb";
    private const string DefaultUrlApp       = "https://app.pointer.moamen.work";
    private const string DefaultUrlDemo      = "https://demo.pointer.moamen.work";
    private const string DefaultUrlDocs      = "https://github.com/moamen-ui/poitner-api#readme";
    private const string DefaultUrlLanding   = "https://pointer.moamen.work";

    private static readonly Regex HexColorRegex =
        new(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

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
        // Validate
        if (!string.IsNullOrWhiteSpace(dto.PrimaryColor) && !HexColorRegex.IsMatch(dto.PrimaryColor))
            return Result<BrandingResponse>.Failure(
                "primaryColor must be a valid hex color (e.g. #2563eb or #fff).");

        if (dto.Urls != null)
        {
            if (!string.IsNullOrWhiteSpace(dto.Urls.App)     && !IsHttpUrl(dto.Urls.App))
                return Result<BrandingResponse>.Failure("urls.app must be an http(s) URL.");
            if (!string.IsNullOrWhiteSpace(dto.Urls.Demo)    && !IsHttpUrl(dto.Urls.Demo))
                return Result<BrandingResponse>.Failure("urls.demo must be an http(s) URL.");
            if (!string.IsNullOrWhiteSpace(dto.Urls.Docs)    && !IsHttpUrl(dto.Urls.Docs))
                return Result<BrandingResponse>.Failure("urls.docs must be an http(s) URL.");
            if (!string.IsNullOrWhiteSpace(dto.Urls.Landing) && !IsHttpUrl(dto.Urls.Landing))
                return Result<BrandingResponse>.Failure("urls.landing must be an http(s) URL.");
        }

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

    private static bool IsHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
