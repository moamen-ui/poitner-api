using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Stats;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/stats")]
[Authorize(Policy = Policies.Admin)]
public class StatsController(
    IStatsService statsService,
    IPlatformInsightsService platformInsightsService,
    IActivationStatsService activationStatsService
) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(StatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var result = await statsService.GetAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Cross-tenant usage insights — super admins only (the [Authorize] above is the
    /// class-level Admin policy; this action overrides it with the stricter SuperAdmin one).</summary>
    [HttpGet("insights")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [ProducesResponseType(typeof(PlatformInsightsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInsights()
    {
        var result = await platformInsightsService.GetPlatformInsightsAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Own-tenant usage insights — any workspace admin (the class-level Admin policy).</summary>
    [HttpGet("workspace-insights")]
    [ProducesResponseType(typeof(WorkspaceInsightsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkspaceInsights()
    {
        var result = await platformInsightsService.GetWorkspaceInsightsAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>DB-15: the activation funnel (demo → converted → widget installed → first comment
    /// → first apply) across every workspace, plus the weekly activated series — super admins
    /// only. Metadata (counts), so it works without an impersonation session (DB-13).</summary>
    [HttpGet("funnel")]
    [Authorize(Policy = Policies.SuperAdmin)]
    [ProducesResponseType(typeof(ActivationFunnelResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFunnel([FromQuery] int weeks = 12)
    {
        var result = await activationStatsService.GetFunnelAsync(weeks);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>DB-15: the caller's own workspace "getting started" checklist — any workspace
    /// admin (the class-level Admin policy); a super admin uses /funnel instead.</summary>
    [HttpGet("activation")]
    [ProducesResponseType(typeof(WorkspaceActivationResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivation()
    {
        var result = await activationStatsService.GetActivationAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
