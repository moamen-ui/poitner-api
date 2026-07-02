using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/tenants")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Tenants")]
public class TenantsController(ITenantService tenantService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(Result<List<TenantResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await tenantService.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(Result<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request)
    {
        var result = await tenantService.CreateAsync(request);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetStatus(int id, [FromBody] SetTenantStatusRequest request)
    {
        var result = await tenantService.SetStatusAsync(id, request.Action);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:int}/extend")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExtendDemo(int id)
    {
        var result = await tenantService.ExtendDemoAsync(id);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}/demo-config")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDemoConfig(int id, [FromBody] SetDemoConfigRequest request)
    {
        var result = await tenantService.SetDemoConfigAsync(id, request.CommentCapOverride, request.TtlHoursOverride);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}/plan")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePlan(int id, [FromBody] ChangeTenantPlanRequest request)
    {
        var result = await tenantService.ChangePlanAsync(id, request.PlanId);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        // Resolve int id → PublicId via a lightweight ListAsync is expensive; instead load the user directly.
        // We need to resolve the PublicId from the int id. Use ITenantService to resolve.
        // Since we don't want to expose a separate resolve method, we do it via ListAsync first to get PublicId.
        // Actually, let's add the convenience: ListAsync returns TenantResponse which has PublicId.
        // But that's too heavy. Better: add HardDeleteByIdAsync overload OR resolve here via the repo.
        // Per brief: "have the controller resolve the int id → PublicId → call HardDeleteAsync(publicId)".
        // We'll call SetStatusAsync to verify existence (it already does), then look up PublicId.
        // Simpler: call ListAsync, find by id. But expensive. Let's add a private resolve approach:
        // The brief says controller resolves it — use ITenantService.ListAsync to get PublicId.
        var listResult = await tenantService.ListAsync();
        if (!listResult.IsSuccess)
            return BadRequest(listResult);

        var tenant = listResult.Data?.FirstOrDefault(t => t.Id == id);
        if (tenant == null)
            return NotFound(Result.NotFound("Tenant not found."));

        var result = await tenantService.HardDeleteAsync(tenant.PublicId);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}

public class SetTenantStatusRequest
{
    public string Action { get; set; } = string.Empty;
}

public class ChangeTenantPlanRequest
{
    public int PlanId { get; set; }
}

public class SetDemoConfigRequest
{
    /// <summary>Per-tenant demo comment cap. Null clears the override (use the global default).</summary>
    public int? CommentCapOverride { get; set; }

    /// <summary>Per-tenant demo TTL (hours) used on extend. Null clears the override (use the global default).</summary>
    public int? TtlHoursOverride { get; set; }
}
