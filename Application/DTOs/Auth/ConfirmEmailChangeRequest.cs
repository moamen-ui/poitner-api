namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/auth/confirm-email-change — the scoped, one-time token e-mailed to
/// the new address by POST /api/me/change-email.</summary>
public class ConfirmEmailChangeRequest
{
    public string Token { get; set; } = string.Empty;
}
