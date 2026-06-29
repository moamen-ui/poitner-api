using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Settings;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Settings")]
public class SettingsController(ISettingsService settingsService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var enabled = await settingsService.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, fallback: false);
        return Ok(Result<SettingsResponse>.Success(new SettingsResponse
        {
            ScopedAdminSignupEnabled = enabled
        }));
    }

    [HttpPut]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request)
    {
        await settingsService.SetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, request.ScopedAdminSignupEnabled);
        return Ok(Result<SettingsResponse>.Success(new SettingsResponse
        {
            ScopedAdminSignupEnabled = request.ScopedAdminSignupEnabled
        }));
    }
}
