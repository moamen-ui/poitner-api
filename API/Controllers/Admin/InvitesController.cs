using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// Tenant invite-link management. Admin-gated + tenant-scoped via the EF query filter (strict-own).
/// List/revoke can NEVER reach another tenant's invite (revoke uses an explicit own-owner load).
/// </summary>
[ApiController]
[Route("api/admin/invites")]
[Produces("application/json")]
[Tags("Invites")]
[Authorize(Policy = Policies.Admin)]
public class InvitesController(IInviteService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<InviteResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await service.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.InviteCreated)]
    [ProducesResponseType(typeof(InviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateInviteRequest request)
    {
        var result = await service.CreateAsync(request);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [Audited(AuditActions.InviteRevoked)]
    // DB-18: revoking an invite (incl. a quick-access link) is access-removing — always allowed while frozen.
    [AllowWhenWorkspacePaused]
    [ProducesResponseType(typeof(InviteRevokeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(int id)
    {
        var result = await service.RevokeAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Replace a quick-access invite's magic link. The previous link stops working immediately;
    /// the client account and its project stay as they are, so the admin can hand over a new link
    /// after a leak without re-inviting anyone.
    /// </summary>
    [HttpPost("{id:int}/quick-link/rotate")]
    [Audited(AuditActions.InviteQuickLinkRotated)]
    // DB-18: rotating a leaked link revokes the old one — access-removing, allowed while frozen.
    [AllowWhenWorkspacePaused]
    [ProducesResponseType(typeof(InviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RotateQuickLink(int id)
    {
        var result = await service.RotateQuickLinkAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
