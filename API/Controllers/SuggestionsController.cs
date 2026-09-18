using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Stakeholder-facing: suggest a predefined action on a project the caller cannot edit.
/// [Authorize] (any tenant member). Admins/owners are rejected with guidance (they add directly).
/// </summary>
[ApiController]
[Authorize]
[Produces("application/json")]
[Tags("Suggestions")]
public class SuggestionsController(ISuggestionService service) : ControllerBase
{
    [HttpPost("api/projects/{id:int}/predefined-action-suggestions")]
    [ProducesResponseType(typeof(SuggestionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Suggest(int id, [FromBody] CreateSuggestionRequest request)
    {
        var result = await service.SuggestAsync(id, request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("api/me/predefined-action-suggestions")]
    [ProducesResponseType(typeof(List<SuggestionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine()
    {
        var result = await service.ListMineAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPut("api/predefined-action-suggestions/{id:int}")]
    [ProducesResponseType(typeof(SuggestionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSuggestionRequest request)
    {
        var result = await service.UpdateAsync(id, request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
