using System.Security.Cryptography;
using System.Text;
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

    public ResetTokenService(IConfiguration config)
    {
        var key = config["JWT:SigningKey"]
            ?? throw new InvalidOperationException("JWT:SigningKey is not configured.");
        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Create(Guid userPublicId)
    {
        var exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + TtlMinutes * 60;
        var id = userPublicId.ToString("N");
        return $"{id}.{exp}.{Sign(id, exp)}";
    }

    public bool TryValidate(string token, out Guid userPublicId)
    {
        userPublicId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[1], out var exp)) return false;
        if (exp <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

        var expected = Encoding.UTF8.GetBytes(Sign(parts[0], exp));
        var actual = Encoding.UTF8.GetBytes(parts[2]);
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        return Guid.TryParseExact(parts[0], "N", out userPublicId);
    }

    private string Sign(string id, long exp)
    {
        using var hmac = new HMACSHA256(_key);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{id}|{exp}"));
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
