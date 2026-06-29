using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenancy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_status_presentations_status_value",
                table: "status_presentations");

            migrationBuilder.DropIndex(
                name: "IX_projects_key",
                table: "projects");

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "status_presentations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_super_admin",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "roles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "replies",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "projects",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_id",
                table: "comments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "app_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_settings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_users_email_owner_id",
                table: "users",
                columns: new[] { "email", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_owner_id",
                table: "users",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_status_presentations_owner_id",
                table: "status_presentations",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_status_presentations_status_value_owner_id",
                table: "status_presentations",
                columns: new[] { "status_value", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_roles_owner_id",
                table: "roles",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_replies_owner_id",
                table: "replies",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_projects_key_owner_id",
                table: "projects",
                columns: new[] { "key", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_owner_id",
                table: "projects",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_comments_owner_id",
                table: "comments",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_app_settings_key",
                table: "app_settings",
                column: "key",
                unique: true);

            // Partial unique indexes for global scope (owner_id IS NULL = super-admin/global rows).
            // Postgres treats NULLs as distinct in unique indexes, so without these, multiple
            // global rows with the same key could coexist; these partial indexes prevent that.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_projects_key_global ON projects(key) WHERE owner_id IS NULL;");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_status_presentations_status_value_global ON status_presentations(status_value) WHERE owner_id IS NULL;");
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_users_email_global ON users(email) WHERE owner_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_settings");

            migrationBuilder.DropIndex(
                name: "IX_users_email_owner_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_owner_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_status_presentations_owner_id",
                table: "status_presentations");

            migrationBuilder.DropIndex(
                name: "IX_status_presentations_status_value_owner_id",
                table: "status_presentations");

            migrationBuilder.DropIndex(
                name: "IX_roles_owner_id",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_replies_owner_id",
                table: "replies");

            migrationBuilder.DropIndex(
                name: "IX_projects_key_owner_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_projects_owner_id",
                table: "projects");

            migrationBuilder.DropIndex(
                name: "IX_comments_owner_id",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "status_presentations");

            migrationBuilder.DropColumn(
                name: "is_super_admin",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "replies");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "owner_id",
                table: "comments");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_status_presentations_status_value",
                table: "status_presentations",
                column: "status_value",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_projects_key",
                table: "projects",
                column: "key",
                unique: true);

            // Drop partial unique indexes for global scope
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_projects_key_global;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_status_presentations_status_value_global;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_email_global;");
        }
    }
}
