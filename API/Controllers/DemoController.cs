using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/demo")]
[Tags("Demo")]
public class DemoController(IDemoService demoService, IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("demo")]
    [HttpPost]
    [ProducesResponseType(typeof(Result<DemoSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result<DemoSessionResponse>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Create([FromBody] DemoRequest request)
    {
        var serverUrl = configuration["Pointer:PublicUrl"]
            ?? $"{Request.Scheme}://{Request.Host}";

        var result = await demoService.ProvisionAsync(serverUrl, request?.Email ?? string.Empty);

        // Capacity / per-email daily limit → 429; other failures (e.g. invalid email) → 400.
        if (!result.IsSuccess && result.Message != null
            && (result.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("demo limit", StringComparison.OrdinalIgnoreCase)))
            return StatusCode(StatusCodes.Status429TooManyRequests, result);

        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
