using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Auth;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.DTOs.Notification;
using Pointer.Application.DTOs.Preferences;
using Pointer.Application.DTOs.User;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
[Produces("application/json")]
public class MeController(
    IPreferencesService preferencesService,
    IProfileService profileService,
    IAuthService authService,
    INotificationService notificationService,
    IUserService users,
    IIdentityEraseService erase,
    Pointer.Application.Abstractions.ICurrentUser currentUser) : ControllerBase
{
    [Audited(AuditActions.AuthPasswordChanged)]
    [HttpPost("change-password")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        var result = await authService.ChangePasswordAsync(request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Starts changing the caller's e-mail: password check, confirmation link to the new address, notice to the old one. Nothing changes until the link is confirmed.</summary>
    [Audited(AuditActions.AuthEmailChangeRequested)]
    [HttpPost("change-email")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest request)
    {
        var result = await authService.RequestEmailChangeAsync(request);
        if (result.IsNotFound) return NotFound(result);
        if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
        if (result.IsConflict) return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("personal UI preference")]
    [HttpPatch("preferences")]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdatePreferences([FromBody] UpdatePreferencesRequest request)
    {
        var result = await preferencesService.UpdateAsync(request);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("read of the caller's own profile")]
    [HttpGet("profile")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.UserProfileResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile()
    {
        if (currentUser.Id is null) return Unauthorized();
        var result = await profileService.GetByPublicIdAsync(currentUser.Id.Value);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("read, not a mutation (creation-on-first-view is audited by apikey.created)")]
    [HttpGet("api-key")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.ApiKeyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetApiKey()
    {
        if (currentUser.Id is null) return Unauthorized();
        var result = await profileService.GetOrCreateApiKeyAsync(currentUser.Id.Value, currentUser.TenantId);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [Audited(AuditActions.ApikeyRegenerated)]
    [HttpPost("api-key/regenerate")]
    [ProducesResponseType(typeof(Pointer.Application.DTOs.Profile.ApiKeyResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RegenerateApiKey()
    {
        if (currentUser.Id is null) return Unauthorized();
        var result = await profileService.RegenerateApiKeyAsync(currentUser.Id.Value, currentUser.TenantId);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("inbox read")]
    [HttpGet("notifications")]
    [ProducesResponseType(typeof(Pointer.Application.Response.PagedData<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNotifications([FromQuery] bool? unread = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await notificationService.ListAsync(unread, page, pageSize);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("inbox read")]
    [HttpGet("notifications/unread-count")]
    [ProducesResponseType(typeof(UnreadCountResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUnreadCount()
    {
        var result = await notificationService.GetUnreadCountAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("inbox state")]
    [HttpPatch("notifications/{id:int}/read")]
    [ProducesResponseType(typeof(NotificationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkRead(int id)
    {
        var result = await notificationService.MarkReadAsync(id);
        if (result.IsNotFound) return NotFound(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [NoAudit("inbox state")]
    [HttpPost("notifications/read-all")]
    [ProducesResponseType(typeof(ReadAllNotificationsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkAllRead()
    {
        var result = await notificationService.MarkAllReadAsync();
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>Leaves the current workspace. Blocked while the caller is its only Workspace Admin.</summary>
    [Audited(AuditActions.MemberLeft)]
    [HttpPost("leave-workspace")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> LeaveWorkspace()
    {
        var r = await users.LeaveWorkspaceAsync();
        if (r.IsConflict) return Conflict(r);
        return r.IsSuccess ? Ok(r) : BadRequest(r);
    }

    /// <summary>Deletes the caller's account everywhere (GDPR erase). Password required. Comments stay, attributed to "Deleted user".</summary>
    [Audited(AuditActions.IdentityErased)]
    [HttpDelete]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMyAccount([FromBody] DeleteMyAccountRequest request)
    {
        var r = await erase.EraseSelfAsync(request);
        if (r.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, r);
        if (r.IsConflict) return Conflict(r);
        return r.IsSuccess ? Ok(r) : BadRequest(r);
    }

    /// <summary>Magic-link accounts only: e-mails a one-time link that confirms deleting the account (30 min). Password accounts use DELETE /api/me.</summary>
    [Audited(AuditActions.IdentityEraseRequested)]
    [HttpPost("request-erase")]
    [EnableRateLimiting("signup")]
    [ProducesResponseType(typeof(Result), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RequestErase()
    {
        var r = await erase.RequestEraseLinkAsync();
        if (r.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, r);
        return r.IsSuccess ? Ok(r) : BadRequest(r);
    }
}
