using Pointer.Application.DTOs.Auth;
using Pointer.Domain.Entity;

namespace Pointer.Application.Common;

/// <summary>
/// Shared mapping of a <see cref="User"/> entity to the <see cref="MeResponse"/> DTO.
/// Used by AuthService (login/me) and DemoService (upgrade) to avoid duplication.
/// </summary>
public static class UserMapper
{
    /// <summary>
    /// DB-11f. The role a session carries: its membership's role when there is a membership (never
    /// the identity's — a membership whose Role is not loaded yields no role); without one, the
    /// identity's own role ONLY for a super admin (users.role_id is the platform role).
    /// </summary>
    public static Role? SessionRole(User identity, WorkspaceMembership? membership) =>
        membership is not null
            ? membership.Role
            : (identity.Role is { IsSuperAdmin: true } platform ? platform : null);

    /// <param name="role">
    /// DB-11f: the caller's role for THIS session — resolve it with <see cref="SessionRole"/>, never
    /// read user.Role/RoleId directly.
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
            RoleId = role?.Id ?? 0,
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
