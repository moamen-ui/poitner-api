using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    // DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; D14.1 "existing identities are
    // grandfathered as verified", relayed by the orchestrator; docs/db/execution/DB-14-email-verification-and-password-policy.md)
    [ContractMigration("DB-14")]
    public partial class BackfillUsersEmailVerifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DB-14 D14.1: every identity that exists before this release is grandfathered. The
            // literal is the cut-off written by the implementer on the day the migration is authored
            // (UTC midnight of that day); rows created after it are never touched, so a second run is
            // a no-op (R3 idempotent).
            // Must be deployed before 2026-09-24T00:00Z; otherwise ship BackfillUsersEmailVerifiedAt2
            // with a later literal — never edit this file after merge (R10).
            migrationBuilder.Sql(
                """
                UPDATE users SET email_verified_at = created_at
                WHERE email_verified_at IS NULL AND deleted_at IS NULL AND created_at < TIMESTAMPTZ '2026-09-24 00:00:00+00';
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No inverse: grandfathering is a one-time fact (DB-14 §8).
        }
    }
}
