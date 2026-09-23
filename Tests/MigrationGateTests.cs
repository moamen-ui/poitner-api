using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pointer.API.Startup;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Migrations;

/// <summary>
/// DB-09: unit tests for <see cref="MigrationGate.FindContractMigrations"/> (pure) plus two
/// real-assembly checks that the [ContractMigration] attribute is visible through EF's migrations
/// discovery for an actual migration class. <see cref="MigrationGate.AllowMigrateAsync"/> needs a
/// live Postgres and is covered by the DB-09 rehearsal (docs/db/execution/DB-09-migration-apply-gate.md
/// §7 criteria 6-8) and by DB-10's CI job.
/// </summary>
public class MigrationGateTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
    }

    [ContractMigration("T")]
    private sealed class MarkedMigration { }

    private sealed class PlainMigration { }

    private static AppDbContext BuildContext() =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Port=1;Database=gate;Username=x;Password=x")
                .Options,
            new FakeCurrentUser(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    [Fact]
    public void FindContractMigrations_ReturnsOnlyMarkedPendingIds_InOrder()
    {
        var migrations = new Dictionary<string, TypeInfo>
        {
            ["20990101000000_Marked"] = typeof(MarkedMigration).GetTypeInfo(),
            ["20990101000001_Plain"] = typeof(PlainMigration).GetTypeInfo(),
        };

        var pending = new[]
        {
            "20990101000002_Unknown",
            "20990101000001_Plain",
            "20990101000000_Marked",
        };

        var result = MigrationGate.FindContractMigrations(pending, migrations);

        Assert.Equal(new[] { "20990101000000_Marked" }, result);
    }

    [Fact]
    public void FindContractMigrations_EmptyPending_ReturnsEmpty()
    {
        var migrations = new Dictionary<string, TypeInfo>
        {
            ["20990101000000_Marked"] = typeof(MarkedMigration).GetTypeInfo(),
        };

        var result = MigrationGate.FindContractMigrations(Array.Empty<string>(), migrations);

        Assert.Empty(result);
    }

    [Fact]
    public void RealAssembly_DropShadowProjectId1_IsMarked()
    {
        using var db = BuildContext();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations;
        const string id = "20260922080137_DropShadowProjectAppUrlProjectId1";

        Assert.True(
            migrations.TryGetValue(id, out var type),
            $"{id} not found in migrations assembly"
        );
        var attr = type!.GetCustomAttribute<ContractMigrationAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("DB-04", attr!.Doc);

        var result = MigrationGate.FindContractMigrations(
            new[] { id, "20260623133436_InitialCreate" },
            migrations
        );
        Assert.Equal(new[] { id }, result);
    }

    [Fact]
    public void RealAssembly_EveryBaselineMigration_IsUnmarked()
    {
        using var db = BuildContext();
        var migrations = db.GetService<IMigrationsAssembly>().Migrations;

        foreach (var id in MigrationSafetyTests.Baseline)
        {
            Assert.True(
                migrations.TryGetValue(id, out var type),
                $"{id} not found in migrations assembly"
            );
            Assert.Null(type!.GetCustomAttribute<ContractMigrationAttribute>());
        }
    }
}
