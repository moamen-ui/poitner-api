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
            // Not the original values (legacy copies of the creation role; only the pre-db11f dump holds
            // them). Refills every NULL so B-1's Down() can restore NOT NULL, with the least-privilege
            // global role ONLY — never a membership's role, which may be the platform role or another
            // workspace's (cross-review Opus MEDIUM/LOW: privilege escalation). Nothing in the Part A code
            // this rolls back to reads users.role_id for a non-super-admin. P9 proves the role exists.
            migrationBuilder.Sql(
                "UPDATE users SET role_id = (SELECT r.id FROM roles r WHERE r.owner_id IS NULL "
                    + "AND r.deleted_at IS NULL AND NOT r.is_super_admin AND NOT r.grants_admin "
                    + "AND NOT r.quick_access ORDER BY r.id LIMIT 1) WHERE role_id IS NULL;"
            );
        }
    }
}
