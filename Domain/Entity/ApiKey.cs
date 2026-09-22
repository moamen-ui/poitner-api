namespace Pointer.Domain.Entity;

/// <summary>
/// A user's long-lived personal access key, stored so that the database alone never yields a usable
/// credential: <see cref="Hash"/> is what lookups match (SHA-256 of the raw key), and
/// <see cref="Encrypted"/> is an AES-256-GCM blob that only a caller holding the encryption key can
/// open. It exists because the profile page and the dashboard quick-start promise the key stays
/// re-viewable (pointer-init.md) — a hash alone could not honour that.
///
/// Replaces the plaintext <c>User.ApiKey</c> column (dropped by DB-07). See
/// docs/roadmap/execution/R1-06-api-key-hardening.md.
/// </summary>
public class ApiKey : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Tenant boundary — mirrors the owning <see cref="User"/>'s OwnerId. Strict-own filter.</summary>
    public Guid? OwnerId { get; set; }

    /// <summary>
    /// First 12 characters of the raw key (e.g. <c>ptr_0a1b2c3d</c>) — for display and masking only.
    /// Deliberately short enough to be useless on its own and long enough to identify a key in a list.
    /// </summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the full raw key. Unique; this is what login matches on.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Base64 of AES-256-GCM(nonce ‖ ciphertext ‖ tag) over the full raw key. Only ever read to satisfy
    /// an authenticated "show me my key" request; a failure to decrypt is reported, never repaired by
    /// minting a new key (that would silently rotate every key after an encryption-key change).
    /// </summary>
    public string Encrypted { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="ApiKeyScopes"/> flags. R1 always writes <c>Full</c> and enforces nothing; the column
    /// exists so §25 can add scoped keys without a second migration.
    /// </summary>
    public int Scopes { get; set; } = (int)ApiKeyScopes.Full;

    /// <summary>Human label for multi-key UIs (§25). Null in R1 — every user has one unnamed key.</summary>
    public string? Label { get; set; }

    /// <summary>Updated at most once a minute, so a busy agent does not write on every request.</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>Non-null = dead. Regeneration revokes rather than deletes, so history survives.</summary>
    public DateTime? RevokedAt { get; set; }
}

/// <summary>
/// Capability flags for a key. Declared now, enforced by §25 — R1 issues <c>Full</c> only, so that
/// adding scopes later is a behaviour change rather than a schema change.
/// </summary>
[Flags]
public enum ApiKeyScopes
{
    None = 0,
    Read = 1,
    Apply = 2,
    Manage = 4,
    Full = Read | Apply | Manage,
}
