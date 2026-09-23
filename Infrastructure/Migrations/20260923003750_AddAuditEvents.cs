using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    occurred_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_membership_id = table.Column<int>(type: "integer", nullable: true),
                    actor_kind = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    target_type = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: false
                    ),
                    target_id = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    before = table.Column<string>(
                        type: "jsonb",
                        nullable: false,
                        defaultValueSql: "'{}'"
                    ),
                    after = table.Column<string>(
                        type: "jsonb",
                        nullable: false,
                        defaultValueSql: "'{}'"
                    ),
                    request_id = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: true
                    ),
                    ip_hash = table.Column<string>(
                        type: "character varying(64)",
                        maxLength: 64,
                        nullable: true
                    ),
                    user_agent = table.Column<string>(
                        type: "character varying(256)",
                        maxLength: 256,
                        nullable: true
                    ),
                    impersonation_session_id = table.Column<long>(type: "bigint", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_occurred",
                table: "audit_events",
                columns: new[] { "actor_user_id", "occurred_at" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_owner_occurred",
                table: "audit_events",
                columns: new[] { "owner_id", "occurred_at" },
                descending: new[] { false, true }
            );

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_target",
                table: "audit_events",
                columns: new[] { "target_type", "target_id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "audit_events");
        }
    }
}
