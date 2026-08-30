using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppEnvironmentsAndProjectAppUrls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_environments",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_environments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "project_app_urls",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    app_environment_id = table.Column<int>(type: "integer", nullable: false),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_project_app_urls", x => x.id);
                    table.ForeignKey(
                        name: "FK_project_app_urls_app_environments_app_environment_id",
                        column: x => x.app_environment_id,
                        principalTable: "app_environments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_project_app_urls_projects_project_id",
                        column: x => x.project_id,
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_environments_name_owner_id",
                table: "app_environments",
                columns: new[] { "name", "owner_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_environments_owner_id",
                table: "app_environments",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_app_environment_id",
                table: "project_app_urls",
                column: "app_environment_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_owner_id",
                table: "project_app_urls",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_project_id_app_environment_id",
                table: "project_app_urls",
                columns: new[] { "project_id", "app_environment_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "project_app_urls");

            migrationBuilder.DropTable(
                name: "app_environments");
        }
    }
}
