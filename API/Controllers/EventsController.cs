using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Event;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
public class EventsController(IUsageEventService usageEventService) : ControllerBase
{
    [HttpPost("api/events")]
    [Authorize]
    [EnableRateLimiting("events")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(Result<bool>), 400)]
    [ProducesResponseType(typeof(Result<bool>), 404)]
    [Produces("application/json")]
    public async Task<IActionResult> RecordEvent([FromBody] RecordEventRequest request)
    {
        var result = await usageEventService.RecordEventAsync(request.Type, "cli", request.ProjectKey, request.Meta);
        if (!result.IsSuccess)
        {
            if (result.IsNotFound) return NotFound(result);
            return BadRequest(result);
        }
        return NoContent();
    }

    [HttpGet("api/admin/events/summary")]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType(typeof(EventsSummaryResponse), 200)]
    [Produces("application/json")]
    public async Task<IActionResult> GetSummary([FromQuery] int projectId)
    {
        var result = await usageEventService.GetSummaryAsync(projectId);
        return Ok(result);
    }
}
