using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Invite;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// The super-admin's workspace-onboarding surface, sitting on top of the general invite machinery.
///
/// It is a separate service rather than extra methods on <see cref="IInviteService"/> for one
/// reason: <c>IInviteService.RevokeAsync</c> loads an invite through a path that bypasses tenant
/// scoping entirely for a super admin, so a Tenants screen calling it directly could revoke a
/// customer's staff or client invitation. Every id-addressed method here re-checks that the invite
/// really is a workspace invite (<c>OwnerId == null</c>) and 404s otherwise.
/// </summary>
public class TenantInviteService(
    IUnitOfWork unitOfWork,
    IInviteService invites,
    ICurrentUser currentUser,
    IAuditWriter audit) : ITenantInviteService
{
    public async Task<Result<TenantInviteResponse>> CreateAsync(CreateTenantInviteRequest request)
    {
        if (!currentUser.IsSuperAdmin)
            return Result<TenantInviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);

        // The invite service owns the rules (email required, single use, 30-day cap, address does
        // not already own a workspace) so the legacy route cannot bypass them.
        var created = await invites.CreateAsync(
            new CreateInviteRequest
            {
                CreateNewWorkspace = true,
                Email = request.Email,
                DisplayName = request.DisplayName,
                PlanId = request.PlanId,
                ExpiresInDays = request.ExpiresInDays,
            });

        if (!created.IsSuccess || created.Data is null)
            return created.IsConflict
                ? Result<TenantInviteResponse>.Conflict(created.Message ?? MessageKeys.Auth.AccountExists)
                : Result<TenantInviteResponse>.Failure(created.Message ?? MessageKeys.Invite.NotFound);

        var row = await LoadWorkspaceInviteAsync(created.Data.Id);
        if (row is null)
            return Result<TenantInviteResponse>.NotFound(MessageKeys.Invite.NotFound);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantInviteCreated,
                AuditTargets.TenantInvite,
                row.Id.ToString(),
                null,
                After: new Dictionary<string, string>
                {
                    ["plan_id"] = row.PlanId?.ToString() ?? string.Empty,
                    ["expires_at"] = row.ExpiresAt.ToString("O"),
                }
            )
        );

        return Result<TenantInviteResponse>.Success(await MapAsync(row, created.Data.Url, created.Data.EmailSent));
    }

    public async Task<Result<List<TenantInviteResponse>>> ListAsync()
    {
        if (!currentUser.IsSuperAdmin)
            return Result<List<TenantInviteResponse>>.Forbidden(MessageKeys.Invite.Forbidden);

        var now = DateTime.UtcNow;

        var rows = await unitOfWork
            .Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(i => i.OwnerId == null
                        && i.DeletedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now
                        // `Uses < MaxUses` is NULL (false) in SQL when MaxUses is null, which would
                        // hide every invite created before single-use was enforced — still valid,
                        // still acceptable, but invisible.
                        && (i.MaxUses == null || i.Uses < i.MaxUses))
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync();

        var mapped = new List<TenantInviteResponse>(rows.Count);
        foreach (var row in rows)
        {
            // Url is deliberately omitted on list rows: the code is a bearer credential, and a list
            // endpoint is the wrong place to hand it out. Resend returns it when it is needed.
            mapped.Add(await MapAsync(row, url: null, emailSent: null));
        }

        return Result<List<TenantInviteResponse>>.Success(mapped);
    }

    public async Task<Result<TenantInviteResponse>> ResendAsync(int id, bool rotate)
    {
        if (!currentUser.IsSuperAdmin)
            return Result<TenantInviteResponse>.Forbidden(MessageKeys.Invite.Forbidden);

        var invite = await LoadWorkspaceInviteAsync(id);
        if (invite is null)
            return Result<TenantInviteResponse>.NotFound(MessageKeys.Invite.NotFound);

        var resent = await invites.ResendAsync(id, rotate);
        if (!resent.IsSuccess || resent.Data is null)
            return Result<TenantInviteResponse>.Failure(resent.Message ?? MessageKeys.Invite.NotFound);

        var refreshed = await LoadWorkspaceInviteAsync(id);
        if (refreshed is null)
            return Result<TenantInviteResponse>.NotFound(MessageKeys.Invite.NotFound);

        await audit.WriteAsync(
            new AuditEntry(
                AuditActions.TenantInviteResent,
                AuditTargets.TenantInvite,
                refreshed.Id.ToString(),
                null,
                After: new Dictionary<string, string>
                {
                    ["plan_id"] = refreshed.PlanId?.ToString() ?? string.Empty,
                    ["expires_at"] = refreshed.ExpiresAt.ToString("O"),
                }
            )
        );

        return Result<TenantInviteResponse>.Success(await MapAsync(refreshed, resent.Data.Url, resent.Data.EmailSent));
    }

    public async Task<Result> RevokeAsync(int id)
    {
        if (!currentUser.IsSuperAdmin)
            return Result.Forbidden(MessageKeys.Invite.Forbidden);

        // The guard that makes this surface safe: without it, an id belonging to a tenant's own
        // staff invite would be revoked from a screen labelled "workspace invitations".
        var invite = await LoadWorkspaceInviteAsync(id);
        if (invite is null)
            return Result.NotFound(MessageKeys.Invite.NotFound);

        var result = await invites.RevokeAsync(id);
        if (!result.IsSuccess)
            return result;

        await audit.WriteAsync(
            new AuditEntry(AuditActions.TenantInviteRevoked, AuditTargets.TenantInvite, invite.Id.ToString(), null)
        );

        return result;
    }

    /// <summary>Loads an invite only if it is a workspace invite (null owner) and still live.</summary>
    private async Task<Invite?> LoadWorkspaceInviteAsync(int id) =>
        await unitOfWork
            .Repository<Invite>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id && i.OwnerId == null && i.DeletedAt == null);

    private async Task<TenantInviteResponse> MapAsync(Invite invite, string? url, bool? emailSent)
    {
        string? planName = null;
        if (invite.PlanId is int planId)
        {
            planName = await unitOfWork
                .Repository<Plan>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => p.Id == planId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync();
        }

        return new TenantInviteResponse
        {
            Id = invite.Id,
            Email = invite.Email ?? string.Empty,
            DisplayName = invite.DisplayName,
            PlanId = invite.PlanId,
            PlanName = planName,
            ExpiresAt = invite.ExpiresAt,
            CreatedAt = invite.CreatedAt,
            Url = url,
            EmailSent = emailSent,
        };
    }
}
