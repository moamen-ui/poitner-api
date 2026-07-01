using Pointer.Application.DTOs.Auth;
using Pointer.Domain.Entity;

namespace Pointer.Application.Common;

/// <summary>
/// Shared mapping of a <see cref="User"/> entity to the <see cref="MeResponse"/> DTO.
/// Used by AuthService (login/me) and DemoService (upgrade) to avoid duplication.
/// </summary>
public static class UserMapper
{
    public static MeResponse ToMeResponse(User user) => new()
    {
        Id = user.PublicId,
        Email = user.Email,
        DisplayName = user.DisplayName,
        RoleId = user.RoleId,
        RoleName = user.Role?.Name ?? string.Empty,
        IsAdmin = user.Role?.GrantsAdmin ?? false,
        IsSuperAdmin = user.Role?.IsSuperAdmin ?? false,
        Language = user.Language,
        Theme = user.Theme,
    };
}
