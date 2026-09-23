namespace Pointer.Domain.Entity;

/// <summary>
/// R5-61: one single-use MFA recovery code, minted 8-at-a-time when TOTP enrollment completes
/// (<c>POST /api/me/mfa/verify</c>). Only <see cref="CodeHash"/> is stored — the plain code is
/// returned to the caller exactly once and never persisted. Rows are kept (not deleted) after use
/// so <see cref="UsedAt"/> stands as the audit trail; disabling MFA deletes every row for the user
/// (§3.2) since a fresh enrollment mints a brand-new set.
/// </summary>
public class UserRecoveryCode : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Lowercase hex SHA-256 of the plain recovery code (<c>IApiKeyProtector.Hash()</c>) —
    /// the same one-way hash used for API keys.</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>Non-null once this code has been redeemed. A used code never validates again.</summary>
    public DateTime? UsedAt { get; set; }
}
