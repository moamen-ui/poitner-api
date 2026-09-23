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
            // workspace with another live member whose OWN home is elsewhere (a demo has exactly
            // one member unless the demo admin minted a quick-access/addressed invite of their own —
            // review finding #3: those members' `owner_id` IS this same workspace, so they do not
            // block tagging; only a member whose `owner_id` is DISTINCT FROM this workspace — i.e.
            // genuinely at home somewhere else — does). Guarded so a second run is a no-op (R3).
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
                                  JOIN users ou ON ou.id = o.user_id
                                  WHERE o.owner_id = w.id AND o.user_id <> u.id AND o.left_at IS NULL AND o.deleted_at IS NULL
                                    AND ou.owner_id IS DISTINCT FROM w.id);  -- reject only a member whose home is elsewhere; a demo-own member (quick-access/invited by the demo admin, home = this workspace) does not block tagging
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversible: the `users` columns still hold the source (dual-write) — this just nulls
            // the copy on unconverted workspaces. Caveat (DB-17 review finding #12): this only
            // reverses what Migration 2 (THIS migration) wrote — `demo_expiry_warned_at` is written
            // later by DemoService.WarnExpiringAsync (§3.5), never by this backfill, but a rollback
            // that stops here (Migration 2 rolled back, Migration 1 — the columns themselves — kept)
            // would otherwise leave a stale warned-at timestamp sitting next to a freshly-renulled
            // demo_expires_at; nulled here too so the two columns cannot disagree after only this
            // migration is undone.
            migrationBuilder.Sql(
                """
                UPDATE workspaces SET demo_expires_at = NULL, demo_extended_at = NULL, demo_comment_cap_override = NULL, demo_ttl_hours_override = NULL, demo_expiry_warned_at = NULL WHERE demo_converted_at IS NULL;
                """
            );
        }
    }
}
