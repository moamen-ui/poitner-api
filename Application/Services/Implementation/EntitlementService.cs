using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

public class EntitlementService : IEntitlementService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly ISettingsService _settings;

    // Per-request caches (scoped service ⇒ one instance per request).
    private readonly Dictionary<Guid, (int PlanId, PlanEntitlements Entitlements)> _resolved = new();
    private int? _freePlanId;
    private bool? _enforcementEnabled;

    public EntitlementService(IUnitOfWork unitOfWork, ICurrentUser currentUser, ISettingsService settings)
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _settings = settings;
    }

    public async Task<PlanEntitlements> GetForTenantAsync(Guid tenantId)
    {
        var (_, entitlements) = await ResolveAsync(tenantId);
        return entitlements;
    }

    public Task<Result> CheckCountAsync(string key, int currentCount) =>
        CheckCountAsync(CurrentTenantId(), key, currentCount);

    public async Task<Result> CheckCountAsync(Guid tenantId, string key, int currentCount)
    {
        if (!await EnforcementOnAsync())
            return Result.Success();

        var (planId, entitlements) = await ResolveAsync(tenantId);
        var limit = EntitlementCatalog.ResolveInt(entitlements, key);

        // -1 = unlimited. Grandfather-safe by construction: the caller only invokes this on CREATE and
        // counts only active (DeletedAt == null) rows, so a downgrade never touches existing data.
        if (limit != -1 && currentCount >= limit)
            return Result.LimitReached(
                MessageKeys.Plan.LimitReached,
                new PlanLimit(key, currentCount, limit, planId));

        return Result.Success();
    }

    public Task<Result> EnforceFlagAsync(string key) => EnforceFlagAsync(CurrentTenantId(), key);

    public async Task<Result> EnforceFlagAsync(Guid tenantId, string key)
    {
        if (!await EnforcementOnAsync())
            return Result.Success();

        var (planId, entitlements) = await ResolveAsync(tenantId);
        var enabled = EntitlementCatalog.ResolveBool(entitlements, key);
        if (!enabled)
            return Result.LimitReached(
                MessageKeys.Plan.ExtensionDisabled,
                new PlanLimit(key, 0, 0, planId));

        return Result.Success();
    }

    // ── Internals ──────────────────────────────────────────────────────────────

    private Guid CurrentTenantId() =>
        TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id ?? Guid.Empty;

    private async Task<bool> EnforcementOnAsync()
    {
        _enforcementEnabled ??= await _settings.GetBoolAsync(ISettingsService.EnforcementEnabled, fallback: false);
        return _enforcementEnabled.Value;
    }

    private async Task<(int PlanId, PlanEntitlements Entitlements)> ResolveAsync(Guid tenantId)
    {
        if (_resolved.TryGetValue(tenantId, out var cached))
            return cached;

        // Subscription is tenant-scoped; enforcement may run under any caller, so bypass the query
        // filter and match OwnerId explicitly (count/lookup only — never a cross-tenant row read).
        var planId = await _unitOfWork.Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerId == tenantId && s.DeletedAt == null)
            .Select(s => (int?)s.PlanId)
            .FirstOrDefaultAsync();

        // Missing subscription ⇒ Free.
        var effectivePlanId = planId ?? await FreePlanIdAsync();

        // Plan is filter-free (global catalog) — plain query.
        var plan = await _unitOfWork.Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Id == effectivePlanId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Entitlements })
            .FirstOrDefaultAsync();

        var result = plan == null
            ? (effectivePlanId, new PlanEntitlements()) // no plan row ⇒ empty ⇒ everything resolves to catalog defaults
            : (plan.Id, plan.Entitlements ?? new PlanEntitlements());

        _resolved[tenantId] = result;
        return result;
    }

    private async Task<int> FreePlanIdAsync()
    {
        if (_freePlanId is int id)
            return id;

        var freeId = await _unitOfWork.Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => p.Slug == "free" && p.DeletedAt == null)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync();

        _freePlanId = freeId ?? 0;
        return _freePlanId.Value;
    }
}
