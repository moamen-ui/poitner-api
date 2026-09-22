using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pointer.Infrastructure.Migrations
{
    /// <inheritdoc />
    // DB-RULES: R2 contract approved 2026-09-22 by Moamen (owner; instructions "aborting could be
    // better while we need to clean the db" and, for the admin-less tenant, "delete it", relayed by
    // the orchestrator; docs/db/execution/DB-03a-orphan-owner-cleanup.md)
    [ContractMigration("DB-03a")]
    public partial class CleanupOrphanOwnerIds : Migration
    {
        // 13 owner ids that belong to hard-deleted demo tenants (TenantService.HardDeleteAsync
        // missed these tables — review S-2). Resolved from prefix -> full uuid with DB-03a §3.1 C2
        // on 2026-09-22 against pointer_rehearsal (restored from the same-day prod dump): every
        // prefix below resolved to exactly one full uuid (C2 output, 17 rows total including the
        // three named constants further down).
        private const string DeadOwnerIds =
            "'055a1e6b-ad13-4427-9b83-2ca399d3ea50','180f71af-f9c4-43cb-b83d-7cb6ed4c8a36',"
            + "'3595cd12-51d9-4920-9d1d-c9f926eee35b','46d1a3a5-4126-4ba6-8ead-96c6e2825d54',"
            + "'5a942a1e-dceb-4c06-bed1-b2bb21f351e6','76623733-afd3-49f2-a120-faf61f4a8e9b',"
            + "'7e80d71e-f244-414a-96a8-1791e865a3e5','cd950a5b-f065-4480-a9a6-a8097614db4c',"
            + "'ef79c775-48e4-49b9-8f6f-0ba031f17df6','fd6a1159-307e-4208-b1f3-8c4e010f70fe',"
            + "'279f6b19-69e7-4092-935a-9210d8ba7661','4dec1f14-19c8-4b80-85e9-09557f0da4de',"
            + "'51ab6a15-ed0a-403c-8f15-fed3c41a5431'";
        private const string OldSuperAdminOwnerId = "95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42"; // see 20260827124245
        private const string FounderOwnerId = "98699076-e7cb-4392-a271-8db09430fcd6"; // see 20260827124245
        private const string AdminlessOwnerId = "cab219c2-7872-49f8-892f-083cec0027c0"; // §3.5, owner decision: delete

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // U1 - pre-state assertion (subset semantics; idempotent; no-op on an empty database).
            migrationBuilder.Sql(
                $"""
                DO $db03a$
                DECLARE bad text; n int;
                BEGIN
                  WITH census(tbl, owner_id) AS (
                    SELECT 'ai_rules', owner_id FROM ai_rules UNION ALL SELECT 'api_keys', owner_id FROM api_keys
                    UNION ALL SELECT 'app_environments', owner_id FROM app_environments UNION ALL SELECT 'comments', owner_id FROM comments
                    UNION ALL SELECT 'device_logins', owner_id FROM device_logins UNION ALL SELECT 'extension_sites', owner_id FROM extension_sites
                    UNION ALL SELECT 'invites', owner_id FROM invites UNION ALL SELECT 'notifications', owner_id FROM notifications
                    UNION ALL SELECT 'page_context_snapshots', owner_id FROM page_context_snapshots
                    UNION ALL SELECT 'predefined_action_suggestions', owner_id FROM predefined_action_suggestions
                    UNION ALL SELECT 'predefined_actions', owner_id FROM predefined_actions UNION ALL SELECT 'project_app_urls', owner_id FROM project_app_urls
                    UNION ALL SELECT 'project_builds', owner_id FROM project_builds UNION ALL SELECT 'projects', owner_id FROM projects
                    UNION ALL SELECT 'quick_access_links', owner_id FROM quick_access_links UNION ALL SELECT 'replies', owner_id FROM replies
                    UNION ALL SELECT 'role_tenant_overrides', owner_id FROM role_tenant_overrides UNION ALL SELECT 'roles', owner_id FROM roles
                    UNION ALL SELECT 'status_presentations', owner_id FROM status_presentations UNION ALL SELECT 'subscriptions', owner_id FROM subscriptions
                    UNION ALL SELECT 'usage_events', owner_id FROM usage_events UNION ALL SELECT 'users', owner_id FROM users
                    UNION ALL SELECT 'workspace_settings', owner_id FROM workspace_settings
                  ),
                  expected(tbl, owner_id) AS (VALUES
                    ('subscriptions', '055a1e6b-ad13-4427-9b83-2ca399d3ea50'::uuid),
                    ('subscriptions', '180f71af-f9c4-43cb-b83d-7cb6ed4c8a36'::uuid),
                    ('subscriptions', '3595cd12-51d9-4920-9d1d-c9f926eee35b'::uuid),
                    ('subscriptions', '46d1a3a5-4126-4ba6-8ead-96c6e2825d54'::uuid),
                    ('subscriptions', '5a942a1e-dceb-4c06-bed1-b2bb21f351e6'::uuid),
                    ('subscriptions', '76623733-afd3-49f2-a120-faf61f4a8e9b'::uuid),
                    ('subscriptions', '7e80d71e-f244-414a-96a8-1791e865a3e5'::uuid),
                    ('subscriptions', 'cd950a5b-f065-4480-a9a6-a8097614db4c'::uuid),
                    ('subscriptions', 'ef79c775-48e4-49b9-8f6f-0ba031f17df6'::uuid),
                    ('subscriptions', 'fd6a1159-307e-4208-b1f3-8c4e010f70fe'::uuid),
                    ('predefined_actions', '279f6b19-69e7-4092-935a-9210d8ba7661'::uuid), ('predefined_action_suggestions', '279f6b19-69e7-4092-935a-9210d8ba7661'::uuid), ('subscriptions', '279f6b19-69e7-4092-935a-9210d8ba7661'::uuid),
                    ('invites', '4dec1f14-19c8-4b80-85e9-09557f0da4de'::uuid), ('roles', '4dec1f14-19c8-4b80-85e9-09557f0da4de'::uuid),
                    ('extension_sites', '51ab6a15-ed0a-403c-8f15-fed3c41a5431'::uuid), ('invites', '51ab6a15-ed0a-403c-8f15-fed3c41a5431'::uuid),
                    ('replies', '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'::uuid),
                    ('users', 'cab219c2-7872-49f8-892f-083cec0027c0'::uuid), ('roles', 'cab219c2-7872-49f8-892f-083cec0027c0'::uuid), ('projects', 'cab219c2-7872-49f8-892f-083cec0027c0'::uuid))
                  SELECT string_agg(c.tbl || ':' || left(c.owner_id::text, 8) || ' x' || c.n, ', ') INTO bad
                  FROM (SELECT tbl, owner_id, count(*) AS n FROM census
                        WHERE owner_id IN (SELECT owner_id FROM expected) GROUP BY 1, 2) c
                  LEFT JOIN expected e ON e.tbl = c.tbl AND e.owner_id = c.owner_id
                  WHERE e.tbl IS NULL;
                  IF bad IS NOT NULL THEN
                    RAISE EXCEPTION 'DB-03a ABORT: rows exist for a listed owner_id in a table the 2026-09-22 census did not list (data changed since): %. Nothing was changed.', bad;
                  END IF;

                  SELECT count(*) INTO n FROM replies r JOIN comments c ON c.id = r.comment_id
                   WHERE r.owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42' AND c.owner_id <> '98699076-e7cb-4392-a271-8db09430fcd6';
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % replies owned by 95b7f3ee belong to comments of another owner.', n; END IF;

                  -- references INTO the dead tenants' rows from rows we are not deleting (no FK protects these yet, DB-06)
                  SELECT count(*) INTO n FROM users u WHERE u.role_id IN (SELECT id FROM roles WHERE owner_id IN ({DeadOwnerIds}));
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % users still reference a dead tenant''s role.', n; END IF;
                  SELECT count(*) INTO n FROM invites i WHERE i.role_id IN (SELECT id FROM roles WHERE owner_id IN ({DeadOwnerIds})) AND i.owner_id NOT IN ({DeadOwnerIds});
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % live invites pin a dead tenant''s role.', n; END IF;
                  SELECT count(*) INTO n FROM role_tenant_overrides o WHERE o.role_id IN (SELECT id FROM roles WHERE owner_id IN ({DeadOwnerIds}));
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % role_tenant_overrides reference a dead tenant''s role.', n; END IF;
                  SELECT count(*) INTO n FROM quick_access_links q WHERE q.invite_id IN (SELECT id FROM invites WHERE owner_id IN ({DeadOwnerIds}));
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % quick_access_links reference a dead tenant''s invite.', n; END IF;

                  -- extra pre-checks for the admin-less live tenant (§3.5): abort if the world changed since the census
                  SELECT count(*) INTO n FROM users u WHERE u.role_id IN (SELECT id FROM roles WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') AND u.owner_id <> 'cab219c2-7872-49f8-892f-083cec0027c0';
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % users of another tenant reference cab219c2''s role.', n; END IF;
                  SELECT count(*) INTO n FROM comments c WHERE c.project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') AND c.owner_id <> 'cab219c2-7872-49f8-892f-083cec0027c0';
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % comments of another tenant sit on cab219c2''s project.', n; END IF;
                  SELECT count(*) INTO n FROM projects WHERE owner_id <> 'cab219c2-7872-49f8-892f-083cec0027c0' AND owner_id IN (SELECT public_id FROM users WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0');
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % projects in another tenant are owned by cab219c2''s user id.', n; END IF;
                END $db03a$;
                """
            );

            // U2 - reassign the 40 replies (guarded exactly like 20260827125323, plus the comment-owner guard).
            migrationBuilder.Sql(
                $"""
                UPDATE replies SET owner_id = '98699076-e7cb-4392-a271-8db09430fcd6'
                WHERE owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'
                  AND comment_id IN (SELECT id FROM comments WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6');
                """
            );

            // U3 - delete the dead tenants' fragments, children before parents.
            migrationBuilder.Sql(
                $"""
                DELETE FROM predefined_action_suggestions WHERE owner_id IN ({DeadOwnerIds});
                DELETE FROM predefined_actions WHERE owner_id IN ({DeadOwnerIds});
                DELETE FROM extension_sites WHERE owner_id IN ({DeadOwnerIds});
                DELETE FROM invites WHERE owner_id IN ({DeadOwnerIds});
                DELETE FROM roles WHERE owner_id IN ({DeadOwnerIds});
                DELETE FROM subscriptions WHERE owner_id IN ({DeadOwnerIds});
                """
            );

            // U4 - delete the admin-less tenant cab219c2 (§3.5), children before parents.
            migrationBuilder.Sql(
                $"""
                UPDATE usage_events SET project_id = NULL
                 WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0');
                DELETE FROM notifications WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM replies WHERE comment_id IN (SELECT id FROM comments WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0')) OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM comments WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM predefined_action_suggestions WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM predefined_actions WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM ai_rules WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM quick_access_links WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM invites WHERE project_id IN (SELECT id FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0') OR owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM projects WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM api_keys WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0' OR user_id IN (SELECT id FROM users WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0');
                DELETE FROM device_logins WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM users WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM roles WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM subscriptions WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM workspace_settings WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM status_presentations WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM extension_sites WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                DELETE FROM app_environments WHERE owner_id = 'cab219c2-7872-49f8-892f-083cec0027c0';
                """
            );

            // U5 - post-state assertion.
            migrationBuilder.Sql(
                $"""
                DO $db03a$
                DECLARE n int;
                BEGIN
                  WITH census(tbl, owner_id) AS (
                    SELECT 'ai_rules', owner_id FROM ai_rules UNION ALL SELECT 'api_keys', owner_id FROM api_keys
                    UNION ALL SELECT 'app_environments', owner_id FROM app_environments UNION ALL SELECT 'comments', owner_id FROM comments
                    UNION ALL SELECT 'device_logins', owner_id FROM device_logins UNION ALL SELECT 'extension_sites', owner_id FROM extension_sites
                    UNION ALL SELECT 'invites', owner_id FROM invites UNION ALL SELECT 'notifications', owner_id FROM notifications
                    UNION ALL SELECT 'page_context_snapshots', owner_id FROM page_context_snapshots
                    UNION ALL SELECT 'predefined_action_suggestions', owner_id FROM predefined_action_suggestions
                    UNION ALL SELECT 'predefined_actions', owner_id FROM predefined_actions UNION ALL SELECT 'project_app_urls', owner_id FROM project_app_urls
                    UNION ALL SELECT 'project_builds', owner_id FROM project_builds UNION ALL SELECT 'projects', owner_id FROM projects
                    UNION ALL SELECT 'quick_access_links', owner_id FROM quick_access_links UNION ALL SELECT 'replies', owner_id FROM replies
                    UNION ALL SELECT 'role_tenant_overrides', owner_id FROM role_tenant_overrides UNION ALL SELECT 'roles', owner_id FROM roles
                    UNION ALL SELECT 'status_presentations', owner_id FROM status_presentations UNION ALL SELECT 'subscriptions', owner_id FROM subscriptions
                    UNION ALL SELECT 'usage_events', owner_id FROM usage_events UNION ALL SELECT 'users', owner_id FROM users
                    UNION ALL SELECT 'workspace_settings', owner_id FROM workspace_settings
                  )
                  SELECT count(*) INTO n FROM census WHERE owner_id IN ({DeadOwnerIds}) OR owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42';
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % rows still carry a dead or retired owner_id after cleanup.', n; END IF;
                  -- the DB-03 invariant, for the whole database: every owner_id has a Workspace Admin (live or soft-deleted)
                  WITH census(tbl, owner_id) AS (
                    SELECT 'ai_rules', owner_id FROM ai_rules UNION ALL SELECT 'api_keys', owner_id FROM api_keys
                    UNION ALL SELECT 'app_environments', owner_id FROM app_environments UNION ALL SELECT 'comments', owner_id FROM comments
                    UNION ALL SELECT 'device_logins', owner_id FROM device_logins UNION ALL SELECT 'extension_sites', owner_id FROM extension_sites
                    UNION ALL SELECT 'invites', owner_id FROM invites UNION ALL SELECT 'notifications', owner_id FROM notifications
                    UNION ALL SELECT 'page_context_snapshots', owner_id FROM page_context_snapshots
                    UNION ALL SELECT 'predefined_action_suggestions', owner_id FROM predefined_action_suggestions
                    UNION ALL SELECT 'predefined_actions', owner_id FROM predefined_actions UNION ALL SELECT 'project_app_urls', owner_id FROM project_app_urls
                    UNION ALL SELECT 'project_builds', owner_id FROM project_builds UNION ALL SELECT 'projects', owner_id FROM projects
                    UNION ALL SELECT 'quick_access_links', owner_id FROM quick_access_links UNION ALL SELECT 'replies', owner_id FROM replies
                    UNION ALL SELECT 'role_tenant_overrides', owner_id FROM role_tenant_overrides UNION ALL SELECT 'roles', owner_id FROM roles
                    UNION ALL SELECT 'status_presentations', owner_id FROM status_presentations UNION ALL SELECT 'subscriptions', owner_id FROM subscriptions
                    UNION ALL SELECT 'usage_events', owner_id FROM usage_events UNION ALL SELECT 'users', owner_id FROM users
                    UNION ALL SELECT 'workspace_settings', owner_id FROM workspace_settings
                  )
                  SELECT count(DISTINCT owner_id) INTO n FROM census c
                   WHERE c.owner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM users u JOIN roles r ON r.id = u.role_id
                                                                WHERE r.name = 'Workspace Admin' AND u.owner_id = c.owner_id);
                  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % owner_id value(s) still have no Workspace Admin — DB-03 would abort. Re-run DB-03a §3.1 C1 and report.', n; END IF;
                END $db03a$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverses U2 only, for replies the old super admin authored. The U3 and U4 deletes are
            // not reversible from Down() — the pre-db03a dump is the only undo (DB-RULES R5).
            migrationBuilder.Sql(
                $"""
                UPDATE replies SET owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'
                WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6'
                  AND comment_id IN (SELECT id FROM comments WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6')
                  AND created_by = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42';
                """
            );
        }
    }
}
