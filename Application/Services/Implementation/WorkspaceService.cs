using Microsoft.EntityFrameworkCore;
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
    private const int MaxNameLength = 120;

    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditWriter _audit;

    public WorkspaceService(IUnitOfWork unitOfWork, ICurrentUser currentUser, IAuditWriter? audit = null)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    public async Task<Result<WorkspaceResponse>> GetAsync()
    {
        if (_currentUser.IsSuperAdmin)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);
        if (_currentUser.IsQuickAccess)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var ownerId = TenantStamp.OwnerFor(_currentUser);
        if (ownerId is not Guid owner)
            return Result<WorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden);

        // Through the query filter (super admin already returned above; a tenant sees only its
        // own row) — no IgnoreQueryFilters here, the filter is the second line of defence.
        var row = await _unitOfWork
            .Workspaces.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == owner);

        if (row == null)
            return Result<WorkspaceResponse>.NotFound(MessageKeys.Workspace.NotFound);

        return Result<WorkspaceResponse>.Success(ToResponse(row));
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

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return Result<WorkspaceResponse>.Failure(MessageKeys.Workspace.NameRequired);
        if (name.Length > MaxNameLength)
            return Result<WorkspaceResponse>.Failure(MessageKeys.Workspace.NameTooLong);
        if (name.Any(char.IsControl))
            return Result<WorkspaceResponse>.Failure(MessageKeys.Workspace.NameInvalid);

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

        return Result<WorkspaceResponse>.Success(ToResponse(row));
    }

    private static WorkspaceResponse ToResponse(Workspace row) =>
        new()
        {
            Id = row.Id,
            Name = row.Name,
            IsPlaceholderName = row.Name == Workspace.PlaceholderName,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
        };
}
