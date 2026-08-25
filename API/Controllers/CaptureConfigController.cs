using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Widget-facing read of a project's page-context capture toggle. [Authorize] (NOT anonymous): keys
/// are owner-scoped, so a key-only anonymous resolve would collide across tenants — same reasoning as
/// PredefinedActionsController. Called once at widget init to decide whether to buffer console/network
/// events at all and whether to show "Report as a bug".
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("Projects")]
public class CaptureConfigController(IProjectService projectService) : ControllerBase
{
    [HttpGet("api/projects/{key}/capture-config")]
    [ProducesResponseType(typeof(CaptureConfigResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string key)
    {
        var result = await projectService.GetCaptureConfigAsync(key);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
