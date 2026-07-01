using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IAuthService
{
    Task<Result<LoginResponse>> LoginAsync(LoginRequest request);
    Task<Result> RegisterAsync(RegisterRequest request);
    Task<Result> RegisterAdminAsync(RegisterAdminRequest request);
    Result<MeResponse> Me();

    /// <summary>Emails a reset link if the address matches an active account. Always succeeds (no enumeration).</summary>
    Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request);

    /// <summary>Validates the reset token and sets the new password.</summary>
    Task<Result> ResetPasswordAsync(ResetPasswordRequest request);
}
