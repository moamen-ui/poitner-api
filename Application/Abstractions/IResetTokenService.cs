namespace Pointer.Application.Abstractions;

/// <summary>
/// Stateless, short-lived password-reset tokens (HMAC-signed, no DB row). A token encodes the
/// user's PublicId + an expiry and is validated by recomputing the signature.
/// </summary>
public interface IResetTokenService
{
    /// <summary>
    /// Create a signed reset token for the user (default TTL ~30 min). The user's current
    /// <paramref name="securityStamp"/> is bound into the token so that bumping the stamp (on use or
    /// any password change) invalidates it — making reset links effectively single-use (H2).
    /// </summary>
    string Create(Guid userPublicId, Guid securityStamp);

    /// <summary>
    /// True if the token's signature is valid AND unexpired; outputs the user's PublicId and the
    /// <paramref name="securityStamp"/> the token was signed with (the caller must compare it to the
    /// user's current stamp).
    /// </summary>
    bool TryValidate(string token, out Guid userPublicId, out Guid securityStamp);
}
