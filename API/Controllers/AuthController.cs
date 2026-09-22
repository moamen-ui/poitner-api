using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(
    IAuthService authService,
    ISettingsService settingsService,
    IInviteService inviteService,
    IDeviceLoginService deviceLoginService) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    [EnableRateLimiting("login-ip")]
    [RequestSizeLimit(64 * 1024)]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result<LoginResponse>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await authService.LoginAsync(request);
        if (result.IsLocked || result.Data?.Status == "locked")
        {
            if (result.RetryAfterSeconds.HasValue)
            {
                Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
            }
            return StatusCode(StatusCodes.Status429TooManyRequests, result);
        }
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Exchanges a long-lived personal API key for a normal JWT — an alternative to
    /// POST /login for AI/automation tooling (e.g. skill.md), same response shape.</summary>
    [AllowAnonymous]
    /// <summary>
    /// Redeems a quick-access magic link. Anonymous by necessity — the caller has no session yet,
    /// the token IS the credential.
    /// </summary>
    [HttpPost("login-with-invite")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LoginWithInvite([FromBody] LoginWithInviteRequest request)
    {
        var result = await authService.LoginWithInviteAsync(request.Token);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("login-with-key")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> LoginWithKey([FromBody] LoginWithApiKeyRequest request)
    {
        var result = await authService.LoginWithApiKeyAsync(request);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [AllowAnonymous]
    [HttpPost("register")]
    [EnableRateLimiting("signup")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await authService.RegisterAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Me()
    {
        var result = await authService.MeAsync();
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [AllowAnonymous]
    [HttpPost("forgot-password")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        // Always 200 — never reveal whether the email is registered.
        var result = await authService.RequestPasswordResetAsync(request);
        return Ok(result);
    }

    [AllowAnonymous]
    [HttpPost("reset-password")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        var result = await authService.ResetPasswordAsync(request);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [AllowAnonymous]
    [HttpGet("signup-enabled")]
    [ProducesResponseType(typeof(SignupEnabledResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SignupEnabled()
    {
        var enabled = await settingsService.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, fallback: false);
        return Ok(new SignupEnabledResponse { Enabled = enabled });
    }

    [AllowAnonymous]
    [HttpPost("register-admin")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterAdmin([FromBody] RegisterAdminRequest request)
    {
        var result = await authService.RegisterAdminAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Accept a tenant invite: creates an Approved + active tenant-scoped user (skips the approval
    /// queue — the invite is the authorization) and returns a login token (auto-signin).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("register-invite")]
    [EnableRateLimiting("signup")]
    [Tags("Invites")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterInvite([FromBody] AcceptInviteRequest request)
    {
        var result = await inviteService.AcceptAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // ── Device-code ("browser") sign-in for the CLI, mirroring `gh auth login` ─────────────────
    // See Pointer.Domain.Entity.DeviceLogin for the full flow.

    /// <summary>Mints a fresh device/user code pair for `pointer login`. Anonymous — there is no
    /// session yet.</summary>
    [AllowAnonymous]
    [HttpPost("device/start")]
    [EnableRateLimiting("device-start")]
    [ProducesResponseType(typeof(DeviceLoginStartResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeviceStart([FromBody] DeviceLoginStartRequest request)
    {
        var result = await deviceLoginService.StartAsync(request);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Polled by the CLI every `intervalSeconds` until the code is approved/denied/expired.
    /// Anonymous by necessity — the CLI has no session until this returns the key.</summary>
    [AllowAnonymous]
    [HttpPost("device/poll")]
    [EnableRateLimiting("device-poll")]
    [ProducesResponseType(typeof(DeviceLoginPollResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DevicePoll([FromBody] DeviceLoginPollRequest request)
    {
        var result = await deviceLoginService.PollAsync(request);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>For the dashboard's /cli-login page: what a user code is asking for, before the
    /// user decides. Super admins are refused — they have no personal API key to hand out.</summary>
    [Authorize]
    [HttpGet("device/{userCode}")]
    [ProducesResponseType(typeof(DeviceLoginInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeviceInfo(string userCode)
    {
        var result = await deviceLoginService.GetInfoAsync(userCode);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [Authorize]
    [HttpPost("device/approve")]
    [ProducesResponseType(typeof(DeviceLoginInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeviceApprove([FromBody] DeviceLoginUserCodeRequest request)
    {
        var result = await deviceLoginService.ApproveAsync(request.UserCode);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [Authorize]
    [HttpPost("device/deny")]
    [ProducesResponseType(typeof(DeviceLoginInfoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeviceDeny([FromBody] DeviceLoginUserCodeRequest request)
    {
        var result = await deviceLoginService.DenyAsync(request.UserCode);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
