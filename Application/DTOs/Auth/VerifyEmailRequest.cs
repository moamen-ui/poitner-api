namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/auth/verify-email — the scoped, one-time token e-mailed by
/// <c>IEmailVerificationService.SendAsync</c>.</summary>
public class VerifyEmailRequest
{
    public string Token { get; set; } = string.Empty;
}
