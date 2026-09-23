namespace Pointer.Application.DTOs.Auth;

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// DB-11b: sent by the widget (never the dashboard) so a stakeholder with several workspace
    /// memberships is auto-routed to the one that owns this project instead of seeing a picker (D11).
    /// An unknown key is simply ignored — falls through to the ordinary decision table.
    /// </summary>
    public string? ProjectKey { get; set; }
}
