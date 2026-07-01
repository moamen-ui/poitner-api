namespace Pointer.Application.DTOs.Demo;

public class DemoSessionResponse
{
    public string Token { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// True when the credentials were emailed to the requester. When true, <see cref="Password"/>
    /// is intentionally blank (the user reads it from their inbox); when false (email disabled,
    /// capped, or failed) the password is returned inline as a fallback so the demo isn't blocked.
    /// </summary>
    public bool EmailSent { get; set; }
}
