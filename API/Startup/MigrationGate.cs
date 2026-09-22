using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Migrations;

namespace Pointer.API.Startup;

/// <summary>
/// DB-09 (DB-RULES R7): migrations marked [ContractMigration] never auto-apply on an ordinary boot.
/// Runs before MigrateAsync. Pure part (FindContractMigrations) is unit-tested; the async part is
/// exercised by the R11 rehearsal and by scripts/deploy-api.sh.
/// </summary>
public static class MigrationGate
{
    public const string ConfigKey = "DBApplyContractMigrations";

    /// <summary>Of the pending ids, those whose migration class carries [ContractMigration].</summary>
    public static IReadOnlyList<string> FindContractMigrations(
        IEnumerable<string> pendingIds,
        IReadOnlyDictionary<string, TypeInfo> migrations
    )
    {
        return pendingIds
            .Where(id =>
                migrations.TryGetValue(id, out var type)
                && type.GetCustomAttribute<ContractMigrationAttribute>() is not null
            )
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True = boot may continue into MigrateAsync. False = refuse; caller exits non-zero.</summary>
    public static async Task<bool> AllowMigrateAsync(
        AppDbContext db,
        IConfiguration config,
        ILogger logger,
        CancellationToken ct = default
    )
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0)
            return true;

        var marked = FindContractMigrations(
            pending,
            db.GetService<IMigrationsAssembly>().Migrations
        );
        if (marked.Count == 0)
            return true;

        var ids = string.Join(", ", marked);
        if (config.GetValue<bool>(ConfigKey))
        {
            logger.LogWarning(
                "DB-09: applying {Count} contract migration(s) because {Key}=true: {Ids}",
                marked.Count,
                ConfigKey,
                ids
            );
            return true;
        }

        logger.LogCritical(
            "DB-09 REFUSED: {Count} pending migration(s) carry [ContractMigration] and {Key} is not true: {Ids}. "
                + "Not migrating; exiting. Ship them with: POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> "
                + "bash scripts/deploy-api.sh (docs/db/DB-RULES.md R7, docs/db/execution/DB-09-migration-apply-gate.md).",
            marked.Count,
            ConfigKey,
            ids
        );
        return false;
    }
}
