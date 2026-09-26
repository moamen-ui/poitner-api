using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// DB-03b: read/rename the caller's own workspace row. Guards copied from
/// CommentFieldService.GetDefinitionsAsync/UpdateDefinitionsAsync (super admins and quick-access
/// users have no workspace of their own). Reads through the tenant query filter on Workspace
/// (never IgnoreQueryFilters) — R8: the filter is the second line of defence after the guards.
/// </summary>
public class WorkspaceService : IWorkspaceService
{
    private const int MaxNameLength = WorkspaceNameRules.MaxLength;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditWriter _audit;
    private readonly IMembershipService? _memberships;
    private readonly IConfiguration? _config;

    public WorkspaceService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IAuditWriter? audit = null,
        // DB-18: nullable-with-default, same seam as `audit` — a null value (every existing
        // hand-rolled test construction) simply reports CanManageLifecycle = false.
        IMembershipService? memberships = null,
        IConfiguration? config = null
    )
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _audit = audit ?? NoopAuditWriter.Instance;
        _memberships = memberships;
        _config = config;
    }

    public async Task<Result<WorkspaceResponse>> GetAsync()
    {
        // DB-13 (F2): the impersonating operator sees the target's workspace card; a plain super
        // admin still does not (RenameAsync below is a write and stays unchanged either way).
        if (_currentUser.IsSuperAdmin && !_currentUser.IsImpersonating)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.IsQuickAccess)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-13: a read scope — TenantId (the impersonation token's `tenant` claim when
        // impersonating), not TenantStamp.OwnerFor (which is null for every super admin).
        var ownerId = _currentUser.TenantId;
        if (ownerId is not Guid owner)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // Through the query filter (super admin already returned above; a tenant sees only its
        // own row) — no IgnoreQueryFilters here, the filter is the second line of defence.
        var row = await _unitOfWork
            .Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == owner);

        if (row == null)
            return Result<WorkspaceResponse>.NotFound(MessageKeys.Workspace.NotFound);

        return Result<WorkspaceResponse>.Success(await BuildResponseAsync(row));
    }

    public async Task<Result<WorkspaceResponse>> RenameAsync(UpdateWorkspaceNameRequest request)
    {
        if (_currentUser.IsSuperAdmin)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.IsQuickAccess)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser);
        if (ownerId is not Guid owner)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // DB-19 task 6: the rule lives in WorkspaceNameRules, shared with the new-workspace path.
        if (WorkspaceNameRules.Validate(request.Name, out var name) is string nameError)
            return Result<WorkspaceResponse>.Failure(nameError);

        // Tracked load through the query filter — same owner predicate as the read, so a super
        // admin (excluded above) or a mismatched tenant can never reach another workspace's row.
        var row = await _unitOfWork.Workspaces.FirstOrDefaultAsync(w => w.Id == owner);

        if (row == null)
            return Result<WorkspaceResponse>.NotFound(MessageKeys.Workspace.NotFound);

        var previousName = row.Name;
        row.Name = name;
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = _currentUser.Id;
        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceRenamed,
                AuditTargets.Workspace,
                owner.ToString(),
                owner,
                Before: new Dictionary<string, string> { ["name"] = previousName },
                After: new Dictionary<string, string> { ["name"] = name }
            )
        );

        return Result<WorkspaceResponse>.Success(await BuildResponseAsync(row));
    }

    /// <summary>DB-18 §3.4 — adds the lifecycle fields to the base mapping. Name resolution and the
    /// lifecycle guard are both read-only, best-effort (missing DI in a hand-rolled test simply
    /// yields null names / CanManageLifecycle = false).</summary>
    private async Task<WorkspaceResponse> BuildResponseAsync(Workspace row)
    {
        var response = ToResponse(row);

        var ids = new List<Guid>();
        if (row.PausedAt != null && !row.PausedByOperator && row.PausedBy is Guid pausedBy)
            ids.Add(pausedBy);
        if (row.DeletionRequestedBy is Guid requestedBy)
            ids.Add(requestedBy);

        if (ids.Count > 0)
        {
            var names = await UserNameResolver.ResolveAsync(
                _unitOfWork,
                ids,
                ignoreQueryFilters: true
            );
            if (row.PausedAt != null && !row.PausedByOperator && row.PausedBy is Guid pb)
                response.PausedByName = names.GetValueOrDefault(pb);
            if (row.DeletionRequestedBy is Guid rb)
                response.DeletionRequestedByName = names.GetValueOrDefault(rb);
        }

        response.CanManageLifecycle =
            _memberships != null
            && await WorkspaceLifecycleGuard.CanManageAsync(_currentUser, _memberships, row);
        response.GraceDays = WorkspaceDeletionConfig.GraceDays(_config);

        return response;
    }

    private static WorkspaceResponse ToResponse(Workspace row) =>
        new()
        {
            Id = row.Id,
            Name = row.Name,
            IsPlaceholderName = row.Name == Workspace.PlaceholderName,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            PausedAt = row.PausedAt,
            PausedByOperator = row.PausedByOperator,
            DeletionRequestedAt = row.DeletionRequestedAt,
            DeletionScheduledFor = row.DeletionScheduledFor,
        };
}
