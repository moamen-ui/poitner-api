using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/statuses")]
[Tags("Statuses")]
[Authorize(Policy = Policies.Admin)]
public class StatusesController(IStatusAdminService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<StatusAdminItem>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await service.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{value:int}")]
    [ProducesResponseType(typeof(StatusAdminItem), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int value, [FromBody] UpdateStatusPresentationRequest request)
    {
        var result = await service.UpsertAsync(value, request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{value:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reset(int value)
    {
        var result = await service.ResetAsync(value);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
