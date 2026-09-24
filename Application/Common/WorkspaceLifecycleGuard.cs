using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Common;

/// <summary>
/// DB-18 §3.4. The shared "may this caller pause/delete this workspace" check, factored out so
/// <c>WorkspaceService.GetAsync</c> can report <c>WorkspaceResponse.CanManageLifecycle</c> without
/// side effects, using exactly the same rule <c>WorkspaceLifecycleService.RequireLifecycleAdminAsync</c>
/// enforces (Deputy excluded — owner wording "workspace admin"; a live, unconverted demo cannot be
/// managed either).
/// </summary>
public static class WorkspaceLifecycleGuard
{
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    public static async Task<bool> CanManageAsync(
        ICurrentUser currentUser,
        IMembershipService memberships,
        Workspace workspace
    )
    {
        if (currentUser.KeyScopes != null)
            return false;
        // Gemini NIT: IsSuperAdmin is already refused just above, so TenantStamp.TryRequireOwner's
        // own !IsSuperAdmin check is redundant here — a plain TenantId comparison is exactly
        // equivalent (a super admin's TenantId is never a real workspace's Id anyway) and simpler.
        if (currentUser.IsQuickAccess || currentUser.IsSuperAdmin || currentUser.IsImpersonating)
            return false;
        if (currentUser.TenantId != workspace.Id)
            return false;
        if (currentUser.Id is not Guid publicId)
            return false;

        var identity = await memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return false;

        var membership = await memberships.GetMembershipAsync(identity.Id, workspace.Id);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role?.Name != WorkspaceAdminRoleName
        )
            return false;

        // D18.9 (Opus LOW): a converted workspace still inside the 72h re-verify window carries a
        // TTL but is a real workspace — only a LIVE, UNCONVERTED demo is blocked.
        if (workspace.DemoExpiresAt != null && workspace.DemoConvertedAt == null)
            return false;

        return true;
    }
}
