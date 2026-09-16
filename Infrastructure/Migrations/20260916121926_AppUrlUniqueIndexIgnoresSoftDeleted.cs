using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AppUrlUniqueIndexIgnoresSoftDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_project_app_urls_project_id_app_environment_id",
                table: "project_app_urls");

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_project_id_app_environment_id",
                table: "project_app_urls",
                columns: new[] { "project_id", "app_environment_id" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_project_app_urls_project_id_app_environment_id",
                table: "project_app_urls");

            migrationBuilder.CreateIndex(
                name: "IX_project_app_urls_project_id_app_environment_id",
                table: "project_app_urls",
                columns: new[] { "project_id", "app_environment_id" },
                unique: true);
        }
    }
}
