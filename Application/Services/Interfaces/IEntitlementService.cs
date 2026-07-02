using Pointer.Application.Response;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// Resolves a tenant's effective plan entitlements and enforces the count/flag levers. Built once,
/// reused — never copy-pasted into each service. Decoupled from the counted entities: the CALLER
/// passes the current count (the caller owns the entity + its filter), keeping this service from
/// having to know how to count every entity.
///
/// The <c>EnforcementEnabled</c> kill-switch (ISettingsService, default false) makes every
/// <see cref="CheckCountAsync"/>/<see cref="EnforceFlagAsync"/> pass until an operator flips it on.
/// Missing/malformed entitlement values resolve to the catalog default (never 0/false).
/// </summary>
public interface IEntitlementService
{
    /// <summary>The tenant's effective entitlements (Subscription→Plan; missing ⇒ Free). Cached per request.</summary>
    Task<PlanEntitlements> GetForTenantAsync(Guid tenantId);

    /// <summary>
    /// Compare-only count check for the CURRENT caller's tenant. Returns <c>LimitReached</c> when the
    /// key's limit != -1 and <paramref name="currentCount"/> >= limit. Passes when the kill-switch is off.
    /// </summary>
    Task<Result> CheckCountAsync(string key, int currentCount);

    /// <summary>Same as <see cref="CheckCountAsync(string,int)"/> but for an explicit tenant (e.g. a
    /// comment counts against the PROJECT owner, not the caller).</summary>
    Task<Result> CheckCountAsync(Guid tenantId, string key, int currentCount);

    /// <summary>Boolean feature gate for the current caller's tenant (e.g. ExtensionEnabled).</summary>
    Task<Result> EnforceFlagAsync(string key);

    /// <summary>Boolean feature gate for an explicit tenant.</summary>
    Task<Result> EnforceFlagAsync(Guid tenantId, string key);
}
