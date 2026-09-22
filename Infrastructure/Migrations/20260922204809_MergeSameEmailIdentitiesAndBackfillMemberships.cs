using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    [ContractMigration("DB-11a")]
    public partial class MergeSameEmailIdentitiesAndBackfillMemberships : Migration
    {
        /// <inheritdoc />
        // DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; decisions D1 newest password wins, D2 admin-preferring canonical, relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2.1 Who merges into whom. Only LIVE rows (deleted_at IS NULL) are considered. Canonical per
            //     lower(email): F2 (DB-11a cross-review) — a super-admin row first (never merge the
            //     platform super admin away; if a super admin genuinely shares an address with a tenant
            //     row, the tenant row folds INTO the super admin rather than the reverse), then a
            //     'Workspace Admin' row, then a Deputy, then the oldest row (D2). F2 also excludes any
            //     super-admin row (owner_id IS NULL and/or Role.IsSuperAdmin) from ever being the LOSING
            //     ("u"/merged) side — the platform super admin can never be soft-deleted by this merge,
            //     even if it is not picked as canonical for some other reason.
            //     Empty when the census (§9 step 1) shows no duplicates — every later block is then a no-op.
            migrationBuilder.Sql(
                @"
CREATE TEMP TABLE db11_merge AS
SELECT u.id AS merged_id, u.public_id AS merged_public_id, u.owner_id AS merged_owner_id,
       c.id AS canonical_id
FROM users u
JOIN roles ur ON ur.id = u.role_id
JOIN LATERAL (
  SELECT c.id
  FROM users c JOIN roles r ON r.id = c.role_id
  WHERE c.deleted_at IS NULL AND lower(c.email) = lower(u.email)
  ORDER BY (r.is_super_admin) DESC, (r.name = 'Workspace Admin') DESC, (r.name = 'Workspace Admin Deputy') DESC, c.created_at, c.id
  LIMIT 1
) c ON true
WHERE u.deleted_at IS NULL AND u.id <> c.id
  AND u.owner_id IS NOT NULL AND NOT ur.is_super_admin;
"
            );

            // 2.2 D1: the password of the most recently written duplicate wins; the canonical row's sessions
            //     are revoked (new stamp). A passwordless (magic-link-only) row never donates its hash.
            migrationBuilder.Sql(
                @"
UPDATE users c
SET password_hash = n.password_hash, passwordless_only = false, security_stamp = gen_random_uuid()
FROM (
  SELECT DISTINCT ON (m.canonical_id) m.canonical_id, x.password_hash
  FROM (SELECT DISTINCT canonical_id FROM db11_merge) m
  JOIN users k ON k.id = m.canonical_id
  JOIN users x ON x.deleted_at IS NULL AND lower(x.email) = lower(k.email) AND NOT x.passwordless_only
  ORDER BY m.canonical_id, coalesce(x.updated_at, x.created_at) DESC, x.id DESC
) n
WHERE c.id = n.canonical_id AND c.password_hash <> n.password_hash;
"
            );

            // 2.3 Aliases: every merged public_id → its identity. Idempotent.
            migrationBuilder.Sql(
                @"
INSERT INTO user_aliases (alias_public_id, user_id, source_workspace_id, merged_at)
SELECT merged_public_id, canonical_id, merged_owner_id, now() FROM db11_merge
ON CONFLICT (alias_public_id) DO NOTHING;
"
            );

            // 2.4 Memberships for EVERY workspace-scoped users row (owner_id IS NOT NULL), live or soft-deleted,
            //     pointing at the canonical identity. A soft-deleted row becomes an ENDED membership
            //     (left_at = deleted_at, reason Removed=1). Runs BEFORE 2.6 so merged rows are still live here.
            //     NOT EXISTS makes a second run a no-op. created_by = the all-zero uuid (system).
            //     GLM A7: DISTINCT ON (identity, workspace, live?) — several pre-merge rows of the same person
            //     in the same workspace (two soft-deleted rows; or a live pair differing only in e-mail case, which
            //     ux_users_email_owner_live did not catch) collapse to ONE membership per (identity, workspace, live?).
            //     Live partition: the canonical row's own membership wins, else the oldest. Ended partition: the most
            //     recently ended row wins. Without this the live pair would violate ux_workspace_memberships_user_workspace_live
            //     and the migration would fail with a unique-violation instead of a clean ABORT.
            migrationBuilder.Sql(
                @"
INSERT INTO workspace_memberships
  (user_id, owner_id, role_id, is_active, approval_status, security_stamp, joined_at, left_at, left_reason, invite_id, created_at, created_by)
SELECT DISTINCT ON (coalesce(m.canonical_id, u.id), u.owner_id, (u.deleted_at IS NULL))
       coalesce(m.canonical_id, u.id), u.owner_id, u.role_id,
       (u.is_active AND u.deleted_at IS NULL), u.approval_status, gen_random_uuid(), u.created_at,
       CASE WHEN u.deleted_at IS NOT NULL THEN u.deleted_at END,
       CASE WHEN u.deleted_at IS NOT NULL THEN 1 END,
       NULL, now(), '00000000-0000-0000-0000-000000000000'
FROM users u LEFT JOIN db11_merge m ON m.merged_id = u.id
WHERE u.owner_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM workspace_memberships w
                  WHERE w.user_id = coalesce(m.canonical_id, u.id) AND w.owner_id = u.owner_id
                    AND w.deleted_at IS NULL
                    AND (w.left_at IS NULL) = (u.deleted_at IS NULL))
ORDER BY coalesce(m.canonical_id, u.id), u.owner_id, (u.deleted_at IS NULL),
         (u.id = coalesce(m.canonical_id, u.id)) DESC, u.deleted_at DESC NULLS LAST, u.created_at, u.id;
"
            );

            // 2.4b invite_id for quick-access memberships (the only ones we can attribute today).
            migrationBuilder.Sql(
                @"
UPDATE workspace_memberships m SET invite_id = q.invite_id
FROM quick_access_links q JOIN users u ON u.public_id = q.user_id
WHERE m.invite_id IS NULL AND m.owner_id = q.owner_id
  AND m.user_id = coalesce(u.merged_into_user_id, (SELECT canonical_id FROM db11_merge d WHERE d.merged_id = u.id), u.id);
"
            );

            // 2.5 Rewrite every uuid reference to a merged public_id → the canonical public_id. Generic over
            //     information_schema so no user column is missed; excludes owner_id (workspace ids),
            //     workspaces.id, users.public_id/security_stamp, workspace_memberships.security_stamp and the
            //     alias table itself. Expected to touch exactly the columns listed in §2 ("Every uuid column").
            //     Also api_keys.user_id (int). Idempotent: after one run nothing matches an alias.
            //     F1 (DB-11a cross-review) — a same-workspace duplicate pair (canonical and merged
            //     both members of the SAME workspace W) each have their own active api_keys row for
            //     (user_id, W). Rewriting the merged row's key to user_id = canonical_id would then
            //     leave TWO active keys for (canonical_id, W), violating
            //     ux_api_keys_active_per_membership (Migration 1) with a raw unique-violation instead
            //     of the designed 'DB-11a ABORT'. Revoke the merged (losing) identity's active key
            //     FIRST, but only where the canonical identity already holds a live active key for
            //     that SAME owner_id — a merged identity's key for a workspace the canonical identity
            //     does NOT also hold is simply reassigned, unaffected. Idempotent: on a second run
            //     db11_merge is empty (2.1's WHERE excludes already-soft-deleted rows).
            migrationBuilder.Sql(
                @"
UPDATE api_keys k
SET revoked_at = now()
FROM db11_merge m
WHERE k.user_id = m.merged_id
  AND k.revoked_at IS NULL AND k.deleted_at IS NULL
  AND EXISTS (
    SELECT 1 FROM api_keys c
    WHERE c.user_id = m.canonical_id
      AND c.owner_id IS NOT DISTINCT FROM k.owner_id
      AND c.revoked_at IS NULL AND c.deleted_at IS NULL
  );
DO $$
DECLARE t record;
BEGIN
  FOR t IN
    SELECT c.table_name, c.column_name
    FROM information_schema.columns c
    WHERE c.table_schema = 'public' AND c.udt_name = 'uuid'
      AND c.table_name NOT IN ('user_aliases', '__EFMigrationsHistory')
      AND c.column_name <> 'owner_id'
      AND NOT (c.table_name = 'users' AND c.column_name IN ('public_id', 'security_stamp'))
      AND NOT (c.table_name = 'workspaces' AND c.column_name = 'id')
      AND NOT (c.table_name = 'workspace_memberships' AND c.column_name = 'security_stamp')
  LOOP
    EXECUTE format(
      'UPDATE %I t SET %I = c.public_id FROM user_aliases a JOIN users c ON c.id = a.user_id WHERE t.%I = a.alias_public_id',
      t.table_name, t.column_name, t.column_name);
  END LOOP;
END $$;
UPDATE api_keys k SET user_id = m.canonical_id FROM db11_merge m WHERE k.user_id = m.merged_id;
"
            );

            // 2.6 Retire the merged rows: soft-delete, mark merged_into, kill sessions. Then normalise e-mails
            //     to lower-case on live rows (safe now: duplicates are soft-deleted).
            migrationBuilder.Sql(
                @"
UPDATE users u
SET merged_into_user_id = m.canonical_id, deleted_at = now(),
    deleted_by = '00000000-0000-0000-0000-000000000000', is_active = false, security_stamp = gen_random_uuid()
FROM db11_merge m WHERE u.id = m.merged_id AND u.merged_into_user_id IS NULL;
UPDATE users SET email = lower(email) WHERE deleted_at IS NULL AND email <> lower(email);
"
            );

            // 2.7 ABORT if the invariants do not hold (rolls back this migration; Migration 3 never runs).
            migrationBuilder.Sql(
                @"
DO $$
DECLARE dupes text; missing bigint;
BEGIN
  SELECT string_agg(min_id::text, ', ') INTO dupes
  FROM (SELECT min(id) AS min_id FROM users WHERE deleted_at IS NULL GROUP BY lower(email) HAVING count(*) > 1) d;
  IF dupes IS NOT NULL THEN
    RAISE EXCEPTION 'DB-11a ABORT: live duplicate e-mails remain after merge (users.id): %', dupes;
  END IF;
  SELECT count(*) INTO missing FROM users u
  WHERE u.owner_id IS NOT NULL AND u.deleted_at IS NULL
    AND NOT EXISTS (SELECT 1 FROM workspace_memberships w WHERE w.user_id = u.id AND w.owner_id = u.owner_id AND w.left_at IS NULL AND w.deleted_at IS NULL);
  IF missing > 0 THEN
    RAISE EXCEPTION 'DB-11a ABORT: % live workspace users have no live membership', missing;
  END IF;
END $$;
DROP TABLE IF EXISTS db11_merge;
"
            );
        }

        /// <inheritdoc />
        // No inverse: 2.5 rewrites uuid references in place. Rollback = restore the pre-db11a dump (docs/db/execution/DB-11a-identity-and-workspace-memberships.md §8).
        protected override void Down(MigrationBuilder migrationBuilder) { }
    }
}
