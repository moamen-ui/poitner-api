using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Plan;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Anonymous marketing endpoint consumed by the landing page. Returns marketing fields only
/// (no entitlement values / ids), DisplayState != Hidden, ordered by SortOrder. Open CORS applies
/// (not under /api/admin/*). Light rate-limit since the landing fetches it on every page load.
/// </summary>
[ApiController]
[Route("api/plans")]
[AllowAnonymous]
[Tags("Plans")]
[EnableRateLimiting("plans")]
public class PlansPublicController(IPlanService planService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(Result<List<PlanPublicResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await planService.ListPublicAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
