using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantRoleNameIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_roles_name",
                table: "roles");

            migrationBuilder.CreateIndex(
                name: "IX_roles_name_owner_id",
                table: "roles",
                columns: new[] { "name", "owner_id" },
                unique: true);

            // Partial unique index for global scope (owner_id IS NULL = super-admin/global rows).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ix_roles_name_global ON roles(name) WHERE owner_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_roles_name_owner_id",
                table: "roles");

            migrationBuilder.CreateIndex(
                name: "IX_roles_name",
                table: "roles",
                column: "name",
                unique: true);

            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_roles_name_global;");
        }
    }
}
