using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Security;

/// <summary>
/// Hashes API keys for lookup and encrypts them for display.
///
/// Two separate jobs on purpose: <see cref="Hash"/> is what the login path matches, so a database
/// dump alone yields nothing usable; <see cref="Encrypt"/>/<see cref="Decrypt"/> exist only because
/// the product promises the key stays re-viewable on the profile page, which a hash cannot do.
/// Opening the blob needs the encryption key, which lives in configuration, not the database.
/// </summary>
public class ApiKeyProtector : IApiKeyProtector
{
    // AES-GCM standard sizes. The nonce is generated per encryption and stored alongside the
    // ciphertext — reusing a nonce with the same key would be catastrophic, so it is never derived.
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    private const string ConfiguredKeyPath = "Auth:ApiKeyEncryptionKey";
    private const string JwtSigningKeyPath = "JWT:SigningKey";
    private static readonly byte[] DerivationInfo = Encoding.UTF8.GetBytes("pointer-apikey-v1");

    private readonly byte[] _key;

    public ApiKeyProtector(IConfiguration config, ILogger<ApiKeyProtector> log)
    {
        var configured = config[ConfiguredKeyPath];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            _key = DecodeConfiguredKey(configured);
            return;
        }

        // Zero-config boots (local dev, a self-host's first start) must work without the operator
        // having generated a key yet. Derive one from the JWT signing secret so the value is stable
        // across restarts — but say so loudly, because it couples two secrets: an environment leak
        // then yields both. Production sets a dedicated key.
        var signingKey = config[JwtSigningKeyPath];
        if (string.IsNullOrWhiteSpace(signingKey))
            throw new InvalidOperationException(
                $"Neither {ConfiguredKeyPath} nor {JwtSigningKeyPath} is configured — cannot protect API keys.");

        log.LogWarning(
            "{Path} is not set; deriving the API-key encryption key from {Fallback}. Set a dedicated "
                + "32-byte base64 key in production (openssl rand -base64 32). Rotating either value makes "
                + "existing keys undisplayable — logins keep working, since they match on the hash.",
            ConfiguredKeyPath,
            JwtSigningKeyPath);

        _key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(signingKey),
            KeyBytes,
            salt: null,
            info: DerivationInfo);
    }

    private static byte[] DecodeConfiguredKey(string configured)
    {
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"{ConfiguredKeyPath} must be base64 (openssl rand -base64 32).");
        }

        if (decoded.Length != KeyBytes)
            throw new InvalidOperationException(
                $"{ConfiguredKeyPath} must decode to exactly {KeyBytes} bytes, got {decoded.Length}.");

        return decoded;
    }

    public string Hash(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public string Encrypt(string key)
    {
        var plaintext = Encoding.UTF8.GetBytes(key);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(_key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // nonce ‖ ciphertext ‖ tag — self-contained, so Decrypt needs nothing but the key.
        var blob = new byte[NonceBytes + ciphertext.Length + TagBytes];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, NonceBytes);
        tag.CopyTo(blob, NonceBytes + ciphertext.Length);

        return Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Returns the raw key, or null when the blob cannot be opened — a rotated/wrong encryption key
    /// or tampering. Null is a reportable condition, never a reason to mint a replacement key.
    /// </summary>
    public string? Decrypt(string blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
            return null;

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(blob);
        }
        catch (FormatException)
        {
            return null;
        }

        if (decoded.Length <= NonceBytes + TagBytes)
            return null;

        var nonce = decoded.AsSpan(0, NonceBytes);
        var cipherLength = decoded.Length - NonceBytes - TagBytes;
        var ciphertext = decoded.AsSpan(NonceBytes, cipherLength);
        var tag = decoded.AsSpan(NonceBytes + cipherLength, TagBytes);
        var plaintext = new byte[cipherLength];

        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // Authentication failure: wrong key, or the blob was altered. Both are "cannot display".
            return null;
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
