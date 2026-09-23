using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Hosted;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-15 review BLOCKER #1: <c>DateOnly.ToDateTime(TimeOnly.MinValue)</c> yields
/// <c>DateTimeKind.Unspecified</c>, which Npgsql refuses to write to a <c>timestamptz</c> column —
/// the rollup would fail on every single pass in production, silently skipping the usage_events
/// sweep forever. Sqlite (used by <see cref="UsageRollupTests"/>) is Kind-agnostic and would never
/// have caught this, so this test runs the rollup against a REAL Postgres to prove
/// <c>DateTime.SpecifyKind(..., DateTimeKind.Utc)</c> actually fixes it.
///
/// Gated behind the POINTER_TEST_PG env var: skipped (returns immediately, passes trivially) unless
/// it is set, so `dotnet test` / CI never requires a live Postgres. Point it at a throwaway instance:
///
///   docker run -d --name db15b -e POSTGRES_PASSWORD=pw -p 5591:5432 postgres:15
///   POINTER_TEST_PG=1 dotnet test Tests --filter FullyQualifiedName~UsageRollupPostgresTests
///
/// (connection defaults to Host=localhost;Port=5591;Database=postgres;Username=postgres;Password=pw
/// — override with POINTER_TEST_PG_CONNSTRING for a different throwaway instance/port).
/// </summary>
[Trait("Category", "Postgres")]
public class UsageRollupPostgresTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("POINTER_TEST_PG_CONNSTRING")
        ?? "Host=localhost;Port=5591;Database=postgres;Username=postgres;Password=pw";

    private static AppDbContext MakeContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options,
            new FakeCurrentUser(),
            new ConfigurationBuilder().Build()
        );

    [Fact]
    public async Task Rollup_AgainstRealPostgres_WritesUtcKindedRows_AndIsIdempotent()
    {
        if (Environment.GetEnvironmentVariable("POINTER_TEST_PG") is null)
            return; // skipped: no throwaway Postgres configured for this run (see class doc)

        await using var db = MakeContext();
        await db.Database.MigrateAsync();

        // Clean slate so a re-run against the same throwaway instance starts fresh. NOT `workspaces`
        // (or CASCADE): workspaces is referenced by audit_events, whose append-only trigger (DB-12)
        // refuses a TRUNCATE reaching it via cascade. Fresh random GUIDs each run make re-seeding
        // workspaces unnecessary.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE usage_events, usage_daily, app_settings");

        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        db.Workspaces.AddRange(
            new Workspace
            {
                Id = ownerA,
                Name = "A",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            },
            new Workspace
            {
                Id = ownerB,
                Name = "B",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();

        var yesterday = DateTime.UtcNow.AddDays(-1);
        db.UsageEvents.AddRange(
            new UsageEvent
            {
                Type = "installed",
                Source = "test",
                OwnerId = ownerA,
                CreatedAt = yesterday,
            },
            new UsageEvent
            {
                Type = "installed",
                Source = "test",
                OwnerId = ownerA,
                CreatedAt = yesterday,
            },
            new UsageEvent
            {
                Type = "installed",
                Source = "test",
                OwnerId = ownerB,
                CreatedAt = yesterday,
            }
        );
        await db.SaveChangesAsync();

        var options = new RetentionOptions
        {
            Enabled = true,
            UsageEventsDays = 180,
            RollupDays = 3,
            BatchSize = 5000,
        };

        // Pre-fix, this line throws Npgsql.PostgresException ("22007: timestamp/timestamptz cannot
        // have Unspecified kind" / "Cannot write DateTime with Kind=Unspecified to PostgreSQL type
        // 'timestamp with time zone'") — Sqlite is Kind-agnostic and would never surface this.
        await UsageRollup.RollupAsync(
            db,
            options,
            DateTime.UtcNow,
            NullLogger.Instance,
            CancellationToken.None
        );

        var rowsAfterFirst = await db
            .UsageDaily.IgnoreQueryFilters()
            .OrderBy(r => r.OwnerId)
            .ToListAsync();
        Assert.Equal(2, rowsAfterFirst.Count);
        Assert.Equal(2, rowsAfterFirst.Single(r => r.OwnerId == ownerA).Count);
        Assert.Equal(1, rowsAfterFirst.Single(r => r.OwnerId == ownerB).Count);

        // Re-run against the SAME real Postgres: idempotent — same rows, same counts.
        await UsageRollup.RollupAsync(
            db,
            options,
            DateTime.UtcNow.AddMinutes(5),
            NullLogger.Instance,
            CancellationToken.None
        );
        var rowsAfterSecond = await db
            .UsageDaily.IgnoreQueryFilters()
            .OrderBy(r => r.OwnerId)
            .ToListAsync();
        Assert.Equal(2, rowsAfterSecond.Count);
        Assert.Equal(2, rowsAfterSecond.Single(r => r.OwnerId == ownerA).Count);
        Assert.Equal(1, rowsAfterSecond.Single(r => r.OwnerId == ownerB).Count);
    }
}
