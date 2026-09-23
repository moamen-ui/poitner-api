using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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
    /// <summary>Generates and stores a fresh (unverified) TOTP secret. Requires the caller's current
    /// password (review fix #5). 403 if the caller is not the super admin or holds an API-key
    /// session; 409 if MFA is already enabled; a plain failure for a wrong password or a passwordless
    /// account.</summary>
    [NoAudit(
        "secret generated but not yet enabled — POST verify writes auth.mfa.enrolled once the code checks out"
    )]
    [HttpPost("enrol")]
    [ProducesResponseType(typeof(MfaEnrolResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Enrol([FromBody] MfaEnrolRequest request)
    {
        var result = await mfaService.EnrolAsync(request);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict)
            return Conflict(result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Validates the pending secret's code; on success enables MFA and returns 8 recovery
    /// codes (shown once). Review fix #5: shares the login rate-limit policy and the per-e-mail
    /// lockout budget on a wrong code (same as POST /api/auth/login).</summary>
    [Audited(AuditActions.AuthMfaEnrolled)]
    [HttpPost("verify")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(MfaVerifyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(Result<MfaVerifyResponse>), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Verify([FromBody] MfaCodeRequest request)
    {
        var result = await mfaService.VerifyAsync(request);
        if (result.IsLocked)
        {
            if (result.RetryAfterSeconds.HasValue)
                Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, result);
        }
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict)
            return Conflict(result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Validates the current password AND a current TOTP or recovery code (review fix #5),
    /// then clears the secret and every recovery-code row. Shares the login rate-limit policy and
    /// per-e-mail lockout budget on a wrong attempt.</summary>
    [Audited(AuditActions.AuthMfaDisabled)]
    [HttpPost("disable")]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Disable([FromBody] MfaCodeRequest request)
    {
        var result = await mfaService.DisableAsync(request);
        if (result.IsLocked)
        {
            if (result.RetryAfterSeconds.HasValue)
                Response.Headers.RetryAfter = result.RetryAfterSeconds.Value.ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, result);
        }
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
