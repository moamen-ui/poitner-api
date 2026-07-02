using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Plan;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/plans")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Plans")]
public class PlansController(IPlanService planService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(Result<List<PlanAdminResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await planService.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(Result<PlanAdminResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] PlanWriteDto request)
    {
        var result = await planService.CreateAsync(request);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(Result<PlanAdminResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] PlanWriteDto request)
    {
        var result = await planService.UpdateAsync(id, request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await planService.DeleteAsync(id);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
