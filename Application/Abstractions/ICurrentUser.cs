namespace Pointer.Application.Abstractions;

public interface ICurrentUser
{
    Guid? Id { get; }
    bool IsAdmin { get; }
    bool IsSuperAdmin { get; }
    bool IsQuickAccess { get; }
    Guid? TenantId { get; }

    /// <summary>The caller's Role.Id (from the JWT's existing "role_id" claim). Null when there is
    /// no authenticated user at all — never null for a real authenticated caller, since every User
    /// has a RoleId.</summary>
    int? RoleId { get; }
}
