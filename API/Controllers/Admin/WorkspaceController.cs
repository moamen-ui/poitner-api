using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// Workspace-level settings (R4-01). Workspace-admin surface — [Authorize(Policy = Admin)] keeps
/// non-admin stakeholders out with a plain 403; the service additionally refuses super admins
/// (platform-only, no workspace of their own — the dashboard hides the card for them) and
/// quick-access users, mirroring AiRuleService.CreateAsync.
/// </summary>
[ApiController]
[Route("api/admin/workspace")]
[Produces("application/json")]
[Tags("Workspace")]
[Authorize(Policy = Policies.Admin)]
public class WorkspaceController(ICommentFieldService commentFields) : ControllerBase
{
    /// <summary>All comment-field definitions of the caller's workspace, including disabled ones, sorted.</summary>
    [HttpGet("comment-fields")]
    [ProducesResponseType(typeof(CommentFieldDefinitionsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCommentFields()
    {
        var result = await commentFields.GetDefinitionsAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Replaces the whole definition list (empty list deletes all definitions).</summary>
    [HttpPut("comment-fields")]
    [ProducesResponseType(typeof(CommentFieldDefinitionsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateCommentFields([FromBody] UpdateCommentFieldDefinitionsRequest request)
    {
        var result = await commentFields.UpdateDefinitionsAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
