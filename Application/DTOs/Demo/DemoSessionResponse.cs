namespace Pointer.Application.DTOs.Demo;

public class DemoSessionResponse
{
    public string Token { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string ServerUrl { get; set; } = string.Empty;
}
