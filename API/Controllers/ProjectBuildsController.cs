using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Build;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// "This build is live." Closes the loop between a comment being applied and the person who left
/// it being able to see the fix.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("Builds")]
public class ProjectBuildsController(IProjectBuildService buildService) : ControllerBase
{
    [NoAudit("CLI telemetry")]
    [HttpPost("api/projects/{key}/builds")]
    [EnableRateLimiting("builds")]
    [ProducesResponseType(typeof(ReportBuildResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Report(string key, [FromBody] ReportBuildRequest request)
    {
        var result = await buildService.ReportAsync(key, request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
