using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// Admin review queue for predefined-action suggestions. Admin-gated + tenant-scoped. Approve mints
/// a real project-scoped predefined action; reject records the decision.
/// </summary>
[ApiController]
[Route("api/admin/predefined-action-suggestions")]
[Produces("application/json")]
[Tags("Suggestions")]
[Authorize(Policy = Policies.Admin)]
public class SuggestionsController(ISuggestionService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<SuggestionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListPending()
    {
        var result = await service.ListPendingAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:int}/approve")]
    [ProducesResponseType(typeof(SuggestionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(int id)
    {
        var result = await service.ApproveAsync(id);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:int}/reject")]
    [ProducesResponseType(typeof(SuggestionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(int id)
    {
        var result = await service.RejectAsync(id);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
