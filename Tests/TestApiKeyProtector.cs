using System.Security.Cryptography;
using System.Text;
using Pointer.Application.Abstractions;

namespace Pointer.Tests;

/// <summary>
/// Stand-in for the real AES-GCM protector: fast, needs no key configuration, but still models the
/// properties the services depend on — a real SHA-256 hash, and an encrypted form that does not
/// contain the plaintext (an earlier version returned "enc:" + key, which quietly defeated the
/// "nothing is stored in the clear" test). <see cref="FailDecrypt"/> simulates a rotated key.
/// </summary>
public sealed class TestApiKeyProtector : IApiKeyProtector
{
    public bool FailDecrypt { get; set; }

    public string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    // Reversible and plaintext-free: enough for round-trip assertions without real crypto.
    public string Encrypt(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes("v1:" + key));

    public string? Decrypt(string blob)
    {
        if (FailDecrypt)
            return null;

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
            return decoded.StartsWith("v1:") ? decoded["v1:".Length..] : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
