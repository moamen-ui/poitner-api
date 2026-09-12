namespace Pointer.Application.DTOs.Auth;

/// <summary>The raw quick-access magic-link token, as it appeared in <c>?pointer_invite=</c>.</summary>
public class LoginWithInviteRequest
{
    public string Token { get; set; } = string.Empty;
}
