using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Auth;

/// <summary>
/// HMAC-SHA256 password-reset tokens (same keying approach as <see cref="Storage.UploadSigner"/>).
/// Token format: "{publicId:N}.{expUnixSeconds}.{base64urlSig}", signed over "{publicId:N}|{exp}"
/// with the JWT signing key. TTL 30 minutes. Constant-time signature comparison.
/// </summary>
public class ResetTokenService : IResetTokenService
{
    private readonly byte[] _key;
    private const int TtlMinutes = 30;
    private static readonly Regex PurposePattern = new("^[a-z-]+$", RegexOptions.Compiled);

    public ResetTokenService(IConfiguration config)
    {
        var key = config["JWT:SigningKey"]
            ?? throw new InvalidOperationException("JWT:SigningKey is not configured.");
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Create(Guid userPublicId, Guid securityStamp)
    {
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlMinutes * 60;
        var id = userPublicId.ToString("N");
        var stamp = securityStamp.ToString("N");
        return $"{id}.{stamp}.{exp}.{Sign(id, stamp, exp)}";
    }

    public bool TryValidate(string token, out Guid userPublicId, out Guid securityStamp)
    {
        userPublicId = Guid.Empty;
        securityStamp = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 4 || !long.TryParse(parts[2], out var exp)) return false;
        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        var expected = Encoding.UTF8.GetBytes(Sign(parts[0], parts[1], exp));
        var actual = Encoding.UTF8.GetBytes(parts[3]);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        return Guid.TryParseExact(parts[0], "N", out userPublicId)
               && Guid.TryParseExact(parts[1], "N", out securityStamp);
    }

    private string Sign(string id, string stamp, long exp)
    {
        using var hmac = new HMACSHA256(_key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}|{stamp}|{exp}"));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public string CreateScoped(Guid userPublicId, Guid securityStamp, string purpose, string? payload = null)
    {
        if (!PurposePattern.IsMatch(purpose))
            throw new ArgumentException("purpose must match ^[a-z-]+$", nameof(purpose));

        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlMinutes * 60;
        var id = userPublicId.ToString("N");
        var stamp = securityStamp.ToString("N");
        var encodedPayload = string.IsNullOrEmpty(payload)
            ? string.Empty
            : Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var sig = SignScoped(id, stamp, exp, purpose, encodedPayload);
        return $"{id}.{stamp}.{exp}.{purpose}.{encodedPayload}.{sig}";
    }

    public bool TryValidateScoped(
        string token,
        string purpose,
        out Guid userPublicId,
        out Guid securityStamp,
        out string? payload
    )
    {
        userPublicId = Guid.Empty;
        securityStamp = Guid.Empty;
        payload = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 6) return false;
        if (!string.Equals(parts[3], purpose, StringComparison.Ordinal)) return false;
        if (!long.TryParse(parts[2], out var exp)) return false;
        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        var expected = Encoding.UTF8.GetBytes(SignScoped(parts[0], parts[1], exp, parts[3], parts[4]));
        var actual = Encoding.UTF8.GetBytes(parts[5]);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        if (!Guid.TryParseExact(parts[0], "N", out userPublicId)) return false;
        if (!Guid.TryParseExact(parts[1], "N", out securityStamp)) return false;

        payload = parts[4].Length == 0 ? null : Encoding.UTF8.GetString(Base64UrlDecode(parts[4]));
        return true;
    }

    private string SignScoped(string id, string stamp, long exp, string purpose, string payload)
    {
        using var hmac = new HMACSHA256(_key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}|{stamp}|{exp}|{purpose}|{payload}"));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };
        return Convert.FromBase64String(padded);
    }
}
