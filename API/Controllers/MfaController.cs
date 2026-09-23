using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Mfa;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// R5-61 §3.2 — operator (super-admin-only) TOTP MFA: enrol, verify (completes enrollment), disable.
/// Routed under <c>api/me/mfa</c> and tagged <c>"Me"</c> (not the controller-name default "Mfa",
/// which is not in <c>orval.config.ts</c> <c>filters.tags</c> and would silently generate nothing
/// for the dashboard — reusing "Me" needs no client-generation change since it's already listed and
/// every route here lives under <c>api/me/*</c>).
/// </summary>
[ApiController]
[Route("api/me/mfa")]
[Authorize]
[Tags("Me")]
public class MfaController(IMfaService mfaService) : ControllerBase
{
    /// <summary>Generates and stores a fresh (unverified) TOTP secret. 403 if the caller is not the
    /// super admin; 409 if MFA is already enabled.</summary>
    [NoAudit("secret generated but not yet enabled — POST verify writes auth.mfa.enrolled once the code checks out")]
    [HttpPost("enrol")]
    [ProducesResponseType(typeof(MfaEnrolResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Enrol()
    {
        var result = await mfaService.EnrolAsync();
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Validates the pending secret's code; on success enables MFA and returns 8 recovery
    /// codes (shown once).</summary>
    [Audited(AuditActions.AuthMfaEnrolled)]
    [HttpPost("verify")]
    [ProducesResponseType(typeof(MfaVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Verify([FromBody] MfaCodeRequest request)
    {
        var result = await mfaService.VerifyAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Validates a current TOTP or recovery code, then clears the secret and every
    /// recovery-code row.</summary>
    [Audited(AuditActions.AuthMfaDisabled)]
    [HttpPost("disable")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Disable([FromBody] MfaCodeRequest request)
    {
        var result = await mfaService.DisableAsync(request);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
