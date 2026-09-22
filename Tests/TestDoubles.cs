using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pointer.Application.Abstractions;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.ValueObjects;

namespace Pointer.Tests;

public sealed class FakeLoginAttemptLimiter : ILoginAttemptLimiter
{
    public Task<bool> IsLockedAsync(string email) => Task.FromResult(false);
    public Task<int> GetRetryAfterSecondsAsync(string email) => Task.FromResult(0);
    public Task RecordFailureAsync(string email) => Task.CompletedTask;
    public Task ResetAsync(string email) => Task.CompletedTask;
}

/// <summary>
/// A pass-through <see cref="IEntitlementService"/> for tests that don't exercise plan enforcement.
/// Every check succeeds — equivalent to the kill-switch being off (the production default). Tests that
/// DO assert enforcement build a real <c>EntitlementService</c> against seeded plans instead.
/// </summary>
public sealed class PassThroughEntitlements : IEntitlementService
{
    public Task<PlanEntitlements> GetForTenantAsync(Guid tenantId) =>
        Task.FromResult(new PlanEntitlements());

    public Task<Result> CheckCountAsync(string key, int currentCount) =>
        Task.FromResult(Result.Success());

    public Task<Result> CheckCountAsync(Guid tenantId, string key, int currentCount) =>
        Task.FromResult(Result.Success());

    public Task<Result> EnforceFlagAsync(string key) => Task.FromResult(Result.Success());

    public Task<Result> EnforceFlagAsync(Guid tenantId, string key) =>
        Task.FromResult(Result.Success());
}

/// <summary>
/// DB-03's <c>ck_workspaces_name_not_blank</c> check constraint uses Postgres' <c>btrim</c>, which
/// SQLite does not ship (it has <c>trim</c>, not <c>btrim</c>). SQLite resolves every function named
/// in a CHECK constraint eagerly — at <c>CREATE TABLE</c> time AND again on every subsequent
/// INSERT/UPDATE against that table, per connection — so a Sqlite-backed test fixture needs
/// <c>btrim</c> registered on every connection it opens, not just the one that ran EnsureCreated().
/// Test-only: production only ever runs against Npgsql, which has the real function.
/// </summary>
public sealed class SqliteBtrimFunctionInterceptor : DbConnectionInterceptor
{
    private static void Register(DbConnection connection)
    {
        if (connection is SqliteConnection sqlite)
            sqlite.CreateFunction<string?, string?>("btrim", s => s?.Trim());
    }

    public override void ConnectionOpened(
        DbConnection connection,
        ConnectionEndEventData eventData
    ) => Register(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default
    )
    {
        Register(connection);
        return Task.CompletedTask;
    }
}
