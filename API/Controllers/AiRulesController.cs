using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.AiRule;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/ai-rules")]
[Produces("application/json")]
[Tags("AiRules")]
[Authorize]
public class AiRulesController(IAiRuleService service) : ControllerBase
{
    [HttpGet("project/{key}")]
    [ProducesResponseType(typeof(ProjectAiRulesResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProjectRules(string key)
    {
        var result = await service.GetProjectRulesAsync(key);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("my")]
    [ProducesResponseType(typeof(List<AiRuleResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMyRules([FromQuery] int? projectId)
    {
        var result = await service.ListMyRulesAsync(projectId);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("my")]
    [Audited(AuditActions.AiRuleCreated)]
    [ProducesResponseType(typeof(AiRuleResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateMyRule([FromBody] CreateAiRuleRequest request)
    {
        request.IsPersonal = true;
        var result = await service.CreateAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPut("my/{id:int}")]
    [Audited(AuditActions.AiRuleUpdated)]
    [ProducesResponseType(typeof(AiRuleResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMyRule(int id, [FromBody] UpdateAiRuleRequest request)
    {
        var result = await service.UpdateAsync(id, request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("my/{id:int}")]
    [Audited(AuditActions.AiRuleDeleted)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteMyRule(int id)
    {
        var result = await service.DeleteAsync(id);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
