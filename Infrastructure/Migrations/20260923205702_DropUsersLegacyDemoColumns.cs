using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11e")]
    public partial class DropUsersLegacyDemoColumns : Migration
    {
        // DB-RULES: R2 contract approved 2026-09-23 by Moamen (owner, verbatim: "Approved to drop users.expires_at, IX_users_expires_at, users."DemoExtended", users."DemoCommentCapOverride", users."DemoTtlHoursOverride" and the int tenant routes (DB-11e), 2026-09-23."; docs/db/execution/DB-11e-drop-legacy-demo-state.md)
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_users_expires_at", table: "users");

            migrationBuilder.DropColumn(name: "DemoCommentCapOverride", table: "users");

            migrationBuilder.DropColumn(name: "DemoExtended", table: "users");

            migrationBuilder.DropColumn(name: "DemoTtlHoursOverride", table: "users");

            migrationBuilder.DropColumn(name: "expires_at", table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemoCommentCapOverride",
                table: "users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "DemoExtended",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "DemoTtlHoursOverride",
                table: "users",
                type: "integer",
                nullable: true
            );

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_users_expires_at",
                table: "users",
                column: "expires_at"
            );
        }
    }
}
