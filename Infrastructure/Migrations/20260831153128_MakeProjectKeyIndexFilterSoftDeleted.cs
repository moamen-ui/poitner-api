using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeProjectKeyIndexFilterSoftDeleted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_projects_key_owner_id",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "IX_projects_key_owner_id",
                table: "projects",
                columns: new[] { "key", "owner_id" },
                unique: true,
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_projects_key_owner_id",
                table: "projects");

            migrationBuilder.CreateIndex(
                name: "IX_projects_key_owner_id",
                table: "projects",
                columns: new[] { "key", "owner_id" },
                unique: true);
        }
    }
}
