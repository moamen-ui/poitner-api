using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    // DB-RULES: R3 backfill approved 2026-09-23 by Moamen (owner; F4 + D17.5 confirmed 2026-09-23, relayed by the orchestrator; docs/db/execution/DB-17-demo-as-product.md)
    [ContractMigration("DB-17")]
    public partial class BackfillWorkspacesDemoState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DB-17 §3.2: copies the TTL/extension/overrides of every LIVE demo from its admin
            // identity to the workspace that was provisioned for that identity — and no other.
            // Keys on the identity's OWN workspace, matched below (DemoService.ProvisionAsync sets
            // it at creation; R8.7 is suspended for this one statement, exactly as
            // TenantService.HardDeleteAsync reads the same column for the same one-shot "created
            // here" question), requires a live Workspace Admin membership, and refuses any
            // workspace with another live member (a demo workspace has exactly one). Guarded so a
            // second run is a no-op (R3).
            // Implementer's note (deviation from the doc's literal §3.2 SQL, proven on Postgres
            // 15 during R11): the membership-to-workspace join predicate cannot sit in the JOIN's
            // own ON clause — Postgres resolves an UPDATE ... FROM's join list before the UPDATE
            // target's alias is in scope, so the target is not yet visible there ("invalid
            // reference to FROM-clause entry for table w"). Moved to the WHERE clause below
            // instead, where the target alias already is — logically identical, since the
            // membership's workspace and the identity's own workspace are already tied together
            // one line down.
            migrationBuilder.Sql(
                """
                UPDATE workspaces w
                SET demo_expires_at           = u.expires_at,
                    demo_extended_at          = CASE WHEN u."DemoExtended" THEN COALESCE(u.updated_at, u.created_at) END,
                    demo_comment_cap_override = u."DemoCommentCapOverride",
                    demo_ttl_hours_override   = u."DemoTtlHoursOverride"
                FROM users u
                JOIN workspace_memberships m ON m.user_id = u.id AND m.left_at IS NULL AND m.deleted_at IS NULL
                JOIN roles r ON r.id = m.role_id AND r.name = 'Workspace Admin'
                WHERE u.is_demo = true
                  AND u.owner_id = w.id                                   -- the identity's OWN workspace (DemoService.ProvisionAsync :132), never a workspace it was invited into
                  AND m.owner_id = w.id
                  AND u.deleted_at IS NULL
                  AND u.expires_at IS NOT NULL
                  AND w.deleted_at IS NULL
                  AND w.demo_expires_at IS NULL
                  AND w.demo_converted_at IS NULL
                  AND NOT EXISTS (SELECT 1 FROM workspace_memberships o
                                  WHERE o.owner_id = w.id AND o.user_id <> u.id AND o.left_at IS NULL AND o.deleted_at IS NULL);  -- a demo has exactly one member
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible: the `users` columns still hold the source (dual-write) — this just nulls
            // the copy on unconverted workspaces.
            migrationBuilder.Sql(
                """
                UPDATE workspaces SET demo_expires_at = NULL, demo_extended_at = NULL, demo_comment_cap_override = NULL, demo_ttl_hours_override = NULL WHERE demo_converted_at IS NULL;
                """
            );
        }
    }
}
