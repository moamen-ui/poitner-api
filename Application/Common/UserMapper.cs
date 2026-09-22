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
    public static MeResponse ToMeResponse(User user, Role? role, string? tenantName = null) => new()
    {
        Id = user.PublicId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        RoleId = role?.Id ?? user.RoleId,
        RoleName = role?.Name ?? string.Empty,
        IsAdmin = role?.GrantsAdmin ?? false,
        IsSuperAdmin = role?.IsSuperAdmin ?? false,
        IsQuickAccess = role?.QuickAccess ?? false,
        Language = user.Language,
        Theme = user.Theme,
        AddCommentShortcut = user.AddCommentShortcut,
        TenantName = tenantName,
    };
}
