using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAppEnvironmentEnabledFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_enabled",
                table: "app_environments",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_retired",
                table: "app_environments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_enabled",
                table: "app_environments");

            migrationBuilder.DropColumn(
                name: "is_retired",
                table: "app_environments");
        }
    }
}
