using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-14 §3.3 — the e-mailed link that proves control of <see cref="User.Email"/>. A scoped
/// <c>IResetTokenService</c> token, purpose <c>TokenPurposes.VerifyEmail</c>; never a table.
/// </summary>
public interface IEmailVerificationService
{
    /// <summary>
    /// Best-effort: mints and e-mails a verification link. Skips silently for an identity that is
    /// already verified, demo, passwordless, or super admin. Called by every creation site whose
    /// DB-14 §3.2 rule is "send mail" (right after that method's own <c>SaveChangesAsync</c>).
    /// </summary>
    Task SendAsync(User identity);

    /// <summary>POST /api/me/verification/resend — one per 5 minutes per identity, on top of the
    /// per-IP "signup" rate limit.</summary>
    Task<Result> ResendAsync();

    /// <summary>POST /api/auth/verify-email — anonymous; redeems the token minted by
    /// <see cref="SendAsync"/>.</summary>
    Task<Result> ConfirmAsync(string token);

    /// <summary>DB-14 §3.4 review fix: clears the gate's 60s cache key (<c>emailverified:{pid}</c>)
    /// for <paramref name="publicId"/>. Must be called right after every write that flips
    /// <c>User.EmailVerifiedAt</c> (to a value or to null) so a just-flipped identity's next admin
    /// write reflects the change immediately instead of waiting out the TTL.</summary>
    void InvalidateGate(Guid publicId);
}
