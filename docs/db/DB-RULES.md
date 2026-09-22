# DB rules

Standing rules for every schema change in this repository. Created 2026-09-22 by the db-architect
review ([`DB-REVIEW-2026-09-22.md`](DB-REVIEW-2026-09-22.md)); amended the same day after the
cross-reviews (R4, R6, R7, R13 — marked *(amended)*) and again the same evening after the owner
decisions (R7 marker form, **R7.1 batching**, R8 point 6); amend, do not fork. Every execution
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

Verdict for the current plan: DB-03 → DB-03b → DB-06 → DB-07 → DB-08 satisfy 1–7 and ship as
**one** run, `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db03-08 bash scripts/deploy-api.sh`.

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

## R9. Soft delete and uniqueness

`BaseEntity.DeletedAt` (`Domain/Entity/BaseEntity.cs:10`) is a soft delete with **no global query
filter** — every read predicate says `DeletedAt == null` explicitly. Therefore every unique index on
a `BaseEntity` table is partial: `.HasFilter("deleted_at IS NULL")` (precedents
`ProjectMapping.cs:28`, `ApiKeyMapping.cs:45-48`). A unique index that includes a nullable
`owner_id` also sets `.AreNullsDistinct(false)` so two global rows cannot share a name (precedent
`WorkspaceSettingMapping.cs:30`); no more raw-SQL `_global` indexes (`20260629130828:147-149` is
the anti-precedent, fixed by DB-05).

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

## R14. Identity of users

`users.id int` is the PK; `users.public_id uuid` is the identity everything else refers to
(`comments.author_id`, JWT `sub`, `created_by/updated_by/deleted_by`). New references to a user
use `public_id` and say so in the mapping comment. `api_keys.user_id → users.id` is the historical
exception; do not add another.

## R15. Never edit `clients/`, never hand-edit `*.Designer.cs` or the snapshot

Generated files are regenerated. If the snapshot is wrong, fix the mapping and re-run the
migration tooling.
