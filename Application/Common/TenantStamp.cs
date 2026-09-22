using Pointer.Application.Abstractions;

namespace Pointer.Application.Common;

/// <summary>
/// Write-side helper: provides the OwnerId value to stamp on new rows.
/// Super-admin rows are stamped null (global); all other users stamp their TenantId.
/// </summary>
public static class TenantStamp
{
    public static Guid? OwnerFor(ICurrentUser u) => u.IsSuperAdmin ? null : u.TenantId;

    /// <summary>S-14 (DB-11a). True with the caller's workspace for a tenant user; false for a
    /// non-super-admin without a tenant claim — callers return Forbidden, never mint a tenant from a
    /// user id. Super admins: false as well (they own nothing) unless the site documents a global-row case.</summary>
    public static bool TryRequireOwner(ICurrentUser u, out Guid owner)
    {
        owner = u.TenantId ?? Guid.Empty;
        return !u.IsSuperAdmin && u.TenantId is not null;
    }
}
