using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SuggestionChangesRequestedAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "admin_feedback",
                table: "predefined_action_suggestions",
                type: "text",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "comment_id",
                table: "notifications",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "suggestion_id",
                table: "notifications",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_suggestion_id",
                table: "notifications",
                column: "suggestion_id");

            migrationBuilder.AddForeignKey(
                name: "FK_notifications_predefined_action_suggestions_suggestion_id",
                table: "notifications",
                column: "suggestion_id",
                principalTable: "predefined_action_suggestions",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_notifications_predefined_action_suggestions_suggestion_id",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "IX_notifications_suggestion_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "admin_feedback",
                table: "predefined_action_suggestions");

            migrationBuilder.DropColumn(
                name: "suggestion_id",
                table: "notifications");

            migrationBuilder.AlterColumn<int>(
                name: "comment_id",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
