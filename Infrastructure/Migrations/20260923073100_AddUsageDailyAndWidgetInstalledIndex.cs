using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageDailyAndWidgetInstalledIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE (DB-15 implementer): the scaffold also emitted AddColumn users.erased_at here
            // because 20260923045038_AddImpersonationSessions' Designer file predates DB-11c's
            // model (0 references to erased_at at HEAD — a rebase leftover, pre-existing on this
            // branch). The column is created by 20260923040830_AddUsersErasedAt itself, so the
            // emitted AddColumn would fail against any database that already applied that chain
            // (e.g. production). Both extraneous operations (AddColumn/Up, DropColumn/Down) were
            // removed by hand; this migration's Designer carries the full current model, so the
            // stale-Designer gap closes here and future scaffolds diff correctly.

            migrationBuilder.CreateTable(
                name: "usage_daily",
                columns: table => new
                {
                    id = table
                        .Column<long>(type: "bigint", nullable: false)
                        .Annotation(
                            "Npgsql:ValueGenerationStrategy",
                            NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                        ),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type = table.Column<string>(
                        type: "character varying(40)",
                        maxLength: 40,
                        nullable: false
                    ),
                    count = table.Column<int>(type: "integer", nullable: false),
                    computed_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_daily", x => x.id);
                    table.ForeignKey(
                        name: "fk_usage_daily_workspaces_owner_id",
                        column: x => x.owner_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "ux_usage_events_widget_installed_per_project",
                table: "usage_events",
                columns: new[] { "type", "project_id" },
                unique: true,
                filter: "type = 'widget_installed'"
            );

            migrationBuilder.CreateIndex(
                name: "ix_usage_daily_owner_day",
                table: "usage_daily",
                columns: new[] { "owner_id", "day" }
            );

            migrationBuilder
                .CreateIndex(
                    name: "ux_usage_daily_day_owner_type",
                    table: "usage_daily",
                    columns: new[] { "day", "owner_id", "type" },
                    unique: true
                )
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "usage_daily");

            migrationBuilder.DropIndex(
                name: "ux_usage_events_widget_installed_per_project",
                table: "usage_events"
            );
        }
    }
}
