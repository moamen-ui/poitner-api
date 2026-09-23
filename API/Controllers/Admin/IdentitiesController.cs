using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// DB-11c — super-admin GDPR erase of any (non-super-admin) identity. Distinct from
/// <c>UsersController.Delete</c> (which only ends ONE workspace membership): this ends every
/// membership and tombstones the identity everywhere.
/// </summary>
[ApiController]
[Route("api/admin/identities")]
[Produces("application/json")]
[Tags("Identities")]
[Authorize(Policy = Policies.SuperAdmin)]
public class IdentitiesController(IIdentityEraseService erase) : ControllerBase
{
    /// <summary>Erases an identity (GDPR). Blocked while it is the sole Workspace Admin of any workspace (S-13).</summary>
    [HttpDelete("{publicId:guid}")]
    [Audited(AuditActions.IdentityErased)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid publicId)
    {
        var result = await erase.EraseByPublicIdAsync(publicId);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
