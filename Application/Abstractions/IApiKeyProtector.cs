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

    /// <summary>
    /// R5-61 review fix #2 — lowercase hex HMAC-SHA256 of <paramref name="value"/>, keyed from the
    /// same key material <see cref="Encrypt"/>/<see cref="Decrypt"/> use. Unlike <see cref="Hash"/>
    /// (bare SHA-256, no secret — appropriate for API keys, which already carry ≥ 128 bits of
    /// randomness), this is used where the input's own entropy is comparatively low and must be
    /// "peppered" so an offline attacker who obtains only the database (not the app's configuration)
    /// cannot brute-force it — recovery codes (R5-61 finding #2). Deterministic (same input ⇒ same
    /// output), so it remains usable as an equality-lookup column, exactly like <see cref="Hash"/>.
    /// </summary>
    string HmacHex(string value);
}
