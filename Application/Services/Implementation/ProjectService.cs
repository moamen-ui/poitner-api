using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.DTOs.Project;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class ProjectService : IProjectService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public ProjectService(IUnitOfWork unitOfWork, ICurrentUser currentUser)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request)
    {
        var keyNormalized = request.Key.Trim().ToLower();

        // EF query filter already scopes this to the caller's tenant;
        // the check is still correct — a scoped admin cannot key-conflict with another tenant.
        var exists = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
            .AnyAsync();

        if (exists)
            return Result<ProjectResponse>.Conflict(MessageKeys.Project.KeyTaken);

        // OwnerId stamps the tenant. A scoped admin stamps their TenantId; a super-admin (no tenant)
        // owns what they create under their own user id, so the resource always has a non-null owner
        // (predefined actions + comment-scope matching all rely on this).
        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;

        var project = new Project
        {
            Key = keyNormalized,
            Name = request.Name,
            IsActive = true,
            OwnerId = ownerId
        };

        await _unitOfWork.Repository<Project>().AddAsync(project);
        await _unitOfWork.SaveChangesAsync(); // need the project Id before attaching actions

        if (request.PredefinedActions.Count > 0 && ownerId is Guid owner)
        {
            var sort = 0;
            foreach (var input in request.PredefinedActions)
            {
                await _unitOfWork.Repository<PredefinedAction>().AddAsync(new PredefinedAction
                {
                    OwnerId = owner,
                    ProjectId = project.Id,
                    UserId = null,
                    Text = input.Text.Trim(),
                    Prompt = input.Prompt,
                    IsActive = input.IsActive,
                    SortOrder = input.SortOrder != 0 ? input.SortOrder : sort++
                });
            }
            await _unitOfWork.SaveChangesAsync();
        }

        var actions = await LoadProjectActionsAsync(project.Id);
        return Result<ProjectResponse>.Success(MapToResponse(project, actions));
    }

    public async Task<Result<List<ProjectResponse>>> ListAsync()
    {
        var projects = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .ToListAsync();

        var projectIds = projects.Select(p => p.Id).ToList();

        // One batched query for all project-scoped actions; group in memory.
        var actions = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))
            .OrderBy(a => a.SortOrder)
            .ToListAsync();

        var byProject = actions.GroupBy(a => a.ProjectId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var responses = projects
            .Select(p => MapToResponse(p, byProject.GetValueOrDefault(p.Id) ?? new List<PredefinedAction>()))
            .ToList();

        return Result<List<ProjectResponse>>.Success(responses);
    }

    public async Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request)
    {
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result<ProjectResponse>.NotFound(MessageKeys.Project.NotFound);

        if (request.Name != null)
            project.Name = request.Name;

        if (request.IsActive.HasValue)
            project.IsActive = request.IsActive.Value;

        // Repair legacy null-owner projects (e.g. created by a super-admin before ownership was
        // stamped) so the project + its actions share a non-null owner — required for the
        // comment-create scope match (projectOwner == action.OwnerId).
        project.OwnerId ??= TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;

        _unitOfWork.Repository<Project>().Update(project);

        // Reconcile project-scoped predefined actions when the caller sends the list.
        // null (property omitted) → leave actions untouched.
        if (request.PredefinedActions != null && project.OwnerId is Guid owner)
            await ReconcileActionsAsync(project.Id, owner, request.PredefinedActions);

        await _unitOfWork.SaveChangesAsync();

        var actions = await LoadProjectActionsAsync(project.Id);
        return Result<ProjectResponse>.Success(MapToResponse(project, actions));
    }

    /// <summary>
    /// Reconcile the desired set of project-scoped actions against what exists (last-write-wins):
    ///   id present    → update in place
    ///   id absent      → add
    ///   existing row absent from the payload → soft-delete
    /// All queries add DeletedAt == null so soft-deleted rows never resurface or double-delete.
    /// </summary>
    private async Task ReconcileActionsAsync(int projectId, Guid owner, List<PredefinedActionInput> desired)
    {
        var existing = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .Where(a => a.DeletedAt == null && a.ProjectId == projectId)
            .ToListAsync();

        var keptIds = new HashSet<int>();

        foreach (var input in desired)
        {
            if (input.Id is int existingId)
            {
                var row = existing.FirstOrDefault(a => a.Id == existingId);
                if (row == null)
                    continue; // stale/foreign id — ignore (last-write-wins, no cross-tenant edit)

                row.Text = input.Text.Trim();
                row.Prompt = input.Prompt;
                row.IsActive = input.IsActive;
                row.SortOrder = input.SortOrder;
                _unitOfWork.Repository<PredefinedAction>().Update(row);
                keptIds.Add(row.Id);
            }
            else
            {
                await _unitOfWork.Repository<PredefinedAction>().AddAsync(new PredefinedAction
                {
                    OwnerId = owner,
                    ProjectId = projectId,
                    UserId = null,
                    Text = input.Text.Trim(),
                    Prompt = input.Prompt,
                    IsActive = input.IsActive,
                    SortOrder = input.SortOrder
                });
            }
        }

        // Soft-delete rows absent from the payload.
        foreach (var row in existing.Where(a => !keptIds.Contains(a.Id)))
        {
            row.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedAction>().Update(row);
        }
    }

    private async Task<List<PredefinedAction>> LoadProjectActionsAsync(int projectId) =>
        await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId == projectId)
            .OrderBy(a => a.SortOrder)
            .ToListAsync();

    public async Task<Result<int>> EnsureAsync(string key)
    {
        var keyNormalized = key.Trim().ToLower();
        // Resolve by the caller's own scope: widget stakeholders are registered UNDER the project's
        // owner, so their OwnerFor equals the project's owner for any owner value — tenant, super-admin,
        // or null (legacy/super-admin-global projects like the marketing landing). Do NOT fall back to
        // the caller's user id here (that broke null-owner project resolution); the id fallback belongs
        // only on the write side (create/update stamping).
        var ownerId = TenantStamp.OwnerFor(_currentUser);

        // EF query filter is the primary tenant boundary; the explicit OwnerId match is
        // belt-and-suspenders to prevent a scoped admin's key resolving to a global project.
        var project = await _unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized && p.OwnerId == ownerId)
            .Select(p => new { p.Id, p.IsActive })
            .FirstOrDefaultAsync();

        // STRICT: projects must be pre-defined in the dashboard. No lazy self-create.
        // Missing → NotFound (widget hides silently on 404). Disabled → Conflict (below).
        if (project == null)
            return Result<int>.NotFound(MessageKeys.Project.NotFound);

        if (!project.IsActive)
            return Result<int>.Conflict(MessageKeys.Project.Disabled);

        return Result<int>.Success(project.Id);
    }

    private static ProjectResponse MapToResponse(Project project, List<PredefinedAction> actions) => new()
    {
        Id = project.Id,
        Key = project.Key,
        Name = project.Name,
        IsActive = project.IsActive,
        PredefinedActions = actions
            .OrderBy(a => a.SortOrder)
            .Select(a => new PredefinedActionResponse
            {
                Id = a.Id,
                ProjectId = a.ProjectId,
                Text = a.Text,
                Prompt = a.Prompt,
                IsActive = a.IsActive,
                SortOrder = a.SortOrder
            })
            .ToList()
    };
}
