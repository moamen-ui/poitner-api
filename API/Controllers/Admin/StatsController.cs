using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/stats")]
[Authorize(Policy = Policies.Admin)]
public class StatsController(IStatsService statsService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(StatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var result = await statsService.GetAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
