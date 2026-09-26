using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Billing;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// DB-20 §3.9. The workspace's own billing surface — Workspace Admin only (the service itself
/// re-checks <c>WorkspaceLifecycleGuard.CanManageAsync</c> on the caller's current workspace; the
/// policy here only keeps non-admin stakeholders out with a plain 403, same pattern as
/// <see cref="WorkspaceController"/>).
/// </summary>
[ApiController]
[Route("api/admin/billing")]
[Authorize(Policy = Policies.Admin)]
[Tags("Billing")]
[Produces("application/json")]
public class BillingController(IBillingService billing) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(BillingSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary()
    {
        var result = await billing.GetSummaryAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("payments")]
    [ProducesResponseType(typeof(List<WorkspacePaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPayments()
    {
        var result = await billing.GetWorkspacePaymentsAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>The live, requestable plans (with ids) this workspace may quote/request — the
    /// dashboard gap behind <c>GET /api/admin/plans</c> (SuperAdmin-only) and <c>GET /api/plans</c>
    /// (anonymous marketing catalog, no ids).</summary>
    [HttpGet("plans")]
    [NoAudit("read-only catalog listing, no state change")]
    [ProducesResponseType(typeof(List<BillablePlanResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRequestablePlans()
    {
        var result = await billing.ListRequestablePlansAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Price preview — no state change. Call on an explicit "Apply code" click and on plan
    /// change, never per keystroke (rate-limited).</summary>
    [HttpPost("quote")]
    [EnableRateLimiting("danger")]
    [NoAudit("price preview, no state change")]
    [ProducesResponseType(typeof(BillingQuoteResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Quote([FromBody] RequestPlanRequest request)
    {
        var result = await billing.QuoteAsync(request.PlanId, request.ReferenceCode);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("request")]
    [EnableRateLimiting("danger")]
    [Audited(AuditActions.BillingPlanRequested)]
    [ProducesResponseType(typeof(BillingSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestPlan([FromBody] RequestPlanRequest request)
    {
        var result = await billing.RequestPlanAsync(request.PlanId, request.ReferenceCode);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("request")]
    [Audited(AuditActions.BillingRequestCancelled)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelRequest()
    {
        var result = await billing.CancelRequestAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
