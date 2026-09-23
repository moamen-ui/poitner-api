using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request);

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

    /// <summary>Redeems a quick-access magic-link token for a normal session JWT.</summary>
    Task<Result<LoginResponse>> LoginWithInviteAsync(string token);

    /// <summary>
    /// DB-11b: exchanges a selection token (status "choose-workspace") — or an ordinary full token,
    /// for a signed-in user switching workspaces — for a full JWT of the chosen membership.
    /// </summary>
    Task<Result<LoginResponse>> SwitchWorkspaceAsync(Guid workspaceId);
}
