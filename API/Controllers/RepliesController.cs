using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Extensions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("Comments")]
public class RepliesController(ICommentService commentService) : ControllerBase
{
    [HttpPost("api/comments/{id:int}/replies")]
    [ProducesResponseType(typeof(ReplyResponse), StatusCodes.Status200OK)]
    // Shares the comments budget deliberately: a reply is the same write, and leaving it
    // unthrottled would just move a burst one endpoint to the left.
    [EnableRateLimiting("comments")]
    public async Task<IActionResult> AddReply(int id, [FromBody] AddReplyRequest request)
    {
        var result = await commentService.AddReplyAsync(id, request, User.GetId(), Request.RequestOrigin());
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
