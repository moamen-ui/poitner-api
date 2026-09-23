using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddImpersonationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "impersonation_sessions",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operator_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(
                        type: "character varying(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    started_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    expires_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    ended_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    end_reason = table.Column<int>(type: "integer", nullable: true),
                    request_count = table.Column<int>(
                        type: "integer",
                        nullable: false,
                        defaultValue: 0
                    ),
                    last_request_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_impersonation_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_impersonation_sessions_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_impersonation_sessions_owner_started",
                table: "impersonation_sessions",
                columns: new[] { "owner_id", "started_at" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ux_impersonation_sessions_operator_live",
                table: "impersonation_sessions",
                column: "operator_user_id",
                unique: true,
                filter: "ended_at IS NULL"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "impersonation_sessions");
        }
    }
}
