using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Extensions;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Widget-facing effective-set read. [Authorize] (NOT anonymous): keys are owner-scoped, so a
/// key-only anonymous resolve would collide across tenants. The picker only renders post-login,
/// so we resolve the tenant from the JWT and scope through the strict project resolver.
/// Returns id + text ONLY — never the prompt.
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("PredefinedActions")]
public class PredefinedActionsController(IPredefinedActionService service) : ControllerBase
{
    [HttpGet("api/projects/{key}/predefined-actions")]
    [ProducesResponseType(typeof(List<PredefinedActionOption>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetForProject(string key)
    {
        var result = await service.GetEffectiveForProjectAsync(key, User.GetId());
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
