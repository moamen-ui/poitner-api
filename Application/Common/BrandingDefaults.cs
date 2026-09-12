namespace Pointer.Application.Common;

/// <summary>
/// The branding values a install falls back to before an operator saves any of their own.
/// </summary>
/// <remarks>
/// These were private consts on BrandingService, which meant only that service could see them.
/// Anything else needing "the dashboard's URL" read the raw setting instead and got null on every
/// install that had never customised branding — i.e. all of them by default.
///
/// That produced a real failure: the allowed-origins check trusts the dashboard's own origin so an
/// admin working in the dashboard is never blocked, but it resolved that origin from the bare
/// setting. With no row saved, the dashboard was not trusted, so switching enforcement on locked
/// the operator out of their own admin UI.
///
/// Shared here so every reader resolves the same value.
/// </remarks>
public static class BrandingDefaults
{
    public const string ProductName  = "Pointer";
    public const string Tagline      = "Point at the UI. Ship it with AI.";
    public const string PrimaryColor = "#2563eb";
    public const string UrlApp       = "https://app.pointer.moamen.work";
    public const string UrlDemo      = "https://demo.pointer.moamen.work";
    public const string UrlDocs      = "https://github.com/moamen-ui/poitner-api#readme";
    public const string UrlLanding   = "https://pointer.moamen.work";
    public const string ExtensionZipUrl = "https://pointer.moamen.work/pointer-extension.zip";
}
