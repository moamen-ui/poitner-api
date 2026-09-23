using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Event;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Tags("Events")]
public class EventsController(IUsageEventService usageEventService) : ControllerBase
{
    [NoAudit("analytics beacon")]
    [HttpPost("api/events")]
    [Authorize]
    [EnableRateLimiting("events")]
    [ProducesResponseType(204)]
    [ProducesResponseType(typeof(Result<bool>), 400)]
    [ProducesResponseType(typeof(Result<bool>), 404)]
    [Produces("application/json")]
    public async Task<IActionResult> RecordEvent([FromBody] RecordEventRequest request)
    {
        var source = string.IsNullOrWhiteSpace(request.Source) ? "cli" : request.Source;
        var result = await usageEventService.RecordEventAsync(request.Type, source, request.ProjectKey, request.Meta);
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
