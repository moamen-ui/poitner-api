namespace Pointer.Application.DTOs.Demo;

/// <summary>Body for POST /api/demo/upgrade — converts an ephemeral demo user into a permanent account.</summary>
public class UpgradeDemoRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }

    /// <summary>DB-17 §3.4 (D17.3): the workspace's new name. Null/blank → <c>Workspace.PlaceholderName</c>
    /// (never "Demo Workspace") so the "name your workspace" prompt appears.</summary>
    public string? WorkspaceName { get; set; }
}
