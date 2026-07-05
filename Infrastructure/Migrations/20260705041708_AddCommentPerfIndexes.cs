using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCommentPerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_comments_owner_id_created_at",
                table: "comments",
                columns: new[] { "owner_id", "created_at" },
                filter: "deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_comments_project_id_created_at",
                table: "comments",
                columns: new[] { "project_id", "created_at" },
                filter: "deleted_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_comments_owner_id_created_at",
                table: "comments");

            migrationBuilder.DropIndex(
                name: "IX_comments_project_id_created_at",
                table: "comments");
        }
    }
}
