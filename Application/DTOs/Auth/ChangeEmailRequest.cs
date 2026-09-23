namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/me/change-email. Self-service; password accounts only.</summary>
public class ChangeEmailRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewEmail { get; set; } = string.Empty;
}
