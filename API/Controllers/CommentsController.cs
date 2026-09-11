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
    [RequestSizeLimit(262144)] // 256KB — an element capture (snapshot/styles/rules) is small; cap abuse.
    [ProducesResponseType(typeof(CommentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create(string key, [FromBody] CreateCommentRequest request)
    {
        var result = await commentService.CreateAsync(key, request, User.GetId(), RequestOrigin());
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("api/projects/{key}/comments")]
    [ProducesResponseType(typeof(Pointer.Application.Response.PagedData<CommentListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string key, [FromQuery] CommentFilter filter)
    {
        // ?view=summary: an AI-agent-only escape hatch (see pointer.sh/skill.md) for the lean
        // CommentSummaryDto shape — deliberately not modeled in the typed dashboard clients, which
        // never pass it.
        if (string.Equals(filter.View, "summary", StringComparison.OrdinalIgnoreCase))
        {
            var summary = await commentService.ListSummaryAsync(key, filter, User.GetId());
            if (summary.IsNotFound) return NotFound(summary);
            if (summary.IsConflict) return Conflict(summary);
            return summary.IsSuccess ? Ok(summary) : BadRequest(summary);
        }

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
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
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

    // Toggle a comment's private flag. Author-only (enforced in the service).
    [HttpPatch("api/comments/{id:int}/visibility")]
    public async Task<IActionResult> SetVisibility(int id, [FromBody] SetVisibilityRequest request)
    {
        var result = await commentService.SetVisibilityAsync(id, User.GetId(), request.IsPrivate);
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

    /// <summary>
    /// The request's origin for the allow-list check: the Origin header, else the origin part of
    /// Referer. Null when neither is present — which is normal for the CLI and AI agents, and is
    /// handled by IsOriginAllowedAsync rather than treated as a rejection here.
    /// </summary>
    private string? RequestOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin))
            return origin;

        var referer = Request.Headers.Referer.ToString();
        return Uri.TryCreate(referer, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}"
            : null;
    }
}
