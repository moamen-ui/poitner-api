using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-03")]
    public partial class AddWorkspaces : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; decisions Q2=(b) abort on orphan, Q3=workspace name is its own attribute, relayed by the orchestrator; docs/db/execution/DB-03-workspaces-table.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(
                        type: "character varying(120)",
                        maxLength: 120,
                        nullable: false
                    ),
                    created_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: false
                    ),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    deleted_at = table.Column<DateTime>(
                        type: "timestamp with time zone",
                        nullable: true
                    ),
                    deleted_by = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workspaces", x => x.id);
                    table.CheckConstraint(
                        "ck_workspaces_name_not_blank",
                        "length(btrim(name)) > 0"
                    );
                }
            );

            // 3a. one row per workspace that has, or ever had, a Workspace Admin. DISTINCT ON keeps one
            //     candidate per owner_id: a live admin before a soft-deleted one, then the earliest created.
            //     A workspace whose admins are ALL soft-deleted still gets its row (GLM B5): it is a
            //     reachable tenant with no live admin, not an orphan. Name = placeholder (Q3).
            migrationBuilder.Sql(
                @"
INSERT INTO workspaces (id, name, created_at, created_by)
SELECT DISTINCT ON (u.owner_id) u.owner_id, 'Workspace', u.created_at, u.public_id
FROM users u JOIN roles r ON r.id = u.role_id
WHERE r.name = 'Workspace Admin' AND u.owner_id IS NOT NULL
ORDER BY u.owner_id, (u.deleted_at IS NOT NULL), u.created_at
ON CONFLICT (id) DO NOTHING;
"
            );

            // 3b. Q2 = (b): ABORT if any owner_id anywhere still has no workspaces row. RAISE inside the
            //     migration's transaction rolls back 3a and the CreateTable; Migration 2 never runs.
            migrationBuilder.Sql(
                @"
DO $$
DECLARE orphans text;
BEGIN
  SELECT string_agg(o.owner_id::text, ', ' ORDER BY o.owner_id::text) INTO orphans
  FROM (
    SELECT owner_id FROM ai_rules UNION SELECT owner_id FROM api_keys UNION SELECT owner_id FROM app_environments
    UNION SELECT owner_id FROM comments UNION SELECT owner_id FROM device_logins UNION SELECT owner_id FROM extension_sites
    UNION SELECT owner_id FROM invites UNION SELECT owner_id FROM notifications UNION SELECT owner_id FROM page_context_snapshots
    UNION SELECT owner_id FROM predefined_action_suggestions UNION SELECT owner_id FROM predefined_actions
    UNION SELECT owner_id FROM project_app_urls UNION SELECT owner_id FROM project_builds UNION SELECT owner_id FROM projects
    UNION SELECT owner_id FROM quick_access_links UNION SELECT owner_id FROM replies UNION SELECT owner_id FROM role_tenant_overrides
    UNION SELECT owner_id FROM roles UNION SELECT owner_id FROM status_presentations UNION SELECT owner_id FROM subscriptions
    UNION SELECT owner_id FROM usage_events UNION SELECT owner_id FROM users UNION SELECT owner_id FROM workspace_settings
  ) o
  WHERE o.owner_id IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = o.owner_id);
  IF orphans IS NOT NULL THEN
    RAISE EXCEPTION 'DB-03 ABORT: owner_id value(s) with no Workspace Admin user (live or soft-deleted): %. Transaction rolled back; the database is unchanged. Re-run the prod pre-check in docs/db/execution/DB-03-workspaces-table.md section 9 step 1 and report the ids to the owner.', orphans;
  END IF;
END $$;
"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "workspaces");
        }
    }
}
