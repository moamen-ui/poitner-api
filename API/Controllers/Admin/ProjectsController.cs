using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/projects")]
[Produces("application/json")]
[Authorize(Policy = Policies.Admin)]
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
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Admin-gated apply-queue export (the .NET analogue of pending.json). Returns self-contained
    /// comment items INCLUDING the snapshotted predefined-action prompt so the apply-time LLM
    /// receives it — this is the ONLY endpoint that surfaces the prompt. Filter by
    /// <c>?status=ReadyToApply</c> to fetch the queue.
    /// </summary>
    [HttpGet("{key}/apply-queue")]
    [ProducesResponseType(typeof(PagedData<CommentApplyItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ApplyQueue(string key, [FromQuery] CommentFilter filter)
    {
        var result = await commentService.ListApplyQueueAsync(key, filter);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
