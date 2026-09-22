using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Owns the lifecycle of personal API keys: minting, revealing, regenerating, and resolving a raw
/// key back to its user at login. Keys are stored hashed (for lookup) and encrypted (for display) —
/// see <see cref="Pointer.Application.Abstractions.IApiKeyProtector"/>.
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// The user's active key, minting one only if none exists. A row that exists but cannot be
    /// decrypted counts as existing: returning null there would silently regenerate every key after
    /// an encryption-key rotation.
    /// </summary>
    Task<ApiKeyResult> GetOrCreateAsync(Guid publicId, Guid? workspaceId);

    /// <summary>Revokes the active key and mints a replacement. The only path that rotates a key.</summary>
    Task<ApiKeyResult> RegenerateAsync(Guid publicId, Guid? workspaceId);

    /// <summary>Resolves a raw key to its user for login, or null if unknown/revoked.</summary>
    Task<ApiKey?> ResolveAsync(string rawKey);

    /// <summary>Records usage, at most once a minute per key, so a busy agent does not write per request.</summary>
    Task TouchLastUsedAsync(int apiKeyId);
}

/// <summary>
/// Outcome of a key read/mint. <see cref="RawKey"/> is null exactly when the stored blob could not
/// be decrypted — the caller reports "key display unavailable" and changes nothing.
/// </summary>
public sealed record ApiKeyResult(bool Found, string? RawKey, string Prefix, DateTime? LastUsedAt)
{
    public static ApiKeyResult NotFound() => new(false, null, string.Empty, null);

    public static ApiKeyResult Undecryptable(ApiKey key) => new(true, null, key.Prefix, key.LastUsedAt);

    public static ApiKeyResult Ok(ApiKey key, string raw) => new(true, raw, key.Prefix, key.LastUsedAt);
}
