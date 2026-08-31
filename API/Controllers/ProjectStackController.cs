using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Project tech-stack + AI-tool registration. [Authorize] (NOT admin-gated): any authenticated
/// project member can call this, same reasoning as CommentsController — pointer-init.md's
/// automation account isn't necessarily an admin account. Called at most twice in a project's
/// lifetime by a well-behaved caller (pointer-init.md, or skill.md's self-healing branch) — see
/// ProjectService.SetStackAsync for the idempotent/append-if-new write semantics that make repeat
/// calls harmless regardless.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("Projects")]
public class ProjectStackController(IProjectService projectService) : ControllerBase
{
    [HttpGet("api/projects/{key}/stack")]
    [ProducesResponseType(typeof(ProjectStackResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(string key)
    {
        var result = await projectService.GetStackAsync(key);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("api/projects/{key}/stack")]
    [ProducesResponseType(typeof(ProjectStackResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Set(string key, [FromBody] SetProjectStackRequest request)
    {
        var result = await projectService.SetStackAsync(key, request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
