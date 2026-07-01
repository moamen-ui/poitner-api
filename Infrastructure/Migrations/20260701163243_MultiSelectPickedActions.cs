using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MultiSelectPickedActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "picked_action_prompt",
                table: "comments");

            migrationBuilder.DropColumn(
                name: "picked_action_text",
                table: "comments");

            migrationBuilder.AddColumn<string>(
                name: "picked_actions",
                table: "comments",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "picked_actions",
                table: "comments");

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
        }
    }
}
