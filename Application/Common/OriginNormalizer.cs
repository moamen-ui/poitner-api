namespace Pointer.Application.Common;

/// <summary>
/// Normalizes an origin/URL to scheme://host[:port], lower-case, no trailing slash/path, so two
/// URLs that point at the same origin compare equal regardless of casing, trailing slash, or an
/// explicit default port. Shared by ExtensionService's origin lookup and ProjectService's
/// widget-activation-by-origin check.
/// </summary>
public static class OriginNormalizer
{
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
        }
        return trimmed.ToLowerInvariant().TrimEnd('/');
    }
}
