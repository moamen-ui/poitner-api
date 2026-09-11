using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantInviteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProjectId1",
                table: "project_app_urls",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "invites",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "plan_id",
                table: "invites",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "invites");

            migrationBuilder.DropColumn(
                name: "plan_id",
                table: "invites");
        }
    }
}
