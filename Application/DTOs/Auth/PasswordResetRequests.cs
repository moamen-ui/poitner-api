namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/auth/forgot-password.</summary>
public class ForgotPasswordRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>Body for POST /api/auth/reset-password.</summary>
public class ResetPasswordRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
