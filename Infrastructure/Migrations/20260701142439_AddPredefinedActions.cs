using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPredefinedActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "picked_action_prompt",
                table: "comments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "picked_action_text",
                table: "comments",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "predefined_actions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    project_id = table.Column<int>(type: "integer", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_predefined_actions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_predefined_actions_owner_id",
                table: "predefined_actions",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_predefined_actions_owner_id_project_id",
                table: "predefined_actions",
                columns: new[] { "owner_id", "project_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "predefined_actions");

            migrationBuilder.DropColumn(
                name: "picked_action_prompt",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "picked_action_text",
                table: "comments");
        }
    }
}
