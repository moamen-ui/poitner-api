using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Plan;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// Super-admin CRUD over the GLOBAL Plan catalog (Plan has no query filter — plain queries), plus the
/// anonymous public projection. Delete is soft and blocked for the Free fallback or any plan with
/// active subscriptions (mirrors the role-delete-in-use guard).
/// </summary>
public class PlanService : IPlanService
{
    private readonly IUnitOfWork _unitOfWork;

    public PlanService(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<List<PlanAdminResponse>>> ListAsync()
    {
        var plans = await _unitOfWork.Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        var planIds = plans.Select(p => p.Id).ToList();
        var subCounts = await _unitOfWork.Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && planIds.Contains(s.PlanId))
            .GroupBy(s => s.PlanId)
            .Select(g => new { PlanId = g.Key, Count = g.Count() })
            .ToListAsync();
        var countByPlan = subCounts.ToDictionary(x => x.PlanId, x => x.Count);

        return Result<List<PlanAdminResponse>>.Success(
            plans.Select(p => MapAdmin(p, countByPlan.GetValueOrDefault(p.Id, 0))).ToList());
    }

    public async Task<Result<PlanAdminResponse>> CreateAsync(PlanWriteDto request)
    {
        var slug = request.Slug.Trim().ToLower();

        if (await _unitOfWork.Repository<Plan>().Query().AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null && p.Slug == slug))
            return Result<PlanAdminResponse>.Conflict(MessageKeys.Plan.SlugTaken);

        if (await _unitOfWork.Repository<Plan>().Query().AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null && p.Name == request.Name.Trim()))
            return Result<PlanAdminResponse>.Conflict(MessageKeys.Plan.NameTaken);

        var plan = new Plan
        {
            Name = request.Name.Trim(),
            Slug = slug,
            PriceMonthly = request.PriceMonthly,
            Currency = request.Currency.Trim(),
            Interval = request.Interval,
            SortOrder = request.SortOrder,
            IsActive = request.IsActive,
            DisplayState = request.DisplayState,
            FeatureBullets = request.FeatureBullets ?? new(),
            Entitlements = MapEntitlements(request.Entitlements)
        };

        await _unitOfWork.Repository<Plan>().AddAsync(plan);
        await _unitOfWork.SaveChangesAsync();

        return Result<PlanAdminResponse>.Success(MapAdmin(plan, 0), MessageKeys.Plan.Created);
    }

    public async Task<Result<PlanAdminResponse>> UpdateAsync(int id, PlanWriteDto request)
    {
        var plan = await _unitOfWork.Repository<Plan>().GetByIdAsync(id);
        if (plan == null || plan.DeletedAt != null)
            return Result<PlanAdminResponse>.NotFound(MessageKeys.Plan.NotFound);

        var slug = request.Slug.Trim().ToLower();
        var name = request.Name.Trim();

        if (await _unitOfWork.Repository<Plan>().Query().AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null && p.Id != id && p.Slug == slug))
            return Result<PlanAdminResponse>.Conflict(MessageKeys.Plan.SlugTaken);
        if (await _unitOfWork.Repository<Plan>().Query().AsNoTracking()
                .AnyAsync(p => p.DeletedAt == null && p.Id != id && p.Name == name))
            return Result<PlanAdminResponse>.Conflict(MessageKeys.Plan.NameTaken);

        plan.Name = name;
        plan.Slug = slug;
        plan.PriceMonthly = request.PriceMonthly;
        plan.Currency = request.Currency.Trim();
        plan.Interval = request.Interval;
        plan.SortOrder = request.SortOrder;
        plan.IsActive = request.IsActive;
        plan.DisplayState = request.DisplayState;
        plan.FeatureBullets = request.FeatureBullets ?? new();
        plan.Entitlements = MapEntitlements(request.Entitlements);

        _unitOfWork.Repository<Plan>().Update(plan);
        await _unitOfWork.SaveChangesAsync();

        var count = await ActiveSubCountAsync(plan.Id);
        return Result<PlanAdminResponse>.Success(MapAdmin(plan, count), MessageKeys.Plan.Updated);
    }

    public async Task<Result> DeleteAsync(int id)
    {
        var plan = await _unitOfWork.Repository<Plan>().GetByIdAsync(id);
        if (plan == null || plan.DeletedAt != null)
            return Result.NotFound(MessageKeys.Plan.NotFound);

        // Free is the fallback for tenants without a subscription — never deletable.
        if (plan.Slug == "free")
            return Result.Conflict(MessageKeys.Plan.CannotDeleteFree);

        // Block deletion while any active subscription references it (mirror role-delete-in-use).
        var inUse = await ActiveSubCountAsync(id);
        if (inUse > 0)
            return Result.Conflict($"{MessageKeys.Plan.InUse} ({inUse})");

        plan.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<Plan>().Update(plan);
        await _unitOfWork.SaveChangesAsync();
        return Result.Success(MessageKeys.Plan.Deleted);
    }

    public async Task<Result<List<PlanPublicResponse>>> ListPublicAsync()
    {
        var plans = await _unitOfWork.Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.DisplayState != PlanDisplayState.Hidden)
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        return Result<List<PlanPublicResponse>>.Success(plans.Select(MapPublic).ToList());
    }

    private async Task<int> ActiveSubCountAsync(int planId) =>
        await _unitOfWork.Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .CountAsync(s => s.PlanId == planId && s.DeletedAt == null);

    // ── Mapping ──────────────────────────────────────────────────────────────

    private static PlanEntitlements MapEntitlements(PlanEntitlementsDto d) => new()
    {
        MaxProjects = d.MaxProjects,
        MaxSeats = d.MaxSeats,
        MaxCommentsPerMonth = d.MaxCommentsPerMonth,
        ExtensionEnabled = d.ExtensionEnabled,
        MaxExtensionSites = d.MaxExtensionSites,
        MaxPredefinedActionsPerProject = d.MaxPredefinedActionsPerProject,
        MaxTenantWidePredefinedActions = d.MaxTenantWidePredefinedActions,
        RetentionDays = d.RetentionDays,
        MaxEnvironments = d.MaxEnvironments,
        MaxActiveInvites = d.MaxActiveInvites,
        EmailsPerMonth = d.EmailsPerMonth,
        ExtensionCommentsPerMonth = d.ExtensionCommentsPerMonth,
        MaxPendingSuggestions = d.MaxPendingSuggestions,
        ExportImportEnabled = d.ExportImportEnabled,
        PromptSuggestionsEnabled = d.PromptSuggestionsEnabled,
        CustomStatusesEnabled = d.CustomStatusesEnabled,
        PrioritySupport = d.PrioritySupport
    };

    private static PlanEntitlementsDto MapEntitlementsDto(PlanEntitlements e) => new()
    {
        MaxProjects = e.MaxProjects,
        MaxSeats = e.MaxSeats,
        MaxCommentsPerMonth = e.MaxCommentsPerMonth,
        ExtensionEnabled = e.ExtensionEnabled,
        MaxExtensionSites = e.MaxExtensionSites,
        MaxPredefinedActionsPerProject = e.MaxPredefinedActionsPerProject,
        MaxTenantWidePredefinedActions = e.MaxTenantWidePredefinedActions,
        RetentionDays = e.RetentionDays,
        MaxEnvironments = e.MaxEnvironments,
        MaxActiveInvites = e.MaxActiveInvites,
        EmailsPerMonth = e.EmailsPerMonth,
        ExtensionCommentsPerMonth = e.ExtensionCommentsPerMonth,
        MaxPendingSuggestions = e.MaxPendingSuggestions,
        ExportImportEnabled = e.ExportImportEnabled,
        PromptSuggestionsEnabled = e.PromptSuggestionsEnabled,
        CustomStatusesEnabled = e.CustomStatusesEnabled,
        PrioritySupport = e.PrioritySupport
    };

    private static PlanAdminResponse MapAdmin(Plan p, int activeSubs) => new()
    {
        Id = p.Id,
        Name = p.Name,
        Slug = p.Slug,
        PriceMonthly = p.PriceMonthly,
        Currency = p.Currency,
        Interval = p.Interval,
        SortOrder = p.SortOrder,
        IsActive = p.IsActive,
        DisplayState = p.DisplayState,
        FeatureBullets = p.FeatureBullets ?? new(),
        Entitlements = MapEntitlementsDto(p.Entitlements ?? new()),
        ActiveSubscriptions = activeSubs
    };

    private static PlanPublicResponse MapPublic(Plan p) => new()
    {
        Slug = p.Slug,
        Name = p.Name,
        PriceMonthly = p.PriceMonthly,
        Currency = p.Currency,
        Interval = p.Interval,
        FeatureBullets = p.FeatureBullets ?? new(),
        DisplayState = p.DisplayState,
        SortOrder = p.SortOrder
    };
}
