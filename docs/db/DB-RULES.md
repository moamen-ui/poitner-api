# DB rules

Standing rules for every schema change in this repository. Created 2026-09-22 by the db-architect
review ([`DB-REVIEW-2026-09-22.md`](DB-REVIEW-2026-09-22.md)); amended the same day after the
cross-reviews (R4, R6, R7, R13 — marked *(amended)*) and again the same evening after the owner
decisions (R7 marker form, **R7.1 batching**, R8 point 6), and again the same night after the DB-11
cross-review (R9 expression indexes, R14 normaliser + erase inventory, R16 exact fence + scoped tokens), and again the same night (late) after DB-12–15 (R8 point 8 operator/analytics tables, **R17 append-only tables and the audit obligation**),
and on 2026-09-23 after the DB-12–15 cross-review (R16 claim parsing, R17 shape-based trigger + operator redaction, **R18 operator content boundary**);
amend, do not fork. Every execution
doc under [`execution/`](execution/) cites the rule numbers it relies on. `scripts/deploy-api.sh`
already points here.

Vocabulary: **workspace** = tenant = the `owner_id` uuid on a row (after DB-03, a row in
`workspaces`). **Strict-own** / **own-plus-global** = the two query-filter shapes in
`Infrastructure/AppDbContext.cs:76-147`.

---

## R1. Additive first

A migration that ships on its own may only: `CreateTable`, `AddColumn` (nullable, or with
`HasDefaultValue`/`HasDefaultValueSql`), `CreateIndex`, `AddForeignKey`/`AddCheckConstraint` (see R4
for large tables). It must not `DropColumn`, `DropTable`, `RenameColumn`, `RenameTable`, or
`AlterColumn` to a narrower type / `NOT NULL` in the **same release** that stops writing the old
shape. Precedents to copy: `20260917230734_AddCommentLanguage`, `20260921215630_AddCommentFieldsAndWorkspaceSettings`.

## R2. Expand → migrate → contract, across releases

Renames, splits, merges and type changes take three deploys minimum:

1. **Expand** — add the new column/table (R1). Code writes both shapes, reads the old.
2. **Migrate** — backfill (R3). Code switches reads to the new shape; still writes both.
3. **Contract** — drop the old shape in a later release, after the rehearsal (R11) shows zero
   readers. Contract migrations carry a `// DB-RULES: R2 contract approved <yyyy-mm-dd> by <name>`
   comment on the line above `Up()`; the DB-02 guard test requires it, and (after DB-09) the
   class also carries `[ContractMigration("DB-NN")]` so the runtime gate can see it (R7).

`20260903191419_ProjectPerEnvironmentActivation` did all three in one `Up()`. Do not copy it.

## R3. Backfills

A backfill is its own migration (or an idempotent `AdminSeeder` step, precedent
`API/Seed/AdminSeeder.cs:92-118`), never mixed with the DDL that creates the column, and is:

- **idempotent / re-runnable** — guarded `WHERE` so a second run is a no-op
  (precedent `20260915163653_FixPayloadFlagsJsonDefault.cs:44-46`);
- **batched** when the table has more than ~100 000 rows. On PostgreSQL that means: loop
  `UPDATE t SET … WHERE id > $last AND id <= $last + 5000 AND <predicate>` in **separate
  transactions** (so `pg_stat_activity` shows short locks and vacuum can keep up) — EF migrations
  run inside one transaction, so a batched backfill is a `psql`/script step or an `AdminSeeder`
  loop, not `migrationBuilder.Sql`. Below that size a single guarded `UPDATE` is fine and is what
  the repo has always done;
- **never** `SET NOT NULL` in the same statement group as the backfill unless the pre-check
  `SELECT count(*) … WHERE col IS NULL` printed `0` during the rehearsal (R11), and the doc says so.

## R4. Indexes and constraints on large tables

Up to ~1 M rows: plain `CreateIndex`/`AddForeignKey` (millisecond locks at today's size). Above
that: `migrationBuilder.Sql("CREATE INDEX CONCURRENTLY …", suppressTransaction: true)` and FKs as
`ADD CONSTRAINT … NOT VALID` followed by `VALIDATE CONSTRAINT` in a second statement. The
execution doc states which mode applies and why.

*(amended, GLM C1)* Two consequences the doc must spell out when the concurrent mode is used:

- A `CREATE INDEX CONCURRENTLY` that fails or is interrupted leaves an **INVALID** index behind
  (`\d table` shows `INVALID`; `SELECT indexrelid::regclass FROM pg_index WHERE NOT indisvalid`).
  The migration must start with `DROP INDEX CONCURRENTLY IF EXISTS <name>;` so a retry is clean, and
  a migration that builds an index concurrently contains **nothing else** (no other DDL, no data)
  — `suppressTransaction: true` means there is no rollback to lean on.
- `NOT VALID` is a deferral, not an exemption. The `VALIDATE CONSTRAINT` statement ships in the
  same release (its own migration is fine) and the release steps verify
  `SELECT conname FROM pg_constraint WHERE NOT convalidated` returns nothing. An FK left `NOT VALID`
  enforces new writes but hides existing orphans and is not used by the planner.

## R5. Every execution doc has a Rollback section

It names the exact `Down()` behaviour or states in bold that none exists. Anything that destroys
data (drop column/table, data-moving `UPDATE` with no inverse) requires **a fresh
`scripts/backup-db.sh <label>` immediately before** and the label written into the doc's release
steps. `20260911170828_MigrateDefaultProjectAppUrlsToLocal` has an empty `Down()` — that is allowed
only when the doc says so.

## R6. Backup before every deploy that includes a migration

`scripts/deploy-api.sh` calls `scripts/backup-db.sh pre-deploy` before rebuilding the API
(`deploy-api.sh:23-24`); it is the **only** supported deploy path. Restore is `DEPLOY.md` § Backups
/ Restore; *(amended)* it was **rehearsed locally on 2026-09-22** ("Last rehearsed" line) and is
re-rehearsed whenever `backup-db.sh` or the restore steps change. After DB-01 the nightly dump is
also copied off-box, the `uploads` volume is archived beside it, and `deploy-api.sh` refuses to run
when the newest dump is older than 26 h.

## R7. Auto-migrate on boot is for additive migrations only

`API/Program.cs:165-174` runs `MigrateAsync` on every start when `DBMigrationEnabled=true`
(production does, `docker-compose.prod.yml:26`). That is acceptable **only** for R1-class
migrations. Anything in the Contract or Destructive class ships as an explicit step:

```bash
# on the VM, API stopped, fresh dump taken
docker compose -f docker-compose.prod.yml stop api
bash scripts/backup-db.sh pre-<slug>
docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api   # boot applies it
docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|error"
```

i.e. the same commands `deploy-api.sh` runs, but with the API **stopped first** so no request
hits a half-migrated schema, and with a human reading the log. *(amended)* After DB-09 this manual
sequence is refused at boot unless `export DB_APPLY_CONTRACT=true` precedes the `up`; the
supported form is simply `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash
scripts/deploy-api.sh`, which performs exactly these steps.

*(amended)* **Enforcement is [DB-09](execution/DB-09-migration-apply-gate.md), not the PR
reviewer.** Every migration that carries a DB-RULES approval marker (R2 contract, R3 backfill,
index change, R4 constraint — i.e. anything the DB-02 guard flags) also carries the attribute
`[ContractMigration("DB-NN")]` on its class. `API/Program.cs` checks pending migrations before
`MigrateAsync()`; if any pending one carries the attribute and `DBApplyContractMigrations` is not
`true`, the API logs the ids at Critical, exits with code 3 and does **not** migrate. Only
`POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-<slug> bash scripts/deploy-api.sh` sets that
flag — and that path is the block above (stop api → labelled dump → up). The DB-02 test requires
marker and attribute to agree. Until DB-09 ships, the reviewer of the PR is the gate.

**Who signs a marker.** `by <name>` names the human owner, or names the agent **and** the standing
owner instruction it acts under, in parentheses — precedent
`20260922080137_DropShadowProjectAppUrlProjectId1.cs:12` ("by orchestrator (owner instruction
"proceed"; …)"). A marker that names only an agent is not an approval. *(amended 2026-09-22
evening)* When the owner decided a specific question that the marker encodes, the marker names the
owner and the relayed instruction: `by Moamen (owner; instruction "…", relayed by the orchestrator;
docs/db/execution/DB-NN-….md)` — DB-03/06/07 use this form.

### R7.1 Batching several contract docs into one deploy *(added 2026-09-22 evening)*

Several execution docs whose migrations all carry `[ContractMigration]` **may ship in one
`POINTER_APPLY_CONTRACT=1` run** — one stop, one dump, one boot — when **all** of the following hold.
The batch is a *deploy* decision; it never relaxes how each doc is reviewed.

1. **Small blast radius.** The production database restores in well under a minute from the
   labelled dump (rehearsed: 26 tables, seconds). Today that is true — one real workspace. Once
   real tenants exist (say > 10 workspaces or a dump > 1 GB), batching stops: one contract doc per
   deploy, so a failure is attributable and a restore is cheap to explain.
2. **Each doc is its own PR/commit(s)**, merged in the docs' order, each green in CI (DB-10 applies
   the whole chain from empty, DB-02 checks markers/attributes) and each read against its own §3
   (R13). Never squash two docs' migrations into one file.
3. **Every batched doc's prod pre-checks run immediately before the deploy and all pass** (DB-03
   §9 step 1 `0 orphans`; DB-06 §3 ten zeros; DB-07 §2 zero). If any fails, **nothing ships** — do
   not revert one doc on the VM to let the others through; fix, re-merge, re-check.
4. **One R11 rehearsal of the combined pending chain** on a fresh prod dump taken the same day,
   running every batched doc's verification queries; the rehearsal output is pasted into the last
   doc's PR.
5. **One dump labelled `pre-<first>-<last>`** (e.g. `pre-db03-08`), written into every batched doc's
   §9. Code-only docs (DB-03b, DB-08) may ride along; ops-only docs (DB-01) are unrelated.
6. **Rollback is all-or-nothing:** restore that dump (`DEPLOY.md` § Restore) and `git checkout` the
   commit *before the first* batched doc, then `up -d --build api`. EF applies each migration in its
   own transaction, so a failure mid-chain leaves the earlier ones applied — the answer is still the
   restore, never a hand-fix of `__EFMigrationsHistory` or of the schema.
7. **`deploy-api.sh` itself must already be the DB-09 version on the VM** before the batch run
   (deploy the DB-09 commit with an ordinary run first while nothing is pending): bash reads the
   script as it executes, so the run that pulls a new script must not also be the first to rely on
   it.

Verdict for the current plan: **DB-03a ships alone first** (`pre-db03a` — it deletes a real
person's account and its post-census deserves a human look before 23 FKs are built on it; it is
also the cheapest production proof of DB-09). Then DB-03 → DB-03b → DB-06 → DB-07 → DB-08 satisfy
1–7 and ship as **one** run, `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db03-08 bash
scripts/deploy-api.sh`.

8. *(added after DB-03a)* **A cleanup that changes or deletes production rows never rides in the
   same run as the structural migration that depends on it.** The structural migration's own
   pre-check must be re-run on prod *after* the cleanup and *before* its deploy (DB-03 §9 step 0–1).

## R8. Tenancy invariant

Every new entity that holds customer data:

1. carries `Guid? OwnerId` mapped to `owner_id` (NOT NULL unless a global bucket is designed and
   documented in the mapping, like `Role`);
2. after DB-03: has `FOREIGN KEY (owner_id) REFERENCES workspaces(id)`;
3. is listed in `AppDbContext.OnModelCreating` with the strict-own filter copied from `Project`
   (`AppDbContext.cs:77`) unless the mapping comment explains why not (`DeviceLogin`,
   `AppDbContext.cs:149-156`, is the only exemption today);
4. is stamped on write with `TenantStamp.OwnerFor(currentUser)` or the owning project's
   `OwnerId` (`Application/Common/TenantStamp.cs:11`; precedent `ProjectBuild.OwnerId` doc-comment);
5. has a test in the shape of `Tests/TenantQueryFilterTests.cs` proving tenant B reads **nothing**
   of tenant A; and, after DB-03, appears in `TenantService.HardDeleteOrder` (the reflection test
   in DB-03 fails the build otherwise).
6. *(added 2026-09-22 evening)* **A workspace's name is `workspaces.name`** (owner decision Q3).
   No code derives a workspace label from a `users` row (display name or e-mail); DB-03 §3.5 lists
   the four sites that used to. `Workspace.PlaceholderName` (`"Workspace"`) is the only fallback.
7. *(added 2026-09-22 night, DB-11a)* **Membership invariants** — after DB-11a a person's presence
   in a workspace is a `workspace_memberships` row, never `users.owner_id`. Every code path that
   lists, counts, authorises or notifies "the users of workspace W" queries memberships with an
   **explicit** `OwnerId == W` predicate (`IMembershipService.InWorkspace(W)`), filters
   `LeftAt == null` for "current members", and never joins `users` by `owner_id`. Two tests are
   mandatory for any change in this area (copy `Tests/WorkspaceMembershipTests.cs`): tenant B's
   context sees **no** identity or membership of A (the `User` filter is membership-based), and a
   membership-ending action in A leaves the same identity's membership in B untouched. A workspace
   must never lose its last live `Workspace Admin` membership — every demote/remove/disable/leave/
   erase goes through `SoleAdminWorkspacesAsync` (DB-11c §3.2; super admins included; tenant
   suspension exempt by D10).

8. *(added 2026-09-22 late night, DB-12/13/15)* **Operator and analytics tables are exempt from point 5 by name.** `usage_events`, `audit_events`,
   `impersonation_sessions` and `usage_daily` carry `owner_id` for the filter shape (points 1–4 hold) but are **not** deleted with the workspace: their FK is
   `ON DELETE SET NULL`, the row survives as an operator/analytics record with `owner_id = NULL`, and the type is excluded **by name with a comment** in
   `Tests/WorkspaceTests.cs` `HardDeleteOrder_CoversEveryOwnerCarryingEntity`. Such a table never holds content (comment text, prompts, names, addresses) —
   only ids, hashes, counts and whitelisted keys. A new table of this kind cites this point in its mapping comment.

## R9. Soft delete and uniqueness

`BaseEntity.DeletedAt` (`Domain/Entity/BaseEntity.cs:10`) is a soft delete with **no global query
filter** — every read predicate says `DeletedAt == null` explicitly. Therefore every unique index on
a `BaseEntity` table is partial: `.HasFilter("deleted_at IS NULL")` (precedents
`ProjectMapping.cs:28`, `ApiKeyMapping.cs:45-48`). A unique index that includes a nullable
`owner_id` also sets `.AreNullsDistinct(false)` so two global rows cannot share a name (precedent
`WorkspaceSettingMapping.cs:30`); no more raw-SQL `_global` indexes (`20260629130828:147-149` is
the anti-precedent, fixed by DB-05).

*(added 2026-09-22 night, DB-11a GLM A1)* **Expression indexes.** When uniqueness must hold on an
expression EF cannot model (`lower(email)`), the index is `migrationBuilder.Sql("CREATE UNIQUE INDEX …")`
in a migration of its own with **nothing else** in it, carries the `index change` marker +
`[ContractMigration]` (the DB-02 regex flags `.Sql(`), has a `Down()` that drops it, and is **not**
mirrored by a `HasIndex` in the model — the mapping carries a two-line comment naming the migration so
nobody "repairs" the snapshot. The DB-10 CI job (apply from empty + newest `Down()`/`Up()` round-trip)
is the mechanical check that raw SQL and model agree. Precedent: DB-11a Migration 3
(`*_AddUsersEmailLiveUniqueIndex`). Application code still normalises the value it compares
(R14) so equality lookups find the row; the database is the authority.

## R10. Frozen identifiers

- **Migration ids** — all of them, not just the two listed: `REBRANDING-PLAN.md` §4.1 (branch
  `docs/rebranding-plan`) explains why (`__EFMigrationsHistory` PK). Never rename a
  `[Migration("…")]` string.
- **Table/column/index names** — rebranding plan §11.1: the schema carries no brand and no schema
  object is renamed for the rebrand. New names are brand-neutral (`workspaces`, not
  `pointer_workspaces`).
- **Customer-visible names** — `docs/ON-DISK-CONTRACT.md` + `Tests/OnDiskContractTests.cs`
  (nothing in the database is in that contract; keep it that way).
- **Enum ints** — append only; never renumber (`Domain/Enums/*`).

## R11. Local rehearsal — every migration is applied to a copy of production before it ships

Dumps are `pg_dump -Fc -Z6` (custom format). Rehearse on the dev Postgres (`just up` starts it on
host port 5433, user/password/db `pointer`, `docker-compose.yaml:3-9`):

```bash
# 1. fetch the newest prod dump (ssh details: DEPLOY.md / memory note "Prod VM deploy access")
scp -i <key> ubuntu@<vm>:'~/backups/'"$(ssh -i <key> ubuntu@<vm> 'ls -1t ~/backups/pointer-*.dump | head -1 | xargs basename')" /tmp/prod.dump

# 2. restore it into a scratch database on the local container
cd /Users/momen/Desktop/REPOS/pointer-api
docker compose up -d db
docker compose exec -T db psql -U pointer -d postgres \
  -c "DROP DATABASE IF EXISTS pointer_rehearsal;" -c "CREATE DATABASE pointer_rehearsal OWNER pointer;"
docker compose exec -T db pg_restore -U pointer -d pointer_rehearsal --no-owner --no-privileges < /tmp/prod.dump

# 3. see exactly what will run, then run it
dotnet ef migrations script --idempotent -p Infrastructure -s API -o /tmp/pending.sql   # read it
ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer_rehearsal;Username=pointer;Password=pointer" \
  dotnet ef database update -p Infrastructure -s API

# 4. run the doc's verification queries
docker compose exec -T db psql -U pointer -d pointer_rehearsal -c "<query from the execution doc>"

# 5. throw it away
docker compose exec -T db psql -U pointer -d postgres -c "DROP DATABASE pointer_rehearsal;"
```

A migration whose `Up()` differs from the design section of its execution doc is not merged; the
implementer reports the diff instead of "fixing" it.

## R12. Data-shape columns (`jsonb` bags)

Use `Infrastructure/Mappings/JsonColumn.cs` (`ConfigureJsonColumn(col, "'{}'"|"'[]'")`) or
`JsonStringList.cs`; owned `ToJson` only for a fixed shape read as a unit (`Comment.Element`).
Every such column has: the C# shape documented on the property; validation and size caps at the
request validator (R4-01 `CommentFieldService.ValidateValues` is the precedent); a default SQL so
old rows never hit the tolerant-parse path; and a note on the property saying how the shape
versions (add optional keys only; never change the meaning of an existing key — write a new key).

## R13. Read the generated migration

After `just migrate name="<PascalCase>"`, open `Infrastructure/Migrations/<ts>_<name>.cs` and
compare every operation to the doc's design table **before** building. A migration named for one
feature that also carries an unrelated column is a red flag (`20260911223426_AddTenantInviteFields`
silently shipped the shadow `ProjectId1`, see DB-04). Also diff `AppDbContextModelSnapshot.cs`;
changes outside the entity you touched mean a mapping bug. *(amended)* After
[DB-10](execution/DB-10-ci-postgres-migration-job.md) ships, CI also applies every migration to an
empty `postgres:15`, fails on `dotnet ef migrations has-pending-model-changes`, and round-trips
the newest `Down()`/`Up()` — so a mapping edit without a migration, or a `Down()` that does not
undo its `Up()`, fails the PR before the R11 rehearsal.

## R14. Identity of users *(amended 2026-09-22 night, DB-11a)*

**One `users` row per e-mail** (`ux_users_email_live UNIQUE (lower(email)) WHERE deleted_at IS NULL`,
an expression index — DB-11a Migration 3, GLM A1; R9).
`users.id int` is the PK; `users.public_id uuid` is the identity everything else refers to
(`comments.author_id`, JWT `sub`, `created_by/updated_by/deleted_by`). Two kinds of reference:

- **Content references** (`author_id`, `actor_id`, `applied_by`, `user_id` on comments, replies,
  notifications, AI rules, links, device logins, usage events, audit columns) use `public_id`, have
  no FK (Q5 open), and say so in the mapping comment. A `public_id` is **never rewritten or
  reused** after DB-11a; the one historical rewrite (DB-11a §3.2 2.5, the same-e-mail merge) is
  recorded in `user_aliases`, and `UserNameResolver` falls back to that table — new code that
  resolves a user from a uuid uses the resolver, not a bare `users` lookup.
- **Structural children** of the identity (`api_keys`, `workspace_memberships`, `user_aliases`,
  `users.merged_into_user_id`) FK `users.id` — they are rows *about* the identity, not content
  attributed to it. Do not add a third kind.

Erasing a person (DB-11c) keeps the `users` row as a tombstone with the same `public_id` (e-mail →
`erased+<id>@tombstone.invalid`, name → `Deleted user`, `erased_at` + `deleted_at` set) precisely so
content references stay resolvable. Hard-deleting a `users` row is allowed only inside a workspace
hard delete and only when the identity has no membership elsewhere (DB-11a §3.5).

*(added 2026-09-22 night)* Two consequences of "e-mail is the key":

- **One normaliser.** Every e-mail that is compared with or written to `users.email` (or to any
  e-mail-bearing column) passes through `Application/Common/EmailNormalizer.Normalize` /
  `NormalizeRequired` (DB-11a §3.3a). No `.Trim().ToLower()` on an e-mail anywhere else — DB-11a
  acceptance criterion 11 greps for it. Changing an identity's address is `POST /api/me/change-email`
  (DB-11d): password + confirmation link to the new address, **Conflict when the address already
  belongs to a live identity — never a runtime merge** (the merge is the one-time, census-guarded
  DB-11a Migration 2).
- **Erase inventory.** DB-11c §3.4 lists every column that can carry a person's address or name and
  what erase does with each (overwrite, null, tombstone, delete, keep-by-design — e.g. `invites.email`
  is **tombstoned, never nulled**, because null means "anyone may accept"; screenshots are kept, F5).
  A new column that carries a person's e-mail or name is added to that table **and** to
  `IdentityEraseService.EraseAsync` in the same PR, with a line in `Erase_LeavesNoEmailBehind`.

## R15. Never edit `clients/`, never hand-edit `*.Designer.cs` or the snapshot

Generated files are regenerated. If the snapshot is wrong, fix the mapping and re-run the
migration tooling.

## R16. Sessions and per-workspace revocation *(added 2026-09-22 night, DB-11a/b/c)*

A session is (identity, workspace): the JWT carries `sub` (identity `public_id`), `tenant` (the
membership's workspace), `stamp` (identity `users.security_stamp`) and `mstamp` (that membership's
`workspace_memberships.security_stamp`). Consequences every change must respect:

- **Identity-wide events** (password change/reset, erase, merge) rotate `users.security_stamp` —
  every workspace's sessions die.
- **Workspace-scoped events** (role change, disable, removal, leave) rotate **that membership's**
  stamp only; other workspaces' sessions of the same person keep working. Never rotate the identity
  stamp for a workspace-scoped event.
- The validator (`API/Extensions/AuthenticationExtensions.cs`) rejects a token whose membership is
  ended, inactive or not approved, within the 60 s cache window; API keys, magic links and device
  codes are **per membership** (`owner_id` = the workspace) and are checked against the same
  membership state at login. Removing a person from a workspace revokes that workspace's keys and
  links; disabling leaves them in place but inert.
- A `scope=select_workspace` token (DB-11b) is accepted **only** by `POST /api/auth/switch-workspace`
  — **exact** path comparison (`SelectionScopeFence.Allows`, `PathString.Equals`, never
  `StartsWithSegments`; GLM A6), and the fence runs in `OnTokenValidated` regardless of
  `Auth:ValidateSecurityStamp`.
- **E-mailed one-time tokens** (password reset, erase confirmation, e-mail change) are
  `IResetTokenService` tokens: stateless, HMAC-signed, 30 min, bound to the identity **and its current
  `users.security_stamp`** (so the action that consumes one rotates the stamp = single-use), and — for
  anything but the legacy reset — bound to a **purpose** (`TokenPurposes`, DB-11c §3.4a) so a link can
  only do the one thing it was minted for. Never a database row; a new purpose is a new constant, not a
  new table. Endpoints that redeem or send such tokens anonymously carry `[EnableRateLimiting("signup")]`
  and return one indistinguishable failure message.
- **Changing the identity's e-mail** (DB-11d) is an identity-wide event: it rotates `users.security_stamp`
  (every workspace's sessions end); it never changes `public_id` or `users.id`.
- `TenantStamp.TryRequireOwner` (S-14) is the only way to obtain "the caller's workspace" for a
  write; a non-super-admin without a `tenant` claim is Forbidden. `?? _currentUser.Id` never returns.
- *(added 2026-09-23, agy DB-14 #1)* **The caller's identity is `ICurrentUser.Id` (`Guid?`), never a raw claim string.** `users.public_id` is a
  `Guid`; the JWT `sub` is a string that the bearer handler may surface as `ClaimTypes.NameIdentifier`. Filters, middleware and services resolve
  `ICurrentUser` (`Infrastructure/CurrentUser/HttpCurrentUser.cs:9-16` is the one parser) and compare `Guid`s; a component that cannot use DI-scoped
  `ICurrentUser` (the token validator) uses `Guid.TryParse(FindFirst(Sub) ?? FindFirst("sub"))` and fails the request when it does not parse
  (`AuthenticationExtensions.cs:61-66`). A doc that writes `u.PublicId == sub` is wrong by construction.
- *(added 2026-09-24, DB-18 §5 task 19 — R16 amendment)* **Two documented departures for a
  workspace-scoped, multi-step, token-redeeming flow** (DB-18's e-mail-confirmed workspace deletion
  is the precedent):
  - **Rate limiting:** an anonymous endpoint that redeems a scoped token may use a dedicated
    fixed-window policy instead of the general `"signup"` policy when the flow needs several calls
    per link (preview, confirm, retries) — DB-18's `"danger"` policy is 10 requests / 10 min,
    partitioned by `sub` claim + IP when authenticated, IP otherwise. `"signup"`'s 5/h/IP budget is
    shared with signup and password reset and is too small for a flow with its own preview step.
  - **Single-use without an identity-stamp rotation:** single-use may come from **state bound into
    the token's payload** (DB-18: `deletion_requested_at` + `deletion_scheduled_for` on the
    workspace row) instead of rotating an identity/membership stamp, when the event is
    workspace-scoped and the session must survive it (the admin stays signed in to cancel or export
    during the grace period). The token still binds the identity's current stamp for revocation
    (a password change or erase still kills the link); it just does not rotate anything itself on
    redemption.

## R17. Append-only tables and the audit obligation *(added 2026-09-22 late night, DB-12)*

- **`audit_events` is append-only at three layers:** properties are `init`-only (no C# update path), `AppDbContext.SaveChangesAsync` throws on a
  Modified/Deleted `AuditEvent` entry, and the Postgres triggers `trg_audit_events_append_only` / `trg_audit_events_no_truncate` raise on UPDATE, DELETE and
  TRUNCATE — the single permitted UPDATE *shape* is the FK's `ON DELETE SET NULL` detaching a hard-deleted workspace (`owner_id` non-null → NULL, byte-identical
  otherwise). *(amended 2026-09-23, GLM DB-12 #4)* The trigger admits the **shape**, it cannot attribute the UPDATE to the FK — a manual detach of that exact
  shape is equally permitted and only the psql history shows it; that is acceptable because detaching hides nothing from the `/all` view. **DB-08 never sweeps it**;
  a retention policy for audit rows is its own execution doc with an owner decision (D12.2 default: forever). A table that must be immutable follows the same
  three layers; the trigger migration carries the **`R4 constraint`** marker (`Tests/MigrationSafetyTests.cs` accepts only the four marker kinds; a trigger that
  enforces immutability is a constraint) and `[ContractMigration]`, and contains nothing else.
- **Every security-relevant mutation writes exactly one audit row** through `IAuditWriter.WriteAsync`, **after** its own `SaveChangesAsync` (or inside the same
  `ExecuteInTransactionAsync` block after the save), using an `AuditActions` constant — never a literal. Security-relevant = every non-GET action on a controller
  under `Pointer.API.Controllers.Admin`, every `AuthController`/`MeController`/`DemoController`/`ExportImportController` action, and every hosted job that changes
  tenant state (`ActorKindOverride: System`). The action carries `[Audited(AuditActions.X)]` or `[NoAudit("reason")]`; `Tests/AuditCoverageTests.cs` fails the
  build otherwise and `AuditCoverageFilter` logs `AUDIT GAP` at runtime (500 under `Audit:StrictCoverage`). A new endpoint that changes state and lacks the
  attribute does not merge.
- **Audit rows hold no personal data beyond identifiers:** `actor_user_id`/`target_id` are `public_id` uuids (resolve to "Deleted user" after erase — R14 content
  reference), an unknown e-mail is `PseudonymHasher.EmailHash` (16 hex), the IP is a keyed HMAC, `before`/`after` accept only `AuditFields.Allowed` keys (no
  `email`, `display_name`, `password`, `token`, `body`, `prompt`, `text`). Because the table cannot be scrubbed, this is the only way R14's erase inventory can say
  "kept by design". Adding a whitelist key is a PR that names the reviewer; adding a personal field is refused.
- **Reserved action strings** (DB-12 §3.6) are the vocabulary for later docs: DB-11b `auth.workspace_switched`, DB-11c `member.left`, `identity.erase_requested`,
  `identity.erased`, DB-11d `auth.email.change_requested`, `auth.email.changed`, DB-14 `auth.email.verified`, DB-13 `impersonation.started`, `impersonation.ended`.
  Whichever doc lands second writes the row; the spelling never changes (R10).
- *(added 2026-09-23, GLM DB-12 #3 / DB-13 #1)* **Audit read DTOs never name the operator to a workspace.** In any workspace-scoped audit or session view
  (`GET /api/admin/audit`, `GET /api/admin/impersonation` for admins), rows whose actor kind is `SuperAdmin` or `Impersonation` are emitted with
  `actor_user_id = null` and `actor_name = "Operator"` (DB-12 §3.8a, D12.5); the full identity exists only in the super-admin `/all` view. A new operator-facing
  DTO that carries an actor field applies the same redaction and has a test that serialises the workspace view and asserts the operator uuid/name is absent.

## R18. Operator content boundary *(added 2026-09-23, DB-13 cross-review — agy DB-13 #1)*

After DB-13 the super-admin token reads **metadata** everywhere and **content** (comments, replies, snapshots, screenshots, suggestions, AI rules,
tenant predefined actions, exports — the DB-13 §3.1 list) only under a live impersonation token whose `tenant` claim is the target workspace.
Consequences for every later change:

- **Every content read path reachable by an operator token is enumerated in the impersonation doc** (DB-13 §3.4, service table + the three rule
  shapes). A PR that adds or changes a read of a content entity edits that table in the same PR, and the reviewer re-runs both checklists
  (`grep -rn "IsSuperAdmin" Application/Services/Implementation` and `grep -rn "IgnoreQueryFilters" Application/Services/Implementation`).
- **Three shapes, one rule each:** a read scoped by `_currentUser.TenantId` needs nothing; a read that *widens* on `IsSuperAdmin` adds `&& !IsImpersonating`
  and, if it returns content, refuses a non-impersonating operator (`Forbidden(MessageKeys.Impersonation.Required)` or `NotFound`); a loader that
  `IgnoreQueryFilters()` **and skips its owner predicate when `IsSuperAdmin`** is a content read in disguise (`SuggestionService.LoadOwnAsync` was the
  instance) — the owner predicate becomes unconditional and the super admin gets `null`. The pre-DB-11a helper `TenantStamp.OwnerFor(u) ?? u.Id` never
  substitutes for the predicate.
- **A new content entity** (a table whose rows hold customer-authored text or media) gets: no `IsSuperAdmin ||` branch in its query filter, a line in
  DB-13 §3.1 "Content", a seed in DB-13 §6 test 1 (`SuperAdmin_WithoutTenantClaim_SeesNoContent`) and test 2, and — if an operator screen ever needs a
  count of it — a count-only query behind `IsSuperAdmin && !IsImpersonating` (the `StatsService`/`ProfileService` pattern).
- **`TryRequireOwner` under impersonation** returns false (operators own nothing). Every workspace-scoped **read** endpoint that an impersonating operator
  should see uses the two-line branch `if (_currentUser.IsImpersonating && _currentUser.TenantId is Guid t) owner = t; else if (!TenantStamp.TryRequireOwner(...)) return Forbidden(...)`
  (DB-12 §3.8a, DB-15 §3.5). Writes never get that branch — the fence rejects them anyway.

## R19. Workspace freeze (pause / scheduled deletion) coverage *(added 2026-09-24, DB-18 §5 task 19)*

Every new mutating action is either gated by `WorkspaceFrozenFilter` (the default — no attribute
needed) or carries `[AllowWhenWorkspacePaused]` with a one-line reason in the same commit explaining
why it is safe to run against a frozen workspace. Consequences:

- **`Tests/WorkspaceFreezeCoverageTests.cs` pins the exact list** of controllers/actions carrying
  `[AllowWhenWorkspacePaused]` — a PR that adds or removes one updates the pinned list in the same
  commit; the test failing is the signal that the change needs a reviewer's eyes, not a rubber stamp.
- **A new anonymous path that can create a membership** (an invite-accept-style endpoint, a future
  self-serve join flow) checks `IWorkspaceStateService.GetAsync(ownerId).IsFrozen` and refuses with
  `MessageKeys.Workspace.FrozenNoNewMembers` — new members while frozen are refused by default (D18.11).
- **Access-*removing* admin actions are always exempt**, even though they mutate: disabling,
  demoting away from an admin role, removing a member, revoking an invite/quick-link, and rejecting a
  pending applicant all carry `[AllowWhenWorkspacePaused]` — a freeze must never stop an admin from
  locking someone out (Opus HIGH 2, DB-18 §3.5). A route that can both grant and revoke in the same
  request (e.g. `PATCH users/{id}`) keeps the attribute (so the revoking cases pass) but the
  **service** itself refuses the specific fields that would grant access (`Password`, `IsActive:
  true`, an admin-tier `RoleId`) while frozen — never the whole route.
- **Every tracked entity load that can run without a usable tenant claim** (an anonymous
  token-redemption endpoint, a hosted job) uses `IgnoreQueryFilters()` with an explicit
  `Id == x && DeletedAt == null` predicate — never a bare `_unitOfWork.<Set>.FirstOrDefaultAsync(...)`,
  which silently returns nothing once the strict-own query filter has no tenant claim to match
  against (DB-18 code review, both reviewers, BLOCKER — every anonymous confirm/pause-instead/
  preview path and the reminder job hit exactly this before the fix). A PR touching a lifecycle-style
  anonymous or job method greps its own diff for `.FirstOrDefaultAsync(w =>` and confirms
  `IgnoreQueryFilters()` precedes every one that is not inside an already-tenant-scoped session guard.
