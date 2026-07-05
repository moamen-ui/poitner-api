# C1 — `owner_id` back-fill runbook (operator)

**Finding:** BACKEND_REVIEW.md → **C1** (null-owner isolation collapse). **Status:** code lever shipped
(default OFF); this data step + flag flip is what actually closes it in production. **Read-only until
step 3.** Run on the VM's `pointer` DB. Take a backup/snapshot before any `UPDATE`.

> Why this can't be auto-run: the tenancy migration (`20260629130828_AddTenancy`) added `owner_id` as
> nullable with **no back-fill**, so legacy rows are `NULL`. Assigning each row to its *true* owner
> depends on live data only the operator can see — a blind back-fill could mis-assign data across
> tenants. The code change ships a safe, default-OFF lever (`Tenancy:StrictNullTenantIsolation`); flip
> it ON only after the data below is clean.

## Step 1 — Diagnose (READ-ONLY)

```sql
-- How much null-owner data exists per strict-own table?
SELECT 'projects' t, count(*) FROM projects WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'users',    count(*) FROM users    WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'comments', count(*) FROM comments WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'replies',  count(*) FROM replies  WHERE owner_id IS NULL AND deleted_at IS NULL;

-- Which null-owner projects exist, and are they real customers or just global/marketing?
SELECT id, key, name, created_at FROM projects WHERE owner_id IS NULL AND deleted_at IS NULL ORDER BY created_at;

-- Non-super-admin users with a null owner (these are the principals that see the whole null bucket):
SELECT u.id, u.email, u.public_id, r.name role, r.is_super_admin
FROM users u JOIN roles r ON r.id = u.role_id
WHERE u.owner_id IS NULL AND u.deleted_at IS NULL AND r.is_super_admin = false;
```

**If all three counts are 0** (and the only null-owner users are super-admins): C1 is not exploitable
today — skip to Step 3 (flip the flag as defense-in-depth).

## Step 2 — Decide the true owner of each null-owner row

For each null-owner **project**, decide one of:
- **(a) It belongs to a real workspace** → set `owner_id` to that workspace's tenant GUID (the owning
  user's `public_id`), and cascade the same owner to its comments/replies and its stakeholder users.
- **(b) It is a genuine global/marketing project** → give it a dedicated owner too (create/ු designate a
  "global" workspace user and use its `public_id`). After this, **no strict-own row should be
  null-owner** — global visibility is only for own-plus-global entities (roles/statuses/actions), never
  for projects/comments/users.

Record the mapping `project_id → owner_guid` before running Step 3.

## Step 3 — Back-fill (WRITE — customize, run in a transaction, after backup)

Template — **replace the GUIDs/keys with the Step 2 mapping; do NOT run verbatim:**

```sql
BEGIN;

-- Example: assign one null-owner project (and its dependent rows) to a real owner.
--   :proj  = project id,  :owner = owning tenant GUID (a users.public_id)
UPDATE projects  SET owner_id = :owner WHERE id = :proj      AND owner_id IS NULL;
UPDATE comments  SET owner_id = :owner WHERE project_id = :proj AND owner_id IS NULL;
UPDATE replies   SET owner_id = :owner WHERE comment_id IN (SELECT id FROM comments WHERE project_id = :proj) AND owner_id IS NULL;

-- Stakeholder users that belong to this workspace (identify them by your own criteria, e.g. the
-- accounts that registered against :proj). Assign each to :owner:
-- UPDATE users SET owner_id = :owner WHERE public_id IN (...) AND owner_id IS NULL;

-- Verify inside the transaction BEFORE committing:
SELECT 'projects' t, count(*) c FROM projects WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'comments', count(*) FROM comments WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'replies',  count(*) FROM replies  WHERE owner_id IS NULL AND deleted_at IS NULL
UNION ALL SELECT 'users',    count(*) FROM users    WHERE owner_id IS NULL AND deleted_at IS NULL AND role_id IN (SELECT id FROM roles WHERE is_super_admin = false);

-- If the counts are all 0 (super-admin users may legitimately stay null): COMMIT; else ROLLBACK; and revisit Step 2.
COMMIT;
```

## Step 4 — Flip the lever ON

Once Step 3 verification shows **no null-owner strict-own rows** (aside from super-admins), enable the
shipped code lever so a null-tenant principal can never again see the null bucket:

```yaml
# docker-compose.prod.yml  (api service env)  — or appsettings.Production.json
Tenancy__StrictNullTenantIsolation: "true"
```

Redeploy the API. The strict-own query filters (`Project/User/Comment/Reply/Invite/Subscription/
ExtensionSite/PredefinedActionSuggestion`) then return **nothing** for a non-super principal whose
`TenantId` is null, closing C1. Own-plus-global entities (roles/statuses/predefined-actions) are
unaffected — their `owner_id IS NULL` rows remain intentionally global.

## Step 5 — Verify

- Log in as a normal tenant user → sees only their workspace (unchanged).
- The regression test `TenantQueryFilterTests.NullTenant_NonSuper_StrictFlag_SeesNothing` encodes the
  post-flip guarantee; keep it green.
- Confirm no legitimate stakeholder lost access (they shouldn't — every real row now has an owner).

## Related code (already shipped, default-safe)

- `Infrastructure/AppDbContext.cs` — flag-gated strict-own filters + `IModelCacheKeyFactory` so the flag
  varies the model cache.
- `AuthService.RegisterAsync` / `RoleService` — M15: anonymous key resolution now refuses/ignores an
  ambiguous (colliding) project key instead of binding to an arbitrary tenant.
- `SuggestionService` — C1e: refuses to create a null-owner suggestion (keeps that strict-own invariant).
