using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(IPreferencesService preferencesService, IProfileService profileService, IAuthService authService, Pointer.Application.Abstractions.ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var result = await authService.ChangePasswordAsync(request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("preferences")]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var result = await preferencesService.UpdateAsync(request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("profile")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.UserProfileResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile()
    {
        if (currentUser.Id is null) return Unauthorized();
        var result = await profileService.GetByPublicIdAsync(currentUser.Id.Value);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
