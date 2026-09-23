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

    /// <summary>
    /// Purpose-bound variant (DB-11c). Format "{publicId:N}.{stamp:N}.{expUnix}.{purpose}.{payload}.{sig}" — SIX
    /// parts, signed over "id|stamp|exp|purpose|payload"; payload is base64url(UTF-8) or empty. A scoped token never
    /// validates through <see cref="TryValidate"/> (which requires four parts), and the reverse never validates
    /// through the six-part method below either (purpose must also match), so an e-mailed link can only do the one
    /// thing it was minted for. Same 30-minute TTL; same stamp binding — the caller rotates the user's security
    /// stamp on success, which makes the token single-use. <paramref name="purpose"/> must match ^[a-z-]+$ (a '.'
    /// would break the split; this is asserted).
    /// </summary>
    string CreateScoped(Guid userPublicId, Guid securityStamp, string purpose, string? payload = null);

    /// <summary>
    /// True only for a six-part token whose purpose equals <paramref name="purpose"/> (ordinal), whose signature
    /// and expiry check out; outputs the user's PublicId, the security stamp it was signed with, and the decoded
    /// payload (null when empty).
    /// </summary>
    bool TryValidateScoped(
        string token,
        string purpose,
        out Guid userPublicId,
        out Guid securityStamp,
        out string? payload
    );
}
