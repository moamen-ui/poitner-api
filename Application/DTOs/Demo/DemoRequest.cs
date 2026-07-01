namespace Pointer.Application.DTOs.Demo;

/// <summary>Body for POST /api/demo — the email the demo credentials are sent to (email-gated).</summary>
public class DemoRequest
{
    public string Email { get; set; } = string.Empty;
}
