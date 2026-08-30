namespace Pointer.Application.Abstractions;

public interface ICurrentUser
{
    Guid? Id { get; }
    bool IsAdmin { get; }
    bool IsSuperAdmin { get; }
    bool IsQuickAccess { get; }
    Guid? TenantId { get; }
}
