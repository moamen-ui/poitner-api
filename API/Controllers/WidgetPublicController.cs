using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Anonymous, called by the widget itself at boot — before it renders anything, before any login —
/// to decide whether it should show up on this page at all. See
/// ProjectService.CheckWidgetActiveAsync for the exact gating rules.
/// </summary>
[ApiController]
[Route("api/public/projects/{key}/widget-status")]
[AllowAnonymous]
[Tags("Projects")]
public class WidgetPublicController(IProjectService projectService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(WidgetActivationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string key, [FromQuery] string? origin)
    {
        var result = await projectService.CheckWidgetActiveAsync(key, origin);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
