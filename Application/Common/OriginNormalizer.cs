namespace Pointer.Application.Common;

/// <summary>
/// Normalizes an origin/URL to scheme://host[:port], lower-case, no trailing slash/path, so two
/// URLs that point at the same origin compare equal regardless of casing, trailing slash, or an
/// explicit default port. Shared by ExtensionService's origin lookup and ProjectService's
/// widget-activation-by-origin check.
///
/// Also owns wildcard app-URL patterns (R1-05). A pattern may carry a single <c>*</c> inside its
/// leftmost host label so a team can allow preview deployments (<c>https://myapp-*.vercel.app</c>)
/// without listing every ephemeral URL. The rules in <see cref="ValidatePattern"/> exist because a
/// careless pattern is an open door: <c>*.vercel.app</c> would authorise every other tenant on that
/// platform to post comments into this project.
/// </summary>
public static class OriginNormalizer
{
    /// <summary>
    /// Hosts where a bare <c>*</c> leftmost label would authorise unrelated tenants, because anyone
    /// can obtain a subdomain. A pattern on these is only accepted when its leftmost label has a
    /// literal part (<c>myapp-*</c>), which keeps it scoped to one account's naming.
    /// </summary>
    private static readonly string[] SharedHostingSuffixes =
    {
        "vercel.app",
        "netlify.app",
        "pages.dev",
        "github.io",
        "web.app",
        "firebaseapp.com",
        "herokuapp.com",
        "azurewebsites.net",
        "cloudfront.net",
        "amplifyapp.com",
        "onrender.com",
        "fly.dev",
        "railway.app",
        "surge.sh",
        "ngrok.io",
        "ngrok-free.app",
        "loca.lt",
    };

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;
        var trimmed = raw.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
            return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}";
        }
        return trimmed.ToLowerInvariant().TrimEnd('/');
    }

    /// <summary>True when <paramref name="raw"/> contains a wildcard and so needs pattern matching.</summary>
    public static bool IsPattern(string raw) => !string.IsNullOrWhiteSpace(raw) && raw.Contains('*');

    /// <summary>
    /// Validates a wildcard app-URL pattern. Returns null when acceptable, otherwise a message
    /// explaining the rejection (surfaced as a 400 when saving a project app URL).
    /// </summary>
    public static string? ValidatePattern(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "URL is required.";

        if (!IsPattern(raw))
            return null; // an exact origin — nothing wildcard-specific to check

        if (raw.Count(c => c == '*') > 1)
            return "A URL pattern may contain at most one '*'.";

        // Parse by swapping the wildcard for a placeholder label, so Uri can do the heavy lifting
        // (scheme, port, IDN) instead of us hand-rolling a host parser.
        const string placeholder = "wildcardplaceholder";
        if (!Uri.TryCreate(raw.Trim().Replace("*", placeholder), UriKind.Absolute, out var uri))
            return "URL pattern must be an absolute URL, e.g. https://myapp-*.example.com.";

        if (!string.IsNullOrEmpty(uri.AbsolutePath.TrimEnd('/')) || !string.IsNullOrEmpty(uri.Query))
            return "URL pattern must be an origin only — no path or query.";

        var host = uri.Host.ToLowerInvariant();
        var labels = host.Split('.');

        if (!labels[0].Contains(placeholder))
            return "'*' is only allowed in the left-most part of the host, e.g. https://*.staging.example.com.";

        if (labels.Skip(1).Any(l => l.Contains(placeholder)))
            return "'*' is only allowed in the left-most part of the host.";

        var remaining = string.Join('.', labels.Skip(1));
        var bareWildcard = labels[0] == placeholder;

        // Shared-hosting is checked first: for a two-label host like vercel.app both rules fire, and
        // "this would let other tenants in" tells the operator far more than "too few labels".
        if (bareWildcard && IsSharedHostingSuffix(remaining))
            return $"'*.{remaining}' would allow any site on {remaining}, including other people's. "
                + "Add a literal prefix, e.g. myapp-*." + remaining + ".";

        if (bareWildcard && labels.Length - 1 < 3)
            return $"'*.{remaining}' is too broad. Use at least three labels (e.g. *.staging.example.com) "
                + "or add a literal prefix (e.g. myapp-*).";

        return null;
    }

    /// <summary>
    /// True when a concrete origin matches a pattern. Both sides are normalised first; scheme, port
    /// and label count must agree exactly, every label but the leftmost must match literally, and the
    /// leftmost is glob-matched. Label-count equality is what stops <c>*.staging.acme.com</c> from
    /// matching <c>evil.attacker.staging.acme.com</c>.
    /// </summary>
    public static bool Matches(string pattern, string origin)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(origin))
            return false;

        if (!IsPattern(pattern))
            return Normalize(pattern) == Normalize(origin);

        if (ValidatePattern(pattern) is not null)
            return false; // never match on a pattern we would have refused to save

        const string placeholder = "wildcardplaceholder";
        var normalisedPattern = Normalize(pattern.Replace("*", placeholder));
        var normalisedOrigin = Normalize(origin);

        var (patternScheme, patternHost, patternPort) = Split(normalisedPattern);
        var (originScheme, originHost, originPort) = Split(normalisedOrigin);

        if (patternScheme != originScheme || patternPort != originPort)
            return false;

        var patternLabels = patternHost.Split('.');
        var originLabels = originHost.Split('.');

        if (patternLabels.Length != originLabels.Length)
            return false;

        for (var i = 1; i < patternLabels.Length; i++)
        {
            if (patternLabels[i] != originLabels[i])
                return false;
        }

        return GlobMatches(patternLabels[0].Replace(placeholder, "*"), originLabels[0]);
    }

    private static bool IsSharedHostingSuffix(string host) =>
        SharedHostingSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));

    private static (string Scheme, string Host, string Port) Split(string normalised)
    {
        var schemeEnd = normalised.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            return (string.Empty, normalised, string.Empty);

        var scheme = normalised[..schemeEnd];
        var rest = normalised[(schemeEnd + 3)..];
        var colon = rest.LastIndexOf(':');

        return colon > 0 ? (scheme, rest[..colon], rest[(colon + 1)..]) : (scheme, rest, string.Empty);
    }

    /// <summary>Single-label glob: exactly one <c>*</c>, matching any run of characters including empty.</summary>
    private static bool GlobMatches(string pattern, string value)
    {
        var star = pattern.IndexOf('*');
        if (star < 0)
            return pattern == value;

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];

        return value.Length >= prefix.Length + suffix.Length
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && value.EndsWith(suffix, StringComparison.Ordinal);
    }
}
