using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Profile;
using Pointer.Application.DTOs.User;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Enums;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = Policies.Admin)]
public class UsersController(IUserService userService, IProfileService profileService)
    : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(List<UserResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] string? status = null)
    {
        ApprovalStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var parsed))
                return BadRequest();
            filter = parsed;
        }

        var result = await userService.ListAsync(filter);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:int}/approve")]
    [Audited(AuditActions.MemberApproved)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Approve(int id, [FromBody] ApproveUserRequest request)
    {
        var result = await userService.ApproveAsync(id, request);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{id:int}/reject")]
    [Audited(AuditActions.MemberRejected)]
    // DB-18 (Opus LOW): rejecting a pending applicant removes access — always allowed while frozen,
    // like Delete/promote-adjacent revocations.
    [AllowWhenWorkspacePaused]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reject(int id)
    {
        var result = await userService.RejectAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost]
    [Audited(AuditActions.MemberCreated)]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        var result = await userService.CreateAsync(request);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPatch("{id:int}")]
    [Audited(AuditActions.MemberUpdated)]
    // DB-18 (Opus HIGH 2): a freeze must never stop an admin from locking someone out — this route
    // can also GRANT (Password/IsActive=true/an admin-tier RoleId), so UserService.UpdateAsync
    // itself refuses those specific fields while frozen (§3.5).
    [AllowWhenWorkspacePaused]
    [ProducesResponseType(typeof(UserResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        var result = await userService.UpdateAsync(id, request);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        // DB-18 (Gemini HIGH): UserService.UpdateAsync returns Forbidden while the workspace is
        // frozen and the request would grant access (Password/IsActive=true/an admin-tier role) —
        // this fell through to a generic 400 before, which the dashboard cannot distinguish from an
        // ordinary validation failure.
        if (result.IsForbidden)
            return StatusCode(StatusCodes.Status403Forbidden, result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpGet("{id:int}/profile")]
    [ProducesResponseType(
        typeof(Pointer.Application.DTOs.Profile.UserProfileResponse),
        StatusCodes.Status200OK
    )]
    public async Task<IActionResult> Profile(int id)
    {
        var result = await profileService.GetByIdAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpDelete("{id:int}")]
    [Audited(AuditActions.MemberRemoved)]
    // DB-18: member removal is access-removing — always allowed while frozen.
    [AllowWhenWorkspacePaused]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await userService.DeleteAsync(id);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("{deputyPublicId:guid}/promote")]
    [Audited(AuditActions.OwnershipTransferred)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Promote(Guid deputyPublicId)
    {
        var result = await userService.TransferOwnershipAsync(deputyPublicId);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }
}
