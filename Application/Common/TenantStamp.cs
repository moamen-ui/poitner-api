using Pointer.Application.Abstractions;

namespace Pointer.Application.Common;

/// <summary>
/// Write-side helper: provides the OwnerId value to stamp on new rows.
/// Super-admin rows are stamped null (global); all other users stamp their TenantId.
/// </summary>
public static class TenantStamp
{
    public static Guid? OwnerFor(ICurrentUser u) => u.IsSuperAdmin ? null : u.TenantId;
}
