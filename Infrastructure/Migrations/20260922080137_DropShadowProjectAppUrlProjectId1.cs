using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-04")]
    public partial class DropShadowProjectAppUrlProjectId1 : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R2 contract approved 2026-09-22 by orchestrator (owner instruction "proceed"; column provably all-NULL, see docs/db/execution/DB-04-drop-shadow-projectid1.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_project_app_urls_projects_ProjectId1",
                table: "project_app_urls");

            migrationBuilder.DropIndex(
                name: "IX_project_app_urls_ProjectId1",
                table: "project_app_urls");

            migrationBuilder.DropColumn(
                name: "ProjectId1",
                table: "project_app_urls");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId1",
                table: "project_app_urls",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_ProjectId1",
                table: "project_app_urls",
                column: "ProjectId1");

            migrationBuilder.AddForeignKey(
                name: "FK_project_app_urls_projects_ProjectId1",
                table: "project_app_urls",
                column: "ProjectId1",
                principalTable: "projects",
                principalColumn: "id");
        }
    }
}
