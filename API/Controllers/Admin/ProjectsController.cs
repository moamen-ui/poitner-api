using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

// Broadened from [Authorize(Policy=Admin)] to plain [Authorize]: stakeholders may now list/create
// their own projects and update/delete per fine-grained rules enforced in ProjectService. The
// apply-queue action stays admin-gated at the action level (it is the only prompt-emitting surface).
[ApiController]
[Route("api/admin/projects")]
[Produces("application/json")]
[Authorize]
public class ProjectsController(IProjectService projectService, ICommentService commentService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<ProjectResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await projectService.ListAsync();
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest request)
    {
        var result = await projectService.CreateAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProjectRequest request)
    {
        var result = await projectService.UpdateAsync(id, request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await projectService.DeleteAsync(id);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Admin-gated apply-queue export (the .NET analogue of pending.json). Returns self-contained
    /// comment items INCLUDING the snapshotted predefined-action prompt so the apply-time LLM
    /// receives it — this is the ONLY endpoint that surfaces the prompt. Filter by
    /// <c>?status=ReadyToApply</c> to fetch the queue.
    ///
    /// BINDING #1: this action carries its OWN admin policy because the controller is only
    /// [Authorize] now — a non-admin must never reach the prompt-emitting queue.
    /// </summary>
    [HttpGet("{key}/apply-queue")]
    [Authorize(Policy = Policies.Admin)]
    [ProducesResponseType(typeof(PagedData<CommentApplyItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplyQueue(string key, [FromQuery] CommentFilter filter)
    {
        var result = await commentService.ListApplyQueueAsync(key, filter);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
