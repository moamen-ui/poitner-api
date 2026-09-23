using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request);

    /// <summary>
    /// R5-61 §3.3 — POST /api/auth/mfa/verify: completes a login that returned
    /// <c>status: "mfa_required"</c>. Reads the caller from the scoped <c>mfa_pending</c> token
    /// (<c>ICurrentUser.Scope == "mfa_pending"</c> — fenced to this exact path by
    /// <c>AuthenticationExtensions</c>/<c>MfaPendingScopeFence</c>), validates the TOTP/recovery
    /// code, and on success issues a normal full session JWT. Counts a wrong code against the same
    /// per-e-mail <c>ILoginAttemptLimiter</c> budget as a wrong password.
    /// </summary>
    Task<Result<LoginResponse>> VerifyMfaLoginAsync(MfaCodeRequest request);

    /// <summary>Exchanges a long-lived personal API key (an `api_keys` row) for a normal JWT — same
    /// response shape and claims as LoginAsync, just a different credential.</summary>
    Task<Result<LoginResponse>> LoginWithApiKeyAsync(LoginWithApiKeyRequest request);
    Task<Result> RegisterAsync(RegisterRequest request);
    Task<Result> RegisterAdminAsync(RegisterAdminRequest request);
    Task<Result<MeResponse>> MeAsync();

    /// <summary>Emails a reset link if the address matches an active account. Always succeeds (no enumeration).</summary>
    Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request);

    /// <summary>Validates the reset token and sets the new password.</summary>
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request);

    /// <summary>Self-service password change for the current user. Emails a notification on success.</summary>
    Task<Result> ChangePasswordAsync(ChangePasswordRequest request);

    /// <summary>
    /// DB-11d: password-confirmed request to change the caller's e-mail. Sends a confirmation link
    /// to the new address and a notice to the old one; nothing is written until the link is
    /// confirmed. Passwordless and super-admin identities are refused (§3.1).
    /// </summary>
    Task<Result> RequestEmailChangeAsync(ChangeEmailRequest request);

    /// <summary>
    /// DB-11d: redeems the scoped token from POST /api/me/change-email — sets the (normalised) new
    /// e-mail, rotates the identity's security stamp (every session ends), and notifies the old
    /// address. Anonymous; the token is the credential.
    /// </summary>
    Task<Result> ConfirmEmailChangeAsync(string token);

    /// <summary>Redeems a quick-access magic-link token for a normal session JWT.</summary>
    Task<Result<LoginResponse>> LoginWithInviteAsync(string token);

    /// <summary>
    /// DB-11b: exchanges a selection token (status "choose-workspace") — or an ordinary full token,
    /// for a signed-in user switching workspaces — for a full JWT of the chosen membership.
    /// </summary>
    Task<Result<LoginResponse>> SwitchWorkspaceAsync(Guid workspaceId);
}
