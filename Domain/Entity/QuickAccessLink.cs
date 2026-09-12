namespace Pointer.Domain.Entity;

/// <summary>
/// A magic link that signs one provisioned quick-access user into one project, with no password.
/// </summary>
/// <remarks>
/// Distinct from the audit <see cref="Invite"/> row on purpose: that row records who was invited and
/// when, and it is single-use by design. This is the credential, and it stays usable for its whole
/// TTL — a client returning after the 12-hour JWT expires must be silently re-signed-in, which
/// single-use would break.
///
/// The raw token is NEVER stored. Only its SHA-256 is, so a database read cannot reconstruct a
/// working link. The token is 256 bits of randomness, redemption is rate-limited, and rotate/revoke
/// both exist — but it is still a bearer credential bound to a low-privilege account, and admins
/// are told to treat it like a password.
/// </remarks>
public class QuickAccessLink : BaseEntity
{
    public Guid? OwnerId { get; set; }

    /// <summary>The provisioned quick-access user's PublicId.</summary>
    public Guid UserId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>The audit invite this link was issued for.</summary>
    public int InviteId { get; set; }

    /// <summary>SHA-256 hex of the raw token. The raw value exists only in the link we hand out.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>0 = unlimited within the TTL, which is the default.</summary>
    public int MaxUses { get; set; }

    public int Uses { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
}
