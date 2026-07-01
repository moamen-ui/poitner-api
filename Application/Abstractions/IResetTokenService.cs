namespace Pointer.Application.Abstractions;

/// <summary>
/// Stateless, short-lived password-reset tokens (HMAC-signed, no DB row). A token encodes the
/// user's PublicId + an expiry and is validated by recomputing the signature.
/// </summary>
public interface IResetTokenService
{
    /// <summary>Create a signed reset token for the user (default TTL ~30 min).</summary>
    string Create(Guid userPublicId);

    /// <summary>True if the token's signature is valid AND unexpired; outputs the user's PublicId.</summary>
    bool TryValidate(string token, out Guid userPublicId);
}
