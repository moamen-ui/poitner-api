using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Response;
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
public class WorkspaceController(
    ICommentFieldService commentFields,
    IWorkspaceService workspaces,
    IWorkspaceLifecycleService lifecycle
) : ControllerBase
{
    /// <summary>The caller's workspace: id, own name, placeholder flag.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var result = await workspaces.GetAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Renames the caller's workspace (1–120 chars). Header label and invite preview follow.</summary>
    [HttpPut("name")]
    [Audited(AuditActions.WorkspaceRenamed)]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename([FromBody] UpdateWorkspaceNameRequest request)
    {
        var result = await workspaces.RenameAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

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
    [Audited(AuditActions.WorkspaceCommentFieldsUpdated)]
    [ProducesResponseType(typeof(CommentFieldDefinitionsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateCommentFields([FromBody] UpdateCommentFieldDefinitionsRequest request)
    {
        var result = await commentFields.UpdateDefinitionsAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // ── DB-18: self-service pause / delete (Danger zone) ─────────────────────────────────────

    /// <summary>Pauses the caller's workspace: frozen (read-only), reversible, no e-mail (D18.7).</summary>
    [HttpPost("pause")]
    [Audited(AuditActions.WorkspacePaused)]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Pause()
    {
        var result = await lifecycle.PauseAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Resumes a self-paused workspace. Allowed while frozen (self-paused) — an operator
    /// pause refuses with a specific message (§3.4).</summary>
    [HttpPost("resume")]
    [Audited(AuditActions.WorkspaceResumed)]
    [AllowWhenWorkspacePaused]
    [AllowUnverified]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resume()
    {
        var result = await lifecycle.ResumeAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>E-mails the requesting admin a one-time, 30-minute confirm-deletion link (D18.2).</summary>
    [HttpPost("deletion/request")]
    [Audited(AuditActions.WorkspaceDeletionRequested)]
    [AllowWhenWorkspacePaused]
    [EnableRateLimiting("danger")]
    [ProducesResponseType(typeof(WorkspaceDeletionRequestResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestDeletion()
    {
        var result = await lifecycle.RequestDeletionAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Cancels a pending request or a scheduled deletion; voids any outstanding link.</summary>
    [HttpPost("deletion/cancel")]
    [Audited(AuditActions.WorkspaceDeletionCancelled)]
    [AllowWhenWorkspacePaused]
    [AllowUnverified]
    [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelDeletion()
    {
        var result = await lifecycle.CancelDeletionAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
