namespace Pointer.Application.DTOs.User;

/// <summary>Body for DELETE /api/me (DB-11c). Password-account confirmation for GDPR self-erase.</summary>
public class DeleteMyAccountRequest
{
    public string Password { get; set; } = string.Empty;
}
