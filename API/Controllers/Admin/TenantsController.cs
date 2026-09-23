using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Impersonation;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/tenants")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Tenants")]
[Produces("application/json")]
public class TenantsController(
    ITenantService tenantService,
    ITenantInviteService tenantInvites,
    IImpersonationService impersonationService
) : ControllerBase
{
    // ── Workspace invitations — the primary way to onboard a tenant ──────────────────────────────
    // The invitee sets their own password from the emailed link, so nobody ever chooses or
    // transmits someone else's credential. POST /api/admin/tenants (below) remains as the
    // secondary, direct path for seeding and air-gapped installs.

    [HttpPost("invites")]
    [Audited(AuditActions.TenantInviteCreated)]
    [ProducesResponseType(typeof(TenantInviteResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateInvite([FromBody] CreateTenantInviteRequest request)
    {
        var result = await tenantInvites.CreateAsync(request);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("invites")]
    [ProducesResponseType(typeof(List<TenantInviteResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListInvites()
    {
        var result = await tenantInvites.ListAsync();
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("invites/{id:int}/resend")]
    [Audited(AuditActions.TenantInviteResent)]
    [ProducesResponseType(typeof(TenantInviteResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResendInvite(int id, [FromQuery] bool rotate = false)
    {
        var result = await tenantInvites.ResendAsync(id, rotate);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("invites/{id:int}")]
    [Audited(AuditActions.TenantInviteRevoked)]
    // Revoke returns a payload-less Result — annotating a body type here would generate a client
    // method typed to a TenantInviteResponse the server never sends.
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RevokeInvite(int id)
    {
        var result = await tenantInvites.RevokeAsync(id);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<TenantResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List()
    {
        var result = await tenantService.ListAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.TenantCreated)]
    [ProducesResponseType(typeof(TenantResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request)
    {
        var result = await tenantService.CreateAsync(request);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // F9 (DB-11a cross-review): keyed on the workspace id, not an admin's `users.id` — the previous
    // "exactly one admin membership" resolution 404'd for any identity administering more than one
    // workspace (D13, this release's headline capability). Route gains an explicit "/status"
    // segment because the workspace-scoped routes below all key on the same {workspaceId:guid}.
    [HttpPatch("{workspaceId:guid}/status")]
    [Audited(AuditActions.TenantStatusChanged)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetStatus(
        Guid workspaceId,
        [FromBody] SetTenantStatusRequest request
    )
    {
        var result = await tenantService.SetStatusAsync(workspaceId, request.Action);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // DB-17 §3.3 (F9 precedent): workspace-keyed demo routes (the {id:int} pair was removed by DB-11e).
    [HttpPost("{workspaceId:guid}/extend")]
    [Audited(AuditActions.TenantDemoExtended)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExtendDemo(Guid workspaceId)
    {
        var result = await tenantService.ExtendDemoAsync(workspaceId);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{workspaceId:guid}/demo-config")]
    [Audited(AuditActions.TenantDemoConfigChanged)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetDemoConfig(
        Guid workspaceId,
        [FromBody] SetDemoConfigRequest request
    )
    {
        var result = await tenantService.SetDemoConfigAsync(
            workspaceId,
            request.CommentCapOverride,
            request.TtlHoursOverride
        );
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // F9 (DB-11a cross-review): keyed on the workspace id, not an admin's `users.id` (see SetStatus).
    [HttpPatch("{workspaceId:guid}/plan")]
    [Audited(AuditActions.TenantPlanChanged)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePlan(
        Guid workspaceId,
        [FromBody] ChangeTenantPlanRequest request
    )
    {
        var result = await tenantService.ChangePlanAsync(workspaceId, request.PlanId);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // F9 (DB-11a cross-review): keyed on the workspace id directly — `workspaces.id` no longer
    // equals any admin's `public_id`, and it never required resolving through an admin membership
    // in the first place (HardDeleteAsync already took the workspace id).
    [HttpDelete("{workspaceId:guid}")]
    [Audited(AuditActions.TenantHardDeleted)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid workspaceId)
    {
        var result = await tenantService.HardDeleteAsync(workspaceId);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // DB-13 (F2): the metadata-only operator's one way to reach content — an audited, time-boxed,
    // read-only "View as…" session (§3.6).
    [HttpPost("{workspaceId:guid}/impersonate")]
    [Audited(AuditActions.ImpersonationStarted)]
    [ProducesResponseType(typeof(ImpersonationStartResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Impersonate(
        Guid workspaceId,
        [FromBody] StartImpersonationRequest request
    )
    {
        var result = await impersonationService.StartAsync(workspaceId, request);
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}

public class SetTenantStatusRequest
{
    public string Action { get; set; } = string.Empty;
}

public class ChangeTenantPlanRequest
{
    public int PlanId { get; set; }
}

public class SetDemoConfigRequest
{
    /// <summary>Per-tenant demo comment cap. Null clears the override (use the global default).</summary>
    public int? CommentCapOverride { get; set; }

    /// <summary>Per-tenant demo TTL (hours) used on extend. Null clears the override (use the global default).</summary>
    public int? TtlHoursOverride { get; set; }
}
