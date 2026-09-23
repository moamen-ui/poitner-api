using Pointer.Application.DTOs.Auth;
using Pointer.Domain.Entity;

namespace Pointer.Application.Common;

/// <summary>
/// Shared mapping of a <see cref="User"/> entity to the <see cref="MeResponse"/> DTO.
/// Used by AuthService (login/me) and DemoService (upgrade) to avoid duplication.
/// </summary>
public static class UserMapper
{
    /// <param name="role">
    /// DB-11a: the caller's role for THIS session — the membership's role for a tenant user, or the
    /// identity's own (legacy) Role for a super admin with no membership. Callers resolve this once
    /// (membership?.Role ?? user.Role) rather than this mapper reading user.Role directly.
    /// </param>
    /// <param name="workspace">DB-17: the caller's CURRENT workspace (loaded IgnoreQueryFilters by
    /// the tenant id already in hand), when the caller holds one — login "ok", /me, switch, upgrade.
    /// Null for super admins and any other caller that has no current workspace in hand.</param>
    public static MeResponse ToMeResponse(
        User user,
        Role? role,
        string? tenantName = null,
        Workspace? workspace = null
    )
    {
        var isAdmin = role?.GrantsAdmin ?? false;
        var isSuperAdmin = role?.IsSuperAdmin ?? false;
        // DB-14 §3.5: an identity is treated as verified when it either proved control of its
        // address, or is exempt from the gate entirely (demo, passwordless, super admin).
        var emailVerified =
            user.EmailVerifiedAt != null || user.IsDemo || isSuperAdmin || user.PasswordlessOnly;
        return new MeResponse
        {
            Id = user.PublicId,
            Email = user.Email,
            DisplayName = user.DisplayName,
            RoleId = role?.Id ?? user.RoleId,
            RoleName = role?.Name ?? string.Empty,
            IsAdmin = isAdmin,
            IsSuperAdmin = isSuperAdmin,
            IsQuickAccess = role?.QuickAccess ?? false,
            Language = user.Language,
            Theme = user.Theme,
            AddCommentShortcut = user.AddCommentShortcut,
            TenantName = tenantName,
            EmailVerified = emailVerified,
            // The banner is loud only for people the gate actually blocks (an admin-tier role);
            // stakeholders get a soft hint instead (MeResponse's own doc-comment, §3.5).
            EmailVerificationRequired = !emailVerified && isAdmin,
            MfaEnabled = user.TotpEnabledAt != null,
            // DB-17 §3.6: non-null = a live demo; CanExtend requires it not yet extended or converted.
            DemoExpiresAt = workspace?.DemoExpiresAt,
            DemoCanExtend =
                workspace?.DemoExpiresAt != null
                && workspace.DemoExtendedAt == null
                && workspace.DemoConvertedAt == null,
            // DB-18: the current workspace's freeze state (null/false for super admins — no workspace).
            WorkspacePausedAt = workspace?.PausedAt,
            WorkspacePausedByOperator = workspace?.PausedByOperator ?? false,
            WorkspaceDeletionScheduledFor = workspace?.DeletionScheduledFor,
        };
    }
}
