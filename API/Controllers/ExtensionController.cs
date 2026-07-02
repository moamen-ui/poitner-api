using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.DTOs.Extension;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Browser-extension activation (widget-style: authenticated stakeholder scoped to the project's
/// tenant). Enforced-but-inert — the real extension will call this; until then nothing triggers it.
/// </summary>
[ApiController]
[Authorize]
[Tags("Extension")]
public class ExtensionController(IExtensionService extensionService) : ControllerBase
{
    [HttpPost("api/extension/activate")]
    [ProducesResponseType(typeof(Result<ExtensionActivateResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate([FromBody] ExtensionActivateRequest request)
    {
        var result = await extensionService.ActivateAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
