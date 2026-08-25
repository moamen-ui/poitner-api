using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPageContextCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "page_context_capture_enabled",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_bug_report",
                table: "comments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "page_context_snapshot_id",
                table: "comments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "page_context_snapshots",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    environment = table.Column<int>(type: "integer", nullable: false),
                    route = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    session_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_event_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    console_entries = table.Column<string>(type: "jsonb", nullable: true),
                    network_entries = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_page_context_snapshots", x => x.id);
                    table.ForeignKey(
                        name: "FK_page_context_snapshots_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_comments_page_context_snapshot_id",
                table: "comments",
                column: "page_context_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "IX_page_context_snapshots_owner_id",
                table: "page_context_snapshots",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_page_context_snapshots_project_id_route_environment_session~",
                table: "page_context_snapshots",
                columns: new[] { "project_id", "route", "environment", "session_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_comments_page_context_snapshots_page_context_snapshot_id",
                table: "comments",
                column: "page_context_snapshot_id",
                principalTable: "page_context_snapshots",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_comments_page_context_snapshots_page_context_snapshot_id",
                table: "comments");

            migrationBuilder.DropTable(
                name: "page_context_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_comments_page_context_snapshot_id",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "page_context_capture_enabled",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "is_bug_report",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "page_context_snapshot_id",
                table: "comments");
        }
    }
}
