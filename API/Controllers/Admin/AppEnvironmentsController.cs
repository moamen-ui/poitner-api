using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.AppEnvironment;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/environments")]
[Authorize(Policy = Policies.Admin)]
public class AppEnvironmentsController(IAppEnvironmentService appEnvironmentService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<AppEnvironmentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await appEnvironmentService.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.EnvironmentCreated)]
    [ProducesResponseType(typeof(AppEnvironmentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateAppEnvironmentRequest request)
    {
        var result = await appEnvironmentService.CreateAsync(request);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [Audited(AuditActions.EnvironmentUpdated)]
    [ProducesResponseType(typeof(AppEnvironmentResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAppEnvironmentRequest request)
    {
        var result = await appEnvironmentService.UpdateAsync(id, request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [Audited(AuditActions.EnvironmentDeleted)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await appEnvironmentService.DeleteAsync(id);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
