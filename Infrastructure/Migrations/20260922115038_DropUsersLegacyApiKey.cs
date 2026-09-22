using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-07")]
    public partial class DropUsersLegacyApiKey : Migration
    {
        // DB-RULES: R2 contract approved 2026-09-22 by Moamen (owner; instruction "choose the best for clean db", relayed by the orchestrator; docs/db/execution/DB-07-drop-users-api-key.md)
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_users_api_key", table: "users");

            migrationBuilder.DropColumn(name: "api_key", table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "api_key",
                table: "users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_users_api_key",
                table: "users",
                column: "api_key",
                unique: true
            );
        }
    }
}
