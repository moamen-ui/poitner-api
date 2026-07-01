using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Anonymous invite preview. Returns a SAFE preview (workspace display name + role name only —
/// no tenant GUID, no secrets). Invalid/expired/revoked/used-up codes return NotFound so codes
/// can't be probed for validity beyond existence. Rate-limited with the "signup" policy.
/// </summary>
[ApiController]
[Route("api/invites")]
[Produces("application/json")]
[Tags("Invites")]
public class InvitesController(IInviteService service) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("{code}")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(InvitePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(string code)
    {
        var result = await service.GetPreviewAsync(code);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
