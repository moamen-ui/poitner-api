namespace Pointer.Application.DTOs.Auth;

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public MeResponse User { get; set; } = null!;
}
