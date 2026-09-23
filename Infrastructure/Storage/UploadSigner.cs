using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Storage;

/// <summary>
/// HMAC-SHA256 signer for upload file URLs.
/// Key: UTF-8 bytes of config["JWT:SigningKey"].
/// Signed message: "{relPath}|{exp}"
/// Signature: base64url (no padding) of the HMAC-SHA256 digest.
/// URL TTL: 3600 seconds (1 hour).
/// </summary>
public class UploadSigner : IUploadSigner
{
    private readonly byte[] _keyBytes;
    private const int TtlSeconds = 3600;

    public UploadSigner(IConfiguration configuration)
    {
        var key = configuration["JWT:SigningKey"]
            ?? throw new InvalidOperationException("JWT:SigningKey is not configured.");
        _keyBytes = Encoding.UTF8.GetBytes(key);
    }

    public string SignedUrl(string relPath) => SignedUrl(relPath, null);

    /// <summary>
    /// DB-13 review fix #2: when <paramref name="notAfter"/> is given (an impersonating operator's
    /// session ExpiresAt), the URL expires at whichever is sooner — the normal 3600s TTL, or the
    /// session's remaining time — so a screenshot URL handed to an impersonating operator can never
    /// outlive that session, even though the signature itself is otherwise identical.
    /// </summary>
    public string SignedUrl(string relPath, DateTime? notAfter)
    {
        var normalExp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlSeconds;
        var exp =
            notAfter is DateTime na
                ? Math.Min(normalExp, new DateTimeOffset(DateTime.SpecifyKind(na, DateTimeKind.Utc)).ToUnixTimeSeconds())
                : normalExp;
        var sig = ComputeSig(relPath, exp);
        var encodedPath = Uri.EscapeDataString(relPath);
        return $"/api/uploads/file?p={encodedPath}&exp={exp}&sig={sig}";
    }

    public bool Validate(string relPath, long exp, string sig)
    {
        // Check expiry first (fast path; this is not secret information).
        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return false;

        var expected = ComputeSig(relPath, exp);

        // Constant-time comparison to prevent timing attacks.
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(sig);

        if (expectedBytes.Length != actualBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public string ExtractRelPath(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        // (a) Signed URL: /api/uploads/file?p=uploads%2F...
        // Try to parse the "p" query parameter.
        var queryStart = stored.IndexOf('?');
        if (queryStart >= 0 && stored.Contains("/api/uploads/file", StringComparison.OrdinalIgnoreCase))
        {
            var query = stored[(queryStart + 1)..];
            foreach (var pair in query.Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var paramName = pair[..eq];
                if (string.Equals(paramName, "p", StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }
        }

        // (b) Absolute or relative public URL / raw path containing "uploads/"
        var idx = stored.IndexOf("uploads/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            // Strip any trailing query string if present.
            var rel = stored[idx..];
            var qs = rel.IndexOf('?');
            if (qs >= 0) rel = rel[..qs];
            return rel;
        }

        // (c) Return as-is (best effort).
        return stored;
    }

    // -----------------------------------------------------------------------

    private string ComputeSig(string relPath, long exp)
    {
        var message = Encoding.UTF8.GetBytes($"{relPath}|{exp}");
        var digest = HMACSHA256.HashData(_keyBytes, message);
        // base64url without padding
        return Convert.ToBase64String(digest)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
