using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.API.Extensions;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/demo")]
[Tags("Demo")]
public class DemoController(
    IDemoService demoService,
    IConfiguration configuration,
    ICurrentUser currentUser
) : ControllerBase
{
    [AllowAnonymous]
    [Audited(AuditActions.AuthDemoProvisioned)]
    [EnableRateLimiting("demo")]
    [HttpPost]
    [ProducesResponseType(typeof(Result<DemoSessionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(
        typeof(Result<DemoSessionResponse>),
        StatusCodes.Status429TooManyRequests
    )]
    public async Task<IActionResult> Create([FromBody] DemoRequest request)
    {
        var serverUrl = configuration["Pointer:PublicUrl"] ?? $"{Request.Scheme}://{Request.Host}";

        var result = await demoService.ProvisionAsync(serverUrl, request?.Email ?? string.Empty);

        // Capacity / per-email daily limit → 429; other failures (e.g. invalid email) → 400.
        if (
            !result.IsSuccess
            && result.Message != null
            && (
                result.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
                || result.Message.Contains("demo limit", StringComparison.OrdinalIgnoreCase)
            )
        )
            return StatusCode(StatusCodes.Status429TooManyRequests, result);

        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>DB-17 §3.4: converts the demo in the CALLER'S SESSION workspace (R16) — never "the
    /// one demo the identity belongs to" (an identity may administer more than one demo).</summary>
    [Authorize]
    [Audited(AuditActions.AuthDemoUpgraded)]
    [HttpPost("upgrade")]
    [ProducesResponseType(typeof(UpgradeDemoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Upgrade([FromBody] UpgradeDemoRequest request)
    {
        if (!TenantStamp.TryRequireOwner(currentUser, out var ws))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                Result.Forbidden(MessageKeys.Common.Forbidden)
            );

        var callerId = User.GetId();
        var result = await demoService.UpgradeAsync(callerId, ws, request);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict)
            return Conflict(result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>DB-17 §3.6: the demo admin's one-time extension (+TTL hours) of the CURRENT
    /// workspace. Operator extensions live under /api/admin/tenants.</summary>
    [Authorize(Policy = Policies.Admin)]
    [Audited(AuditActions.DemoExtended)]
    [HttpPost("extend")]
    [ProducesResponseType(typeof(DemoStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Extend()
    {
        if (!TenantStamp.TryRequireOwner(currentUser, out var ws))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                Result.Forbidden(MessageKeys.Common.Forbidden)
            );

        var result = await demoService.ExtendAsync(User.GetId(), ws);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
