using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11f")]
    public partial class ClearUsersRoleIdForMembers : Migration
    {
        // DB-RULES: R2 contract approved 2026-09-24 by Moamen (owner, verbatim: "Approved to drop users.owner_id, users.approval_status, ux_users_email_owner_live, IX_users_owner_id, fk_users_workspaces_owner_id and make users.role_id super-admin-only (DB-11f Part B), 2026-09-24."; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DB-11f §3.4: users.role_id is the PLATFORM role — kept only for super admins. Idempotent.
            migrationBuilder.Sql(
                "UPDATE users SET role_id = NULL WHERE role_id IS NOT NULL "
                    + "AND role_id NOT IN (SELECT id FROM roles WHERE is_super_admin);"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Review fix (Opus MEDIUM / Gemini G6): the guard block below runs BEFORE the refill
            // UPDATE that follows it (one migrationBuilder call, so Npgsql executes them as one batch
            // in order). Without it, if no least-privilege global role exists the refill UPDATE would
            // silently no-op (0 rows matched, no error) — every member's role_id stays NULL, and
            // B-1's Down() then fails setting role_id NOT NULL on a column that still has NULLs,
            // leaving the rollback half-applied (B-2 rolled back, B-1 not). P9 (§9, both Part A and
            // Part B) is supposed to guarantee this role exists before Part B ever ships, but this
            // makes the migration itself refuse to proceed rather than fail confusingly one step
            // later. Not the refilled values' original values either way (legacy copies of the
            // creation role; only the pre-db11f dump holds them) — the refill uses the
            // least-privilege global role ONLY, never a membership's role, which may be the platform
            // role or another workspace's (cross-review Opus MEDIUM/LOW: privilege escalation).
            // Nothing in the Part A code this rolls back to reads users.role_id for a non-super-admin.
            migrationBuilder.Sql(
                "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM roles r WHERE r.owner_id IS NULL "
                    + "AND r.deleted_at IS NULL AND NOT r.is_super_admin AND NOT r.grants_admin "
                    + "AND NOT r.quick_access) THEN RAISE EXCEPTION "
                    + "'DB-11f: no least-privilege global role — cannot roll back B-2'; END IF; END $$;\n"
                    + "UPDATE users SET role_id = (SELECT r.id FROM roles r WHERE r.owner_id IS NULL "
                    + "AND r.deleted_at IS NULL AND NOT r.is_super_admin AND NOT r.grants_admin "
                    + "AND NOT r.quick_access ORDER BY r.id LIMIT 1) WHERE role_id IS NULL;"
            );
        }
    }
}
