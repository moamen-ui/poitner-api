using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProjectPerEnvironmentActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add the 3 new columns FIRST (still alongside the old is_active), backfill them from
            // it, THEN drop is_active — so existing projects carry their current state forward
            // instead of every one resetting to "fully active" or "fully inactive".
            migrationBuilder.AddColumn<bool>(
                name: "is_active_local",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active_production",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_active_staging",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                "UPDATE projects SET is_active_local = is_active, is_active_staging = is_active, is_active_production = is_active;");

            migrationBuilder.DropColumn(
                name: "is_active",
                table: "projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_active",
                table: "projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // "Fully inactive" (old semantics) = disabled in every environment; any environment
            // still active reverses to is_active = true.
            migrationBuilder.Sql(
                "UPDATE projects SET is_active = (is_active_local OR is_active_staging OR is_active_production);");

            migrationBuilder.DropColumn(
                name: "is_active_local",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "is_active_production",
                table: "projects");

            migrationBuilder.DropColumn(
                name: "is_active_staging",
                table: "projects");
        }
    }
}
