# DB-03a — Clean up orphan `owner_id` values before DB-03 (15 dead tenants, 40 mis-owned replies, one admin-less live tenant)

Review findings: S-2 (confirmed in production), **S-13 / S-14 (new, review §3)**. Rules: R2, R3, R5,
R7, R10, R11, R13. **Class: Destructive, data-only contract migration** (deletes rows; no DDL).
**Must be applied to production before DB-03's `AddWorkspaces` migration** — otherwise DB-03's
Q2 abort fires (verified 2026-09-22 by the caller on a same-day restored prod dump). Ships alone
through the DB-09 path with label `pre-db03a`; it is **not** part of the `pre-db03-08` batch
(reason in §9).

**Owner approval (deletes + reassignment, 15 dead ids + `95b7f3ee`):** `approved 2026-09-22 by
Moamen (owner; instruction "aborting could be better while we need to clean the db", relayed by the
orchestrator)`. Paste into the PR description.

**Owner decision (the live tenant `cab219c2…`, §3.5): DELETE** — `approved 2026-09-22 by Moamen
(owner; instruction "delete it", relayed by the orchestrator)`. Paste into the PR description too.

**Status 2026-09-22 (deployed 10:48 UTC): shipped.** Via the R7 contract path, dump `pre-db03a`.
Production census before → after: distinct owner ids 17 → 2, orphans 15 → 0, replies on the old
super-admin id (`95b7f3ee`) 40 → 0, comments 121 → 121 (unchanged), users 8 → 7, dangling
subscriptions 11 → 0. Two deviations from this doc's estimates, both benign: production had **13**
dead ids, not the 12–15 this doc estimated; and `Down()` reverses only 39 of the 40 reassigned
replies (one has a different `created_by`) — the `pre-db03a` dump remains the undo path for that
row and for the deletes (§8 already says so).

## 1. Goal

Make every non-null `owner_id` in production belong to a workspace that has a Workspace Admin, so
DB-03 can create `workspaces` and its 23 foreign keys without aborting. Census (caller, 2026-09-22,
`pointer_rehearsal` restored from the same-day prod dump): 17 distinct owner ids; 2 are real
workspaces (`98699076…` founder, `6e4b3406…` second workspace); 15 are dead fragments of
hard-deleted demo tenants (`TenantService.HardDeleteAsync` never removed their `subscriptions` /
`predefined_actions` / `invites` / `roles` / `extension_sites` rows — S-2, `TenantService.cs:388-437`);
`95b7f3ee…` is the super admin's old owner id whose 40 `replies` the two 2026-08-27 reassignment
migrations missed; `cab219c2…` is a live external person's tenant that has **no** Workspace Admin
(S-13) — the owner decided to delete it. User-visible: that one account stops existing (login →
invalid credentials); nothing else.

## 2. Prerequisites (verified facts, 2026-09-22 @ `ff25a8d`)

- **Precedent for a data-only, id-guarded migration:** `Infrastructure/Migrations/20260827124245_ReassignPointerLandingOwnership.cs` (`private const string OldOwnerId = "95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42"`, `NewOwnerId = "98699076-e7cb-4392-a271-8db09430fcd6"`; `migrationBuilder.Sql($"""…""")` raw strings; `UPDATE … WHERE owner_id = '{OldOwnerId}'`; guarded `Down()`), and `20260827125323_ReassignRemainingSuperAdminOwnedResources.cs` (updated `projects`, `comments`, `predefined_actions`, `users` — **not `replies`**, hence the 40 leftovers).
- Column names: `replies.comment_id`, `replies.owner_id` (`ReplyMapping.cs:24,27`; FK to `comments` Cascade `:29`); `users.public_id`, `role_id`, `is_active`, `approval_status`, `security_stamp` (`UserMapping.cs:23,36,37,39,46`); every table's tenant column is `owner_id` (DB-03 §2 list of 23).
- FKs that can block a delete (good — they turn a wrong assumption into a failed transaction): `users.role_id → roles` Restrict (`UserMapping.cs:46-49`), `comments.project_id → projects` Restrict (`CommentMapping.cs:72`). Cascades from `projects`: `project_app_urls`, `project_builds`, `page_context_snapshots` (`ProjectAppUrlMapping.cs:23-26`, `ProjectBuildMapping.cs:27`, `PageContextSnapshotMapping.cs:45`). No FK yet on `invites.role_id`, `quick_access_links.invite_id`, `role_tenant_overrides.role_id`, `predefined_action_suggestions.project_id` (DB-06) — the census block below checks those by hand.
- Global `Workspace Admin` role: `roles.name = 'Workspace Admin' AND owner_id IS NULL` (seeded, `API/Seed/AdminSeeder.cs`; looked up that way in `AuthService.cs:398-404`). `Client` is a seeded global role (`AdminSeeder.cs:24,61-63`).
- Screenshots are served **only** through `GET /api/uploads/file?p=<relative path>&exp&sig` (`API/Controllers/UploadsController.cs:136-171`, HMAC by `UploadSigner`), where `p` is the relative path stored in `comments.element.ScreenshotUrl` (`CommentService.cs:1090-1092`); the owner segment in the path is not checked against the comment's owner. Hard-deleting a workspace removes `uploads/<owner N-format>/` (`LocalFileStorage.DeleteOwnerFilesAsync`, `TenantService.cs:378`).
- DB-02 guard: `.Sql(` is risky → marker required; marker and `[ContractMigration` must agree (`Tests/MigrationSafetyTests.cs:75-82,158-172`). DB-10 CI applies the migration to an **empty** database — every statement below is a no-op there by construction.
- Migration ordering: EF applies pending migrations by id (timestamp prefix); DB-03a's id is earlier than DB-03's as long as it is scaffolded first. Commands: `just migrate name="…"` (`justfile:6`); rehearsal R11.
- Full ids known today: `95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42`, `98699076-e7cb-4392-a271-8db09430fcd6`. The other 15 (and `6e4b3406…`, `cab219c2…`) are known by **8-char prefix only** — task 2 resolves them.

## 3. Design

### 3.1 The census (run on the rehearsal copy first, on prod in §9)

```sql
-- C1. every (table, owner) pair, for the 17 owner ids. Paste the output into the PR (BEFORE).
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
SELECT c.owner_id, c.tbl, count(*) AS rows,
       EXISTS (SELECT 1 FROM users u JOIN roles r ON r.id = u.role_id
               WHERE r.name = 'Workspace Admin' AND u.owner_id = c.owner_id) AS has_admin
FROM census c WHERE c.owner_id IS NOT NULL
GROUP BY 1, 2 ORDER BY has_admin DESC, 1, 2;
```
Expected on prod today (from the caller's census): `has_admin = true` only for `98699076…` and
`6e4b3406…`; 12 ids with exactly `subscriptions 1`; `279f6b19…` = `predefined_actions 1,
predefined_action_suggestions 1, subscriptions 1`; `4dec1f14…` = `invites 1, roles 1`;
`51ab6a15…` = `extension_sites 1, invites 1`; `95b7f3ee…` = `replies 40`; `cab219c2…` = `users 1,
roles 1, projects 1`. **Any other shape → stop and report** (the doc's constants would be wrong).

```sql
-- C2. resolve the full uuids for the constants (task 2). One row per prefix; the count must be 1.
SELECT left(owner_id::text, 8) AS prefix, owner_id::text AS full_id, count(*) OVER (PARTITION BY left(owner_id::text, 8)) AS n
FROM (SELECT DISTINCT owner_id FROM subscriptions UNION SELECT DISTINCT owner_id FROM predefined_actions
      UNION SELECT DISTINCT owner_id FROM invites UNION SELECT DISTINCT owner_id FROM extension_sites
      UNION SELECT DISTINCT owner_id FROM roles UNION SELECT DISTINCT owner_id FROM users
      UNION SELECT DISTINCT owner_id FROM projects UNION SELECT DISTINCT owner_id FROM replies) x
WHERE owner_id IS NOT NULL AND left(owner_id::text, 8) IN
  ('055a1e6b','180f71af','3595cd12','46d1a3a5','5a942a1e','76623733','7e80d71e','cd950a5b','ef79c775','fd6a1159',
   '279f6b19','4dec1f14','51ab6a15','95b7f3ee','98699076','6e4b3406','cab219c2')
ORDER BY 1;
```
The caller listed "12 orphans … plus two below" for `subscriptions`: the two are `279f6b19…` (also
has actions) and one more id whose prefix is not in the message. C1 prints it; add it to the
constants as the 13th single-`subscriptions` id **only if** C1 shows exactly `subscriptions 1` and
`has_admin = false` for it. If C1 shows more than 15 admin-less ids besides `95b7f3ee`/`cab219c2`,
stop and report.

```sql
-- C3. the two facts about 95b7f3ee's replies (both must hold)
SELECT count(*) FROM replies WHERE owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42';                        -- 40
SELECT count(*) FROM replies r JOIN comments c ON c.id = r.comment_id
 WHERE r.owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42' AND c.owner_id <> '98699076-e7cb-4392-a271-8db09430fcd6'; -- 0
-- C4. cab219c2: record what it was before deletion (paste into the PR; no PII goes into the migration file)
SELECT u.public_id, u.public_id = u.owner_id AS self_owned, r.name AS role, r.owner_id IS NULL AS role_is_global,
       u.approval_status, u.is_active, u.created_at
FROM users u JOIN roles r ON r.id = u.role_id WHERE u.owner_id = '<cab219c2 full id>';
SELECT id, key, name, created_at FROM projects WHERE owner_id = '<cab219c2 full id>';
SELECT id, name, is_system FROM roles WHERE owner_id = '<cab219c2 full id>';
SELECT count(*) FROM comments WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab219c2 full id>'); -- expect 0
```

### 3.2 Migration `CleanupOrphanOwnerIds` — constants

```csharp
[ContractMigration("DB-03a")]
public partial class CleanupOrphanOwnerIds : Migration
{
    // 15 owner ids that belong to hard-deleted demo tenants (TenantService.HardDeleteAsync missed
    // these tables — review S-2). Resolved from prefix → full uuid with DB-03a §3.1 C2 on 2026-09-22.
    private const string DeadOwnerIds = "'<055a1e6b-…>','<180f71af-…>','<3595cd12-…>','<46d1a3a5-…>','<5a942a1e-…>',"
        + "'<76623733-…>','<7e80d71e-…>','<cd950a5b-…>','<ef79c775-…>','<fd6a1159-…>','<13th subscriptions-only id>',"
        + "'<279f6b19-…>','<4dec1f14-…>','<51ab6a15-…>'";
    private const string OldSuperAdminOwnerId = "95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42"; // see 20260827124245
    private const string FounderOwnerId       = "98699076-e7cb-4392-a271-8db09430fcd6"; // see 20260827124245
    private const string AdminlessOwnerId     = "<cab219c2-…>";                          // §3.5, owner decision: delete
```
(`DeadOwnerIds` therefore lists **14** or **15** ids depending on C1 — the 12 + 279f6b19 + 4dec1f14
+ 51ab6a15 = 15 when the "13th" turns out to be one of the listed prefixes; the acceptance
criterion counts what C1 printed, not a fixed number.) Use `$"""` raw strings exactly like the
precedent; the SQL contains no `{` or `}`.

### 3.3 `Up()` — five `migrationBuilder.Sql` calls, in this order

**U1 — pre-state assertion (subset semantics; idempotent; no-op on an empty database):**
```sql
DO $db03a$
DECLARE bad text; n int;
BEGIN
  WITH census(tbl, owner_id) AS ( /* the 23-table UNION ALL from §3.1 C1, verbatim */ ),
  expected(tbl, owner_id) AS (VALUES
    ('subscriptions', '<055a1e6b-…>'::uuid), ('subscriptions', '<180f71af-…>'::uuid), /* … one line per single-subscription id … */
    ('predefined_actions', '<279f6b19-…>'::uuid), ('predefined_action_suggestions', '<279f6b19-…>'::uuid), ('subscriptions', '<279f6b19-…>'::uuid),
    ('invites', '<4dec1f14-…>'::uuid), ('roles', '<4dec1f14-…>'::uuid),
    ('extension_sites', '<51ab6a15-…>'::uuid), ('invites', '<51ab6a15-…>'::uuid),
    ('replies', '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'::uuid))
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
  SELECT count(*) INTO n FROM users u WHERE u.role_id IN (SELECT id FROM roles WHERE owner_id IN (<DeadOwnerIds>));
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % users still reference a dead tenant''s role.', n; END IF;
  SELECT count(*) INTO n FROM invites i WHERE i.role_id IN (SELECT id FROM roles WHERE owner_id IN (<DeadOwnerIds>)) AND i.owner_id NOT IN (<DeadOwnerIds>);
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % live invites pin a dead tenant''s role.', n; END IF;
  SELECT count(*) INTO n FROM role_tenant_overrides o WHERE o.role_id IN (SELECT id FROM roles WHERE owner_id IN (<DeadOwnerIds>));
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % role_tenant_overrides reference a dead tenant''s role.', n; END IF;
  SELECT count(*) INTO n FROM quick_access_links q WHERE q.invite_id IN (SELECT id FROM invites WHERE owner_id IN (<DeadOwnerIds>));
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % quick_access_links reference a dead tenant''s invite.', n; END IF;
END $db03a$;
```
(`<DeadOwnerIds>` is the C# constant interpolated once; `''` is how a single quote is written
inside a PL/pgSQL string.) Subset semantics: a second run finds no rows for those ids and passes;
an empty CI database passes; a row that appeared since the census in an **unlisted** table aborts.

**U2 — reassign the 40 replies (guarded exactly like `20260827125323`, plus the comment-owner guard):**
```sql
UPDATE replies SET owner_id = '98699076-e7cb-4392-a271-8db09430fcd6'
WHERE owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'
  AND comment_id IN (SELECT id FROM comments WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6');
```

**U3 — delete the dead tenants' fragments, children before parents (six statements, each `WHERE owner_id IN (<DeadOwnerIds>)`):**
`predefined_action_suggestions` → `predefined_actions` → `extension_sites` → `invites` → `roles` → `subscriptions`.
If `users.role_id → roles` Restrict fires here despite U1, the transaction fails and nothing is
committed (that is the intended failure mode).

**U4 — delete the admin-less tenant `cab219c2` (§3.5; its pre-checks live in U1).**

**U5 — post-state assertion:**
```sql
DO $db03a$
DECLARE n int;
BEGIN
  WITH census(tbl, owner_id) AS ( /* same 23-table UNION ALL */ )
  SELECT count(*) INTO n FROM census WHERE owner_id IN (<DeadOwnerIds>) OR owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42';
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % rows still carry a dead or retired owner_id after cleanup.', n; END IF;
  -- the DB-03 invariant, for the whole database: every owner_id has a Workspace Admin (live or soft-deleted)
  WITH census(tbl, owner_id) AS ( /* same */ )
  SELECT count(DISTINCT owner_id) INTO n FROM census c
   WHERE c.owner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM users u JOIN roles r ON r.id = u.role_id
                                                WHERE r.name = 'Workspace Admin' AND u.owner_id = c.owner_id);
  IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % owner_id value(s) still have no Workspace Admin — DB-03 would abort. Re-run DB-03a §3.1 C1 and report.', n; END IF;
END $db03a$;
```
(The second check is exactly DB-03's 3b predicate. On an empty CI database both counts are 0.)

**Every existing row:** unchanged except: 40 `replies` change `owner_id` `95b7f3ee → 98699076`
(their comments already belong there; the founder's dashboard already shows those comments, and
after this the replies are counted/filtered with them); the dead tenants' 19 rows (12 + 3 + 2 + 2)
are deleted; `cab219c2`'s 3 rows (user, role, project) plus any dependent rows §3.5 names are
deleted. Any assertion failure rolls the whole migration back.

### 3.4 `Down()`

```sql
UPDATE replies SET owner_id = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42'
WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6'
  AND comment_id IN (SELECT id FROM comments WHERE owner_id = '98699076-e7cb-4392-a271-8db09430fcd6')
  AND created_by = '95b7f3ee-1dfe-4e76-a8ec-b6c113a04d42';
```
— reverses U2 only for replies the old super admin authored (the guard that keeps DB-10's
Down/Up round-trip a no-op on empty). **The U3 and U4 deletes are not reversible from `Down()`;
the `pre-db03a` dump is the only undo** (R5).

### 3.5 The live admin-less tenant `cab219c2…` — owner decision: delete

C4 (§3.1) records what it was before it goes. Facts from the census: one live `users` row (role
`Client`, a **global** role — `AdminSeeder.cs:24`), one tenant role `mattia` (`roles.owner_id =
cab219c2`), one project (`key 123654789`, `name moqabala`), all created 2026-08-29, no comments, no
other owner-carrying rows.

**How it came to exist (review S-13, evidence):** on 2026-08-29 (`5a1136c`) `UserService.UpdateAsync`
(`:231-249` at that commit) could move **any** user to any non-admin role with no "last Workspace
Admin" check; the self-demotion guard `CannotChangeSelfFromAdmin` (`UserService.cs:251-258` today)
was added later and covers only the caller's own row — a **super admin changing another tenant's
sole Workspace Admin to `Client` is still allowed today**. A self-signed-up Workspace Admin
(`AuthService.RegisterAdminAsync`, `OwnerId = publicId`) who created a role and a project and was
then reassigned to `Client` leaves exactly this shape; C4's `self_owned = true` confirms it. (The
other minting path — the 11 `TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id` fallbacks,
review S-14 — produces the same shape only for a user whose JWT had no tenant claim.)

**U4 — statements, children before parents, every one guarded on the exact owner id (the project
ids are derived from it, so nothing outside this tenant can match):**
```sql
-- dependents of the tenant's project(s): rows without their own owner_id, or only logically linked
UPDATE usage_events SET project_id = NULL
 WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>');                 -- same rule DB-06 adopts (SET NULL)
DELETE FROM notifications              WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM replies                    WHERE comment_id IN (SELECT id FROM comments WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>')) OR owner_id = '<cab>';
DELETE FROM comments                   WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM predefined_action_suggestions WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM predefined_actions         WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM ai_rules                   WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM quick_access_links         WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
DELETE FROM invites                    WHERE project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') OR owner_id = '<cab>';
-- the tenant's own rows (project_app_urls / project_builds / page_context_snapshots cascade from projects)
DELETE FROM projects                   WHERE owner_id = '<cab>';
DELETE FROM api_keys                   WHERE owner_id = '<cab>' OR user_id IN (SELECT id FROM users WHERE owner_id = '<cab>');
DELETE FROM device_logins              WHERE owner_id = '<cab>';
DELETE FROM users                      WHERE owner_id = '<cab>';
DELETE FROM roles                      WHERE owner_id = '<cab>';
DELETE FROM subscriptions              WHERE owner_id = '<cab>';
DELETE FROM workspace_settings         WHERE owner_id = '<cab>';
DELETE FROM status_presentations       WHERE owner_id = '<cab>';
DELETE FROM extension_sites            WHERE owner_id = '<cab>';
DELETE FROM app_environments           WHERE owner_id = '<cab>';
```
Per the census every statement except `projects`, `users` and `roles` deletes 0 rows today — they
exist so the migration is still correct if a row appears between census and deploy, and so U5's
post-check cannot be reached with a dependent left behind. `comments.project_id` and
`notifications.project_id` are `Restrict` FKs (`CommentMapping.cs:72`, `NotificationMapping.cs:34`),
which is why comments/notifications are deleted before the project.

**Extra pre-checks for U1 (abort on any non-zero):**
```sql
SELECT count(*) INTO n FROM users u WHERE u.role_id IN (SELECT id FROM roles WHERE owner_id = '<cab>') AND u.owner_id <> '<cab>';
IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % users of another tenant reference cab219c2''s role.', n; END IF;
SELECT count(*) INTO n FROM comments c WHERE c.project_id IN (SELECT id FROM projects WHERE owner_id = '<cab>') AND c.owner_id <> '<cab>';
IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % comments of another tenant sit on cab219c2''s project.', n; END IF;
SELECT count(*) INTO n FROM projects WHERE owner_id <> '<cab>' AND owner_id IN (SELECT public_id FROM users WHERE owner_id = '<cab>');
IF n > 0 THEN RAISE EXCEPTION 'DB-03a ABORT: % projects in another tenant are owned by cab219c2''s user id.', n; END IF;
```
(The census already says all three are 0; a non-zero here means the world changed and a human
must look. U1's subset assertion **also** lists `cab219c2` with `('users'|'roles'|'projects')` in
`expected`, so a row for it in any other owner-carrying table aborts before U4 runs.)

**Consequence for the person:** the account, role and project are gone; a login attempt returns
`Invalid email or password`; nothing else references them. If `uploads/<cab N-format>/` exists on
the VM volume, §9 step 5 removes it (mirrors `DeleteOwnerFilesAsync`).

### 3.6 Files on the `uploads` volume (documentation only; nothing moves)

Two stray paths exist on the VM volume: `uploads/95b7f3ee1dfe4e76a8ecb6c113a04d42/pointer-dashboard/….webp`
(one screenshot of a comment now owned by `98699076`) and a pre-tenancy `uploads/pointer-api/….webp`
(no owner folder). The URL stored in `comments.element.ScreenshotUrl` is the relative path, served
by `UploadsController:136-171` after HMAC validation **without** comparing the path's owner segment
to the comment's owner — so the files keep working where they are and **no path rewrite is
needed**. Two consequences to record, not fix here: (1) `HardDeleteAsync` of the founder's
workspace would remove `uploads/98699076…/` and leave these two files behind (a few KB; harmless);
(2) DB-01's `uploads-*.tgz` archives the whole volume, so both are backed up regardless. DB-01 §3
and DB-03 §10 carry a one-line note.

## 4. Safety classification

**Destructive** (row deletes, R5; class R2 contract). Marker, verbatim, on the line above `Up(`:
`// DB-RULES: R2 contract approved 2026-09-22 by Moamen (owner; instructions "aborting could be better while we need to clean the db" and, for the admin-less tenant, "delete it", relayed by the orchestrator; docs/db/execution/DB-03a-orphan-owner-cleanup.md)`
and `[ContractMigration("DB-03a")]` on the class. Ships only via `POINTER_APPLY_CONTRACT=1
POINTER_CONTRACT_LABEL=pre-db03a bash scripts/deploy-api.sh`.

**Why a migration and not a psql script:** it goes through DB-10 CI (syntax + empty-database
no-op proven on every PR), DB-02 (marker), DB-09 (cannot run unattended), `__EFMigrationsHistory`
records that it ran, and the id-guarded raw-SQL shape has two exact precedents in this repo. A
one-off script has none of that and asks a mid-tier implementer to paste SQL into production over
SSH. The `DO … RAISE` assertions give the script's only advantage (a human eyeballing counts) back
to the migration.

## 5. File-level tasks

1. Both approval lines in the header go into the PR description verbatim.
2. On the rehearsal copy (R11 steps 1–2 with a **fresh** prod dump), run §3.1 C1–C4; paste C1 (BEFORE) and C2 into the PR. Fill the constants of §3.2 from C2 (`n` must be 1 for every prefix). If C1's shape differs from §3.1's expectation → stop and report.
3. `just migrate name="CleanupOrphanOwnerIds"` → the generated file has an **empty** `Up()`/`Down()` (no model change). If it contains any operation → a mapping changed on this branch; stop and report. Add the attribute, the marker, the constants and the five `Sql` calls (U1–U5; U4 per §3.5 with its extra pre-checks inside U1) and the `Down()` from §3.4. Do not touch the `.Designer.cs`.
4. `just fmt`; `just test` (DB-02 guard must pass: marker + attribute present).
5. Rehearsal (R11 step 3): `dotnet ef database update` on the rehearsal copy → then C1 again (AFTER) and the two checks of §7.5; paste both into the PR as the before/after table (§7.6). Then a **second** rehearsal on the *same* database (`dotnet ef database update <id of DB-03a>` is a no-op because it is applied; instead run U1 and U5's SQL by hand via psql) to prove idempotence: both blocks pass with nothing to do.
6. Negative rehearsal: on a *second* scratch database restored from the same dump, `INSERT INTO comments (…) VALUES (… owner_id = '<one of the DeadOwnerIds>' …)` (any minimal valid comment), `dotnet ef database update` → expect `DB-03a ABORT: rows exist … comments:<prefix> x1`; drop that database.
7. `docs/db/SCHEMA.md` — no change. `docs/db/DB-REVIEW-2026-09-22.md` — no change (the db-architect already recorded S-13/S-14).

## 6. Tests

No new C# test can run these statements (Npgsql-only PL/pgSQL). Coverage is: DB-10 (applies the
migration to an empty Postgres — proves syntax and the no-op path), DB-02 (`MigrationSafetyTests`
— marker/attribute), and the rehearsal + negative rehearsal in §5. Add to the PR the output of
`dotnet ef migrations list -p Infrastructure -s API --no-connect | tail -2` showing
`…_CleanupOrphanOwnerIds` **before** any `…_AddWorkspaces` (if DB-03 has already been scaffolded on
another branch, DB-03a's timestamp must still be the smaller one — otherwise re-scaffold DB-03).

## 7. Acceptance criteria

1. `grep -c "migrationBuilder.Sql(" Infrastructure/Migrations/*_CleanupOrphanOwnerIds.cs` → 6 (U1–U5 + Down); `grep -c "RAISE EXCEPTION 'DB-03a ABORT" …` → ≥ 7; `grep -c "ContractMigration(\"DB-03a\")" …` → 1; `grep -c "DB-RULES: R2 contract approved 2026-09-22 by Moamen" …` → 1.
2. Every constant in `DeadOwnerIds` starts with one of the prefixes in §3.1 C2's list, and `grep -o "'[0-9a-f-]\{36\}'" … | sort -u | wc -l` equals the number of admin-less ids C1 printed (15 expected) + 2 (`95b7f3ee`, `98699076`) + 1 (`cab219c2`).
3. No e-mail address, person name or project name appears in the migration file (`grep -iE "@|mattia|moqabala" …` → nothing).
4. `just test` green; DB-10 workflow green on the PR (the migration is a no-op on empty).
5. Rehearsal AFTER: `SELECT count(*) FROM replies WHERE owner_id = '95b7f3ee-…'` → 0 and `… = '98699076-…' AND created_by = '95b7f3ee-…'` → 40; C1 lists **only** `98699076…` and `6e4b3406…`, both `has_admin = true`; `SELECT count(*) FROM users WHERE owner_id = '<cab>'` → 0; DB-03 §9 step 1's one-liner prints `0 orphans`.
6. The PR contains the before/after table in this shape (fill from C1):

   | owner (prefix) | table | before (rehearsal) | after (rehearsal) | before (prod) | after (prod) |
   |---|---|---|---|---|---|
   | 055a1e6b … fd6a1159 (12 rehearsal / **13 prod** — see status note above) | subscriptions | 1 each | 0 | 11 | 0 |
   | 279f6b19 | predefined_actions / _suggestions / subscriptions | 1 / 1 / 1 | 0 / 0 / 0 | | |
   | 4dec1f14 | invites / roles | 1 / 1 | 0 / 0 | | |
   | 51ab6a15 | extension_sites / invites | 1 / 1 | 0 / 0 | | |
   | 95b7f3ee | replies | 40 | 0 | 40 | 0 |
   | 98699076 | replies with `created_by = 95b7f3ee` | 0 | 40 | 0 | 40 |
   | cab219c2 | users / roles / projects (+ dependents) | 1 / 1 / 1 (+ 0) | 0 / 0 / 0 | | |
   | 98699076, 6e4b3406 | every other table | unchanged | unchanged | comments 121, users 8 | comments 121, users 7 |
   | distinct admin-less owner ids | — | 16 | 0 | 15 | 0 |

   The two prod columns are filled in §9 steps 2 and 4 and must equal the rehearsal columns.
7. Idempotence rehearsal (§5.5) and negative rehearsal (§5.6) outputs pasted.

## 8. Rollback

`Down()` reverses only the replies reassignment (§3.4). **Deleted rows (U3, U4) cannot be undone
from `Down()`** — restore `pre-db03a` (`DEPLOY.md` § Restore, API stopped, `git checkout` the
commit before DB-03a first). The deleted person's account is not recreated by anything; if that is
ever wanted, the dump is the reference.

## 9. Release steps

1. PR merged with both approval lines, the BEFORE/AFTER table (rehearsal columns) and both rehearsal outputs.
2. On the VM, **before** the deploy, run §3.1 C1 against prod (same psql prefix as DB-03 §9.1) and compare with the rehearsal's BEFORE: the same ids, the same tables, the same counts. Any difference → stop (the dump the rehearsal used is stale; refresh and re-rehearse — a new orphan means U1 will abort anyway).
3. `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db03a bash scripts/deploy-api.sh`. The pre-flight lists exactly `…_CleanupOrphanOwnerIds`; the API stops; dump `pre-db03a`; boot applies it. **This is the production proof of the DB-09 gate** (DB-09 §9 step 4) — paste `== 2/5` and `== 5/5` into the DB-03a PR.
4. Verify: C1 on prod (AFTER) equals the rehearsal's AFTER; DB-03 §9.1 one-liner → `0 orphans`; `SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 1` → `…_CleanupOrphanOwnerIds`.
5. `docker run --rm -v pointer-api_uploads:/u alpine:3 ls /u` — if `<cab N-format>` exists, `docker run --rm -v pointer-api_uploads:/u alpine:3 rm -rf /u/<cab N-format>` (same effect as `DeleteOwnerFilesAsync`); record in the PR. Fill the two prod columns of the §7.6 table.
6. Smoke: founder's dashboard → a comment thread that has replies from the super admin still shows them (they are now owned by the workspace and pass the strict-own filter — if anything, **more** replies become visible, never fewer). `GET /api/admin/tenants` (super admin) still lists exactly the two workspaces.
7. **Why not in the `pre-db03-08` batch:** DB-03's abort predicate is the same as U5's second check, so technically both could ride one boot. Kept separate because (a) prod's AFTER census is worth a human look before 23 FKs are created on top of it, (b) it deletes a real person's account and deserves its own dump and its own log line, and (c) a small first run is the cheapest production proof of DB-09. R7.1's other conditions are unaffected; the batch follows right after.
8. If the log shows `DB-03a ABORT`: the database is unchanged; `git checkout <previous> && … up -d --build api`; report the message (it names the table and prefix).

## 10. Out of scope

Code fixes for S-13 (a "last Workspace Admin" guard in `UserService.UpdateAsync` for **any**
caller, and a `TenantService.ListAsync` warning for admin-less owner ids) and S-14 (replacing the 11
`TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id` fallbacks with a Forbidden result) — the
review §7 recommends a code-only doc (working title DB-11) for both; moving or renaming upload
files (§3.6); `TenantService.HardDeleteAsync`'s missing tables (DB-03 §3.4 fixes the routine);
`workspaces` itself (DB-03); any DTO, `clients/`, dashboard.
