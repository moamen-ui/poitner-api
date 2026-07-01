using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPredefinedActionSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "predefined_action_suggestions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    prompt = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_predefined_action_suggestions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_predefined_action_suggestions_owner_id_status",
                table: "predefined_action_suggestions",
                columns: new[] { "owner_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "predefined_action_suggestions");
        }
    }
}
