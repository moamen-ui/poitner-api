using System.Security.Cryptography;

namespace Pointer.Application.Common;

/// <summary>
/// Mints and hashes quick-access magic-link tokens.
/// </summary>
/// <remarks>
/// 32 bytes from a CSPRNG, base64url-encoded to 43 characters — the link is a bearer credential
/// and must not be guessable. Only the SHA-256 is ever persisted, so a database read cannot
/// reconstruct a working link; lookup is by hash, which is why no constant-time compare is needed.
/// </remarks>
public static class QuickAccessTokenGenerator
{
    public static string NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string Hash(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Appends the token to an app URL, preserving any existing query and fragment.</summary>
    public static string BuildMagicLink(string appUrl, string rawToken)
    {
        var uri = new UriBuilder(appUrl);
        var query = uri.Query.TrimStart('?');
        var param = $"pointer_invite={Uri.EscapeDataString(rawToken)}";
        uri.Query = string.IsNullOrEmpty(query) ? param : $"{query}&{param}";
        return uri.Uri.ToString();
    }
}
