using Pointer.Application.DTOs.Auth;

namespace Pointer.Application.DTOs.Demo;

/// <summary>
/// Result of a successful demo upgrade. Same shape as <see cref="LoginResponse"/> (token + MeResponse)
/// so the dashboard can reuse its post-login token-swap path.
/// </summary>
public class UpgradeDemoResponse
{
    public string Token { get; set; } = string.Empty;
    public MeResponse User { get; set; } = null!;
}
