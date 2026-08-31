using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Anonymous marketing endpoint for the landing page (mirrors PlansPublicController). Returns only
/// anonymized cross-tenant counts — see ProjectService.GetStacksSummaryAsync, the one place this
/// service deliberately bypasses the tenant query filter. Never exposes project names, tenant IDs,
/// or any other identifying data.
/// </summary>
[ApiController]
[Route("api/public/stacks-summary")]
[AllowAnonymous]
[Tags("Projects")]
public class StacksPublicController(IProjectService projectService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(Application.DTOs.Project.StacksSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var result = await projectService.GetStacksSummaryAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
