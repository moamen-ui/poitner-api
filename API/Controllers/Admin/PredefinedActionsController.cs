using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// Tenant-wide predefined-action management (ProjectId == null). Admin-gated + tenant-scoped
/// via the EF query filter. Project-scoped actions are managed through the nested reconcile on
/// project create/edit instead. Includes the prompt (admin-only surface).
/// </summary>
[ApiController]
[Route("api/admin/predefined-actions")]
[Produces("application/json")]
[Tags("PredefinedActions")]
[Authorize(Policy = Policies.Admin)]
public class PredefinedActionsController(IPredefinedActionService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<PredefinedActionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await service.ListTenantAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(PredefinedActionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreatePredefinedActionRequest request)
    {
        var result = await service.CreateTenantAsync(request);
        if (result.IsForbidden) return Forbid();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(PredefinedActionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdatePredefinedActionRequest request)
    {
        var result = await service.UpdateAsync(id, request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await service.DeleteAsync(id);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
