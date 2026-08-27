namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/me/change-password. Self-service, any authenticated role.</summary>
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
