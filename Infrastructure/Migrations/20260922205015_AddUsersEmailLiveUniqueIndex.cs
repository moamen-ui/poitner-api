using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11a")]
    public partial class AddUsersEmailLiveUniqueIndex : Migration
    {
        /// <inheritdoc />
        // DB-RULES: index change approved 2026-09-22 by Moamen (owner; requirement "one identity per e-mail", relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email_live ON users (lower(email)) WHERE deleted_at IS NULL;"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_users_email_live;");
        }
    }
}
