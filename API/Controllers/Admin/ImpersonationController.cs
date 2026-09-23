using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Impersonation;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// DB-13 §3.6 (F2) — ends a live impersonation session, and lists sessions (a super admin sees
/// every session; a workspace admin sees only the sessions that targeted their own workspace, with
/// the operator's identity omitted — D13.6).
/// </summary>
[ApiController]
[Route("api/admin/impersonation")]
[Tags("Impersonation")]
[Produces("application/json")]
[Authorize(Policy = Policies.Admin)]
public class ImpersonationController(IImpersonationService impersonation) : ControllerBase
{
    /// <summary>
    /// The only write an impersonation token's fence allows (ImpersonationScopeFence) — ends its
    /// own session. A plain super-admin token may instead end a specific session by id in the body.
    /// </summary>
    [HttpPost("end")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [Audited(AuditActions.ImpersonationEnded)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> End([FromBody] EndImpersonationRequest? request)
    {
        var result = await impersonation.EndAsync(request?.SessionId);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Super admin: every session (optionally filtered by workspace). Workspace admin: their own workspace's sessions only.</summary>
    [HttpGet]
    [NoAudit("read")]
    [ProducesResponseType(typeof(PagedData<ImpersonationSessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? workspaceId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50
    )
    {
        var result = await impersonation.ListAsync(workspaceId, page, pageSize);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
