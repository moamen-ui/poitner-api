using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class PredefinedActionService : IPredefinedActionService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IProjectService _projectService;
    private readonly ICurrentUser _currentUser;
    private readonly IEntitlementService _entitlements;

    public PredefinedActionService(IUnitOfWork unitOfWork, IProjectService projectService, ICurrentUser currentUser, IEntitlementService entitlements)
    {
        _unitOfWork = unitOfWork;
        _projectService = projectService;
        _currentUser = currentUser;
        _entitlements = entitlements;
    }

    // ── Tenant-wide admin CRUD (ProjectId == null) ────────────────────────────

    public async Task<Result<List<PredefinedActionResponse>>> ListTenantAsync()
    {
        // Query filter scopes to tenant; DeletedAt == null added explicitly (filter does not cover it).
        var rows = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null && a.ProjectId == null)
            .OrderBy(a => a.SortOrder)
            .ToListAsync();

        return Result<List<PredefinedActionResponse>>.Success(rows.Select(MapToResponse).ToList());
    }

    public async Task<Result<PredefinedActionResponse>> CreateTenantAsync(CreatePredefinedActionRequest request)
    {
        // Scoped admin → their tenant; super-admin (no tenant) → their own user id, so the operator
        // can manage workspace-wide actions too (consistent non-null owner).
        // ISOLATION-LOAD-BEARING: this MUST stamp a non-null owner. A null-owner tenant-wide row
        // (OwnerId == null, ProjectId == null) would become visible/editable by EVERY tenant under
        // the own-plus-global query filter. Never relax this to allow a null owner here.
        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner)
            return Result<PredefinedActionResponse>.Forbidden(MessageKeys.PredefinedAction.NotFound);

        // MaxTenantWidePredefinedActions: count active tenant-wide (ProjectId == null) actions for this
        // tenant. Grandfather-safe (counts DeletedAt == null, checked only on create).
        var tenantWideCount = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(a => a.OwnerId == owner && a.ProjectId == null && a.DeletedAt == null);
        var check = await _entitlements.CheckCountAsync(owner, EntitlementCatalog.MaxTenantWidePredefinedActions, tenantWideCount);
        if (!check.IsSuccess)
            return Result<PredefinedActionResponse>.LimitReached(check.Message ?? MessageKeys.Plan.LimitReached, check.Limit!);

        // SortOrder default = max(scope) + 1.
        var sortOrder = request.SortOrder ?? await NextSortOrderAsync(a => a.ProjectId == null);

        var entity = new PredefinedAction
        {
            OwnerId = owner,
            ProjectId = null,
            UserId = null,
            Text = request.Text.Trim(),
            Prompt = request.Prompt,
            IsActive = request.IsActive,
            SortOrder = sortOrder
        };

        await _unitOfWork.Repository<PredefinedAction>().AddAsync(entity);
        await _unitOfWork.SaveChangesAsync();

        return Result<PredefinedActionResponse>.Success(MapToResponse(entity));
    }

    // Loads a TENANT-WIDE action owned by the caller. Explicitly scoped (IgnoreQueryFilters + own
    // owner + ProjectId == null) rather than relying on the query filter: the own-plus-global filter
    // would otherwise let a tenant load a null-owner GLOBAL action by guessing its id (cross-tenant
    // read of the LLM prompt + write). These endpoints are tenant-wide-only by contract.
    private async Task<PredefinedAction?> LoadOwnTenantWideAsync(int id)
    {
        var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
        if (ownerId is not Guid owner) return null;

        return await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .IgnoreQueryFilters()
            .Where(a => a.Id == id && a.DeletedAt == null && a.ProjectId == null && a.OwnerId == owner)
            .FirstOrDefaultAsync();
    }

    public async Task<Result<PredefinedActionResponse>> UpdateAsync(int id, UpdatePredefinedActionRequest request)
    {
        var entity = await LoadOwnTenantWideAsync(id);

        if (entity == null)
            return Result<PredefinedActionResponse>.NotFound(MessageKeys.PredefinedAction.NotFound);

        if (request.Text != null)
            entity.Text = request.Text.Trim();
        if (request.Prompt != null)
            entity.Prompt = request.Prompt;
        if (request.IsActive.HasValue)
            entity.IsActive = request.IsActive.Value;
        if (request.SortOrder.HasValue)
            entity.SortOrder = request.SortOrder.Value;

        _unitOfWork.Repository<PredefinedAction>().Update(entity);
        await _unitOfWork.SaveChangesAsync();

        return Result<PredefinedActionResponse>.Success(MapToResponse(entity));
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var entity = await LoadOwnTenantWideAsync(id);

        if (entity == null)
            return Result.NotFound(MessageKeys.PredefinedAction.NotFound);

        entity.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<PredefinedAction>().Update(entity);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    // ── Widget-facing effective set ───────────────────────────────────────────

    public async Task<Result<List<PredefinedActionOption>>> GetEffectiveForProjectAsync(string projectKey, Guid userId)
    {
        // Strict resolver: NotFound for missing, Conflict for disabled, else the project id.
        // Also confirms the project belongs to the caller's tenant (owner-scoped key).
        var projectResult = await _projectService.EnsureAsync(projectKey);
        if (!projectResult.IsSuccess)
            return projectResult.IsConflict
                ? Result<List<PredefinedActionOption>>.Conflict(projectResult.Message ?? MessageKeys.Project.Disabled)
                : Result<List<PredefinedActionOption>>.NotFound(projectResult.Message ?? MessageKeys.Project.NotFound);

        var projectId = projectResult.Data;

        // Query filter scopes OwnerId to the tenant (from JWT); we add the scope dimensions.
        // No IgnoreQueryFilters — no cross-tenant key collision.
        var options = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null
                        && a.IsActive
                        && (a.ProjectId == null || a.ProjectId == projectId)
                        && (a.UserId == null || a.UserId == userId))
            .OrderBy(a => a.SortOrder)
            .Select(a => new PredefinedActionOption { Id = a.Id, Text = a.Text })
            .ToListAsync();

        return Result<List<PredefinedActionOption>>.Success(options);
    }

    // ── Comment-create validation ─────────────────────────────────────────────

    public async Task<PredefinedAction?> ResolveInScopeAsync(int predefinedActionId, int projectId, Guid? ownerId, Guid userId)
    {
        // Ignore the tenant query filter and match OwnerId explicitly against the resolved PROJECT's
        // owner (the real isolation boundary): comment-create may run under a super-admin caller whose
        // TenantId differs from the project's owner. ownerId may be null for a global/null-owner
        // project — branch so EF emits `owner_id IS NULL` rather than a null-parameter comparison.
        var q = _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(a => a.Id == predefinedActionId
                        && a.DeletedAt == null
                        && a.IsActive
                        && (a.ProjectId == null || a.ProjectId == projectId)
                        && (a.UserId == null || a.UserId == userId));

        q = ownerId is Guid oid ? q.Where(a => a.OwnerId == oid) : q.Where(a => a.OwnerId == null);

        return await q.FirstOrDefaultAsync();
    }

    private async Task<int> NextSortOrderAsync(System.Linq.Expressions.Expression<Func<PredefinedAction, bool>> scope)
    {
        var rows = await _unitOfWork.Repository<PredefinedAction>()
            .Query()
            .AsNoTracking()
            .Where(a => a.DeletedAt == null)
            .Where(scope)
            .Select(a => (int?)a.SortOrder)
            .ToListAsync();

        return (rows.Count == 0 ? -1 : rows.Max() ?? -1) + 1;
    }

    private static PredefinedActionResponse MapToResponse(PredefinedAction a) => new()
    {
        Id = a.Id,
        ProjectId = a.ProjectId,
        Text = a.Text,
        Prompt = a.Prompt,
        IsActive = a.IsActive,
        SortOrder = a.SortOrder
    };
}
