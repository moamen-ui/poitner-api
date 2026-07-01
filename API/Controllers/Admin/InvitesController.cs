using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
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
    [ProducesResponseType(typeof(InviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateInviteRequest request)
    {
        var result = await service.CreateAsync(request);
        if (result.IsForbidden) return Forbid();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Revoke(int id)
    {
        var result = await service.RevokeAsync(id);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
