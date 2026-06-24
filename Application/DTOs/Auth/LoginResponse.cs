namespace Pointer.Application.DTOs.Auth;

public class LoginResponse
{
    /// <summary>
    /// Always present so the web component can branch on it for ALL outcomes:
    /// "ok" | "pending" | "rejected" | "disabled". On non-"ok" results, Token and User are null.
    /// </summary>
    public string Status { get; set; } = string.Empty;
    public string? Token { get; set; }
    public MeResponse? User { get; set; }
}
