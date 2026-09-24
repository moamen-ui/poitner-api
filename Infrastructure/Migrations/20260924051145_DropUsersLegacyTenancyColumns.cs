using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11f")]
    public partial class DropUsersLegacyTenancyColumns : Migration
    {
        // DB-RULES: R2 contract approved 2026-09-24 by Moamen (owner, verbatim: "Approved to drop users.owner_id, users.approval_status, ux_users_email_owner_live, IX_users_owner_id, fk_users_workspaces_owner_id and make users.role_id super-admin-only (DB-11f Part B), 2026-09-24."; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "fk_users_workspaces_owner_id", table: "users");

            migrationBuilder.DropIndex(name: "IX_users_owner_id", table: "users");

            migrationBuilder.DropIndex(name: "ux_users_email_owner_live", table: "users");

            migrationBuilder.DropColumn(name: "approval_status", table: "users");

            migrationBuilder.DropColumn(name: "owner_id", table: "users");

            migrationBuilder.AlterColumn<int>(
                name: "role_id",
                table: "users",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "role_id",
                table: "users",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "approval_status",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 1
            );

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "users",
                type: "uuid",
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_users_owner_id",
                table: "users",
                column: "owner_id"
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_users_email_owner_live",
                    table: "users",
                    columns: new[] { "email", "owner_id" },
                    unique: true,
                    filter: "deleted_at IS NULL"
                )
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "fk_users_workspaces_owner_id",
                table: "users",
                column: "owner_id",
                principalTable: "workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict
            );
        }
    }
}
