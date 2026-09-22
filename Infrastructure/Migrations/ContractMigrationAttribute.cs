namespace Pointer.Infrastructure.Migrations;

/// <summary>
/// Marks a migration that must never auto-apply on an ordinary API boot (DB-RULES R7). Every
/// migration that carries a <c>// DB-RULES: … approved …</c> marker (R2 contract, R3 backfill,
/// index change, R4 constraint — i.e. anything the DB-02 guard flags) also carries this attribute;
/// <c>Tests/MigrationSafetyTests</c> requires the two to agree. <c>API/Startup/MigrationGate</c>
/// refuses <c>MigrateAsync()</c> while such a migration is pending unless
/// <c>DBApplyContractMigrations=true</c>, which only <c>scripts/deploy-api.sh</c> sets, and only on
/// its <c>POINTER_APPLY_CONTRACT=1</c> path (API stopped, labelled dump taken).
/// </summary>
/// <param name="doc">The execution doc that approved it, e.g. <c>"DB-07"</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ContractMigrationAttribute(string doc) : Attribute
{
    public string Doc { get; } = doc;
}
