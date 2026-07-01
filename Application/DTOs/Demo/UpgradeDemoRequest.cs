namespace Pointer.Application.DTOs.Demo;

/// <summary>Body for POST /api/demo/upgrade — converts an ephemeral demo user into a permanent account.</summary>
public class UpgradeDemoRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}
