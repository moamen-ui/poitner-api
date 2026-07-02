using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.ValueObjects;

namespace Pointer.Tests;

/// <summary>
/// A pass-through <see cref="IEntitlementService"/> for tests that don't exercise plan enforcement.
/// Every check succeeds — equivalent to the kill-switch being off (the production default). Tests that
/// DO assert enforcement build a real <c>EntitlementService</c> against seeded plans instead.
/// </summary>
public sealed class PassThroughEntitlements : IEntitlementService
{
    public Task<PlanEntitlements> GetForTenantAsync(Guid tenantId) =>
        Task.FromResult(new PlanEntitlements());

    public Task<Result> CheckCountAsync(string key, int currentCount) => Task.FromResult(Result.Success());

    public Task<Result> CheckCountAsync(Guid tenantId, string key, int currentCount) =>
        Task.FromResult(Result.Success());

    public Task<Result> EnforceFlagAsync(string key) => Task.FromResult(Result.Success());

    public Task<Result> EnforceFlagAsync(Guid tenantId, string key) => Task.FromResult(Result.Success());
}
