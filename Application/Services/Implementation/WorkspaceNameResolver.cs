using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// One shared lookup + fallback rule for naming a workspace inside a tenant-related email (invite,
/// approval/rejection, suggestion notification, password reset/changed, demo-ready). Every sender
/// resolves ONCE per send (never per line) and treats a <c>null</c> result as "omit the workspace
/// mention" rather than rendering the DB-03 boot-time placeholder ("Workspace") back at the user —
/// e.g. never "join the Workspace workspace".
/// </summary>
public static class WorkspaceNameResolver
{
    /// <summary>
    /// Returns the workspace's own name, or <c>null</c> when there is no workspace to name (a global/
    /// super-admin send with no ownerId), the row can't be found, or the name is still the placeholder.
    /// IgnoreQueryFilters mirrors the existing anonymous/system-actor lookups this repo already uses
    /// for the same table (InviteService.GetPreviewAsync, AuthService.ResolveTenantNameAsync): the
    /// caller here is always a system/admin action already scoped to this one ownerId, not an ambient
    /// list query, so bypassing the tenant filter is safe and necessary (the acting user may not be
    /// the tenant whose workspace is being named, e.g. a super-admin approving a user).
    /// </summary>
    public static async Task<string?> ResolveForEmailAsync(IUnitOfWork unitOfWork, Guid? ownerId)
    {
        if (ownerId is not Guid id)
            return null;

        var name = await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => w.Name)
            .FirstOrDefaultAsync();

        return string.IsNullOrEmpty(name) || name == Workspace.PlaceholderName ? null : name;
    }
}
