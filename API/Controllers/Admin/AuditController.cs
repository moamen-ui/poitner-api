using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Audit;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// DB-12 §3.8a — the audit log read API. The workspace view (<c>GET /api/admin/audit</c>) is a
/// workspace admin's own Security log; <c>GET /api/admin/audit/all</c> is the operator view across
/// every workspace, super admins only. Reads of the audit log itself are not audited
/// (<c>[NoAudit]</c>).
/// </summary>
[ApiController]
[Route("api/admin/audit")]
[Tags("Audit")]
[Produces("application/json")]
[Authorize(Policy = Policies.Admin)]
public class AuditController(IAuditQueryService audit) : ControllerBase
{
    /// <summary>The caller's own workspace's audit rows, newest first. A super admin is Forbidden here — use /all.</summary>
    [HttpGet]
    [NoAudit("read of the audit log itself")]
    [ProducesResponseType(typeof(PagedData<AuditEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] AuditQuery q)
    {
        var result = await audit.ListForWorkspaceAsync(q);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Every workspace's rows plus operator-level (owner_id NULL) rows — super admins only.</summary>
    [HttpGet("all")]
    [NoAudit("read of the audit log itself")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [ProducesResponseType(typeof(PagedData<AuditEventDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> All([FromQuery] AuditQuery q)
    {
        var result = await audit.ListAllAsync(q);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
