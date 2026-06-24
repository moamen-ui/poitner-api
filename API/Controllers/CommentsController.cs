using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Extensions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
public class CommentsController(ICommentService commentService) : ControllerBase
{
    [HttpPost("api/projects/{key}/comments")]
    public async Task<IActionResult> Create(string key, [FromBody] CreateCommentRequest request)
    {
        var result = await commentService.CreateAsync(key, request, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("api/projects/{key}/comments")]
    public async Task<IActionResult> List(string key, [FromQuery] CommentFilter filter)
    {
        var result = await commentService.ListAsync(key, filter, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("api/comments/{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var result = await commentService.GetByIdAsync(id, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("api/comments/{id:int}")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateCommentStatusRequest request)
    {
        var result = await commentService.UpdateStatusAsync(id, request, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // Edit a comment's body and/or remove its uploaded image. Author-only (enforced in the service).
    [HttpPut("api/comments/{id:int}")]
    public async Task<IActionResult> Edit(int id, [FromBody] EditCommentRequest request)
    {
        var result = await commentService.EditAsync(id, request, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("api/comments/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await commentService.DeleteAsync(id, User.GetId(), User.IsAdmin());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
