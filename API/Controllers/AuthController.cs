using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IAuthService authService, ISettingsService settingsService) : ControllerBase
{
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await authService.LoginAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [AllowAnonymous]
    [HttpPost("register")]
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
    public IActionResult Me()
    {
        var result = authService.Me();
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
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
}
