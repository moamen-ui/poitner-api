using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request);

    /// <summary>Exchanges a long-lived personal API key (User.ApiKey) for a normal JWT — same
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
}
