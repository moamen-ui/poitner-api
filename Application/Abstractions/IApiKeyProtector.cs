namespace Pointer.Application.Abstractions;

/// <summary>
/// Protects API keys at rest: a one-way hash for lookup, and reversible encryption purely so the
/// profile page can show a user their own key again. Implemented in Infrastructure/Security.
/// </summary>
public interface IApiKeyProtector
{
    /// <summary>Lowercase hex SHA-256 of the raw key. Deterministic — this is the lookup column.</summary>
    string Hash(string key);

    /// <summary>Base64 AES-256-GCM blob (nonce ‖ ciphertext ‖ tag) of the raw key.</summary>
    string Encrypt(string key);

    /// <summary>The raw key, or null when the blob cannot be opened (rotated key, tampering).</summary>
    string? Decrypt(string blob);
}
