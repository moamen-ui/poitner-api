using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.AiRule;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/ai-rules")]
[Produces("application/json")]
[Tags("AiRules")]
[Authorize(Policy = Policies.Admin)]
public class AiRulesController(IAiRuleService service) : ControllerBase
{
    [HttpGet("tenant")]
    [ProducesResponseType(typeof(List<AiRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListTenantRules()
    {
        var result = await service.ListTenantAdminRulesAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("project/{projectId:int}")]
    [ProducesResponseType(typeof(List<AiRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListProjectRules(int projectId)
    {
        var result = await service.ListProjectAdminRulesAsync(projectId);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.AiRuleCreated)]
    [ProducesResponseType(typeof(AiRuleResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateAiRuleRequest request)
    {
        var result = await service.CreateAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPut("{id:int}")]
    [Audited(AuditActions.AiRuleUpdated)]
    [ProducesResponseType(typeof(AiRuleResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAiRuleRequest request)
    {
        var result = await service.UpdateAsync(id, request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [Audited(AuditActions.AiRuleDeleted)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await service.DeleteAsync(id);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("insights")]
    [ProducesResponseType(typeof(AiInsightsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInsights([FromQuery] Guid? tenantId = null, [FromQuery] bool includeDetails = false)
    {
        var result = await service.GetInsightsAsync(tenantId, includeDetails);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("all")]
    [ProducesResponseType(typeof(List<AiRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAll([FromQuery] Guid? tenantId = null, [FromQuery] int? projectId = null)
    {
        var result = await service.ListAllRulesAsync(tenantId, projectId);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
