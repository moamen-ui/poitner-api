using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoTenantConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DemoCommentCapOverride",
                table: "users",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DemoExtended",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DemoTtlHoursOverride",
                table: "users",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DemoCommentCapOverride",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DemoExtended",
                table: "users");

            migrationBuilder.DropColumn(
                name: "DemoTtlHoursOverride",
                table: "users");
        }
    }
}
