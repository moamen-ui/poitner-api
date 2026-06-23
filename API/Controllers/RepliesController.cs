using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Extensions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
public class RepliesController(ICommentService commentService) : ControllerBase
{
    [HttpPost("api/comments/{id:int}/replies")]
    public async Task<IActionResult> AddReply(int id, [FromBody] AddReplyRequest request)
    {
        var result = await commentService.AddReplyAsync(id, request, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
