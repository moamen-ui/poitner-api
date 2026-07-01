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

        if (request.PredefinedActions.Count > 0)
        {
            var sort = 0;
            foreach (var input in request.PredefinedActions)
            {
                await _unitOfWork.Repository<PredefinedAction>().AddAsync(new PredefinedAction
                {
                    OwnerId = project.OwnerId, // inherit the project's owner (may be null = global)
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
        // Freshly-created project: no comments; creator is the caller.
        return Result<ProjectResponse>.Success(MapToResponse(project, actions, 0,
            await ResolveCreatorNameAsync(project.CreatedBy)));
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

        // BINDING #6: batch comment counts in ONE GroupBy query (no N+1).
        var commentCounts = await _unitOfWork.Repository<Comment>()
            .Query()
            .AsNoTracking()
            .Where(c => c.DeletedAt == null && projectIds.Contains(c.ProjectId))
            .GroupBy(c => c.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countByProject = commentCounts.ToDictionary(x => x.ProjectId, x => x.Count);

        // Batch-resolve creator display names.
        var creatorNames = await ResolveCreatorNamesAsync(projects.Select(p => p.CreatedBy));

        var responses = projects
            .Select(p => MapToResponse(
                p,
                byProject.GetValueOrDefault(p.Id) ?? new List<PredefinedAction>(),
                countByProject.GetValueOrDefault(p.Id, 0),
                creatorNames.GetValueOrDefault(p.CreatedBy)))
            .ToList();

        return Result<List<ProjectResponse>>.Success(responses);
    }

    public async Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request)
    {
        // Authz load uses the NORMAL query filter (in-tenant projects are already visible in List).
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result<ProjectResponse>.NotFound(MessageKeys.Project.NotFound);

        // Only an admin or the project's creator may edit. Forbidden (not NotFound) because the
        // project is already visible to the caller in List.
        if (!(_currentUser.IsAdmin || project.CreatedBy == _currentUser.Id))
            return Result<ProjectResponse>.Forbidden(MessageKeys.Project.NotFound);

        if (request.Name != null)
            project.Name = request.Name;

        if (request.IsActive.HasValue)
            project.IsActive = request.IsActive.Value;

        // NOTE: intentionally do NOT mutate project.OwnerId here. A null owner is legitimate for
        // global projects (e.g. the marketing landing); rewriting it would break the widget for
        // that project's null-owner stakeholders. Predefined actions on a null-owner project are a
        // known limitation pending the nullable-owner follow-up.
        _unitOfWork.Repository<Project>().Update(project);

        // Reconcile project-scoped predefined actions when the caller sends the list.
        // null (property omitted) → leave actions untouched. Actions inherit the project's owner
        // (which may be null for a global/null-owner project).
        if (request.PredefinedActions != null)
            await ReconcileActionsAsync(project.Id, project.OwnerId, request.PredefinedActions);

        await _unitOfWork.SaveChangesAsync();

        var actions = await LoadProjectActionsAsync(project.Id);
        var commentsCount = await _unitOfWork.Repository<Comment>()
            .Query().AsNoTracking()
            .CountAsync(c => c.ProjectId == project.Id && c.DeletedAt == null);
        return Result<ProjectResponse>.Success(MapToResponse(project, actions, commentsCount,
            await ResolveCreatorNameAsync(project.CreatedBy)));
    }

    public async Task<Result> DeleteAsync(int id)
    {
        // Authz load uses the NORMAL query filter (IgnoreQueryFilters only inside the cascade below).
        var project = await _unitOfWork.Repository<Project>().GetByIdAsync(id);

        if (project == null || project.DeletedAt != null)
            return Result.NotFound(MessageKeys.Project.NotFound);

        // BINDING #6: re-check comment count + ownership server-side regardless of any client hint.
        var commentsCount = await _unitOfWork.Repository<Comment>()
            .Query().AsNoTracking()
            .CountAsync(c => c.ProjectId == id && c.DeletedAt == null);

        if (_currentUser.IsAdmin)
        {
            // Admin cascade soft-delete — BINDING #2: keyed strictly off ProjectId, NEVER OwnerId
            // (an OwnerId scope would wipe the whole tenant). All inside the retry-safe transaction.
            await _unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                var now = DateTime.UtcNow;
                var actorId = _currentUser.Id;

                var comments = await _unitOfWork.Repository<Comment>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(c => c.ProjectId == id && c.DeletedAt == null)
                    .ToListAsync();
                var commentIds = comments.Select(c => c.Id).ToList();

                foreach (var c in comments)
                {
                    c.DeletedAt = now; c.DeletedBy = actorId;
                    _unitOfWork.Repository<Comment>().Update(c);
                }

                if (commentIds.Count > 0)
                {
                    var replies = await _unitOfWork.Repository<Reply>()
                        .Query()
                        .IgnoreQueryFilters()
                        .Where(r => commentIds.Contains(r.CommentId) && r.DeletedAt == null)
                        .ToListAsync();
                    foreach (var r in replies)
                    {
                        r.DeletedAt = now; r.DeletedBy = actorId;
                        _unitOfWork.Repository<Reply>().Update(r);
                    }
                }

                var actions = await _unitOfWork.Repository<PredefinedAction>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(a => a.ProjectId == id && a.DeletedAt == null)
                    .ToListAsync();
                foreach (var a in actions)
                {
                    a.DeletedAt = now; a.DeletedBy = actorId;
                    _unitOfWork.Repository<PredefinedAction>().Update(a);
                }

                var suggestions = await _unitOfWork.Repository<PredefinedActionSuggestion>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(s => s.ProjectId == id && s.DeletedAt == null)
                    .ToListAsync();
                foreach (var s in suggestions)
                {
                    s.DeletedAt = now; s.DeletedBy = actorId;
                    _unitOfWork.Repository<PredefinedActionSuggestion>().Update(s);
                }

                project.DeletedAt = now; project.DeletedBy = actorId;
                _unitOfWork.Repository<Project>().Update(project);

                await _unitOfWork.SaveChangesAsync();
            });

            return Result.Success();
        }

        // Non-admin: only the owner, and only when the project has no comments.
        if (project.CreatedBy != _currentUser.Id)
            return Result.Forbidden(MessageKeys.Project_Delete.NotOwner);

        if (commentsCount != 0)
            return Result.Conflict(MessageKeys.Project_Delete.HasComments);

        // Owner + 0 comments: soft-delete the project + its predefined actions + suggestions
        // (no comments/replies exist by definition).
        var ownActions = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .Where(a => a.ProjectId == id && a.DeletedAt == null)
            .ToListAsync();
        foreach (var a in ownActions)
        {
            a.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedAction>().Update(a);
        }

        var ownSuggestions = await _unitOfWork.Repository<PredefinedActionSuggestion>()
            .Query()
            .Where(s => s.ProjectId == id && s.DeletedAt == null)
            .ToListAsync();
        foreach (var s in ownSuggestions)
        {
            s.DeletedAt = DateTime.UtcNow;
            _unitOfWork.Repository<PredefinedActionSuggestion>().Update(s);
        }

        project.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Project>().Update(project);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    /// <summary>
    /// Reconcile the desired set of project-scoped actions against what exists (last-write-wins):
    ///   id present    → update in place
    ///   id absent      → add
    ///   existing row absent from the payload → soft-delete
    /// All queries add DeletedAt == null so soft-deleted rows never resurface or double-delete.
    /// </summary>
    private async Task ReconcileActionsAsync(int projectId, Guid? owner, List<PredefinedActionInput> desired)
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

    // Batch-resolve creator display names (Project.CreatedBy is a User.PublicId). One query.
    private async Task<Dictionary<Guid, string>> ResolveCreatorNamesAsync(IEnumerable<Guid> ids)
    {
        var distinct = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<Guid, string>();

        return await _unitOfWork.Repository<User>()
            .Query()
            .AsNoTracking()
            .Where(u => distinct.Contains(u.PublicId))
            .ToDictionaryAsync(u => u.PublicId, u => u.DisplayName);
    }

    private async Task<string?> ResolveCreatorNameAsync(Guid id) =>
        (await ResolveCreatorNamesAsync(new[] { id })).GetValueOrDefault(id);

    private ProjectResponse MapToResponse(Project project, List<PredefinedAction> actions, int commentsCount, string? createdByName)
    {
        var canEdit = _currentUser.IsAdmin || project.CreatedBy == _currentUser.Id;
        var canDelete = _currentUser.IsAdmin || (project.CreatedBy == _currentUser.Id && commentsCount == 0);

        return new ProjectResponse
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
                .ToList(),
            CreatedByName = createdByName,
            CommentsCount = commentsCount,
            CanEdit = canEdit,
            CanDelete = canDelete
        };
    }
}
