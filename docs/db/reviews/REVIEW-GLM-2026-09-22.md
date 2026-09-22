# Independent DB Review — GLM — 2026-09-22

Reviewer: GLM (independent senior DB architect pass, adversarial).
Scope: `docs/db/DB-REVIEW-2026-09-22.md`, `DB-RULES.md`, `SCHEMA.md`, all 8 docs in `docs/db/execution/`,
verified against Domain entities, Infrastructure mappings, `AppDbContext`, the migrations folder/snapshot,
`TenantStamp`, `Program.cs`, `scripts/backup-db.sh`, `scripts/deploy-api.sh`, `DEPLOY.md`,
`Tests/MigrationSafetyTests.cs` (shipped DB-02) and
`Infrastructure/Migrations/20260922080137_DropShadowProjectAppUrlProjectId1.cs` (shipped DB-04).
Static read-only review; no commands that build, test, or touch git/docker/dotnet/npm were run.

**Overall:** the review under review is unusually accurate. Every load-bearing claim I spot-checked
reproduced exactly (boot chain `API/Program.cs:165-174`; six-table hard delete
`Application/Services/Implementation/TenantService.cs:382-440`; hourly demo sweep with failure logging
`API/Hosted/DemoCleanupService.cs:20-24,78-92`; raw `_global` indexes `20260629130828_AddTenancy.cs:147-149`
and `20260629131255_AddTenantRoleNameIndex.cs:24`; unguarded drops `20260701163243_MultiSelectPickedActions.cs:13,17`;
`gen_random_uuid()` backfill `20260827130246_EnforceOwnerIdNotNull.cs:24-30`; empty `Down()`
`20260911170828_MigrateDefaultProjectAppUrlsToLocal.cs:48-51`; the four mint points — the DB-03 grep
fact matches the tree line-for-line; shadow-column fix shipped at `ProjectAppUrlMapping.cs:22-26`;
`postgres:15` / `DBMigrationEnabled: "true"` / `StrictNullTenantIsolation` at
`docker-compose.prod.yml:6,26,30`). The findings below are therefore mostly about **staleness,
implementability by a mid-tier model, and enforcement gaps**, plus adjudication of the AGY review.

---

## Findings

### A. Claims in the review/docs that are wrong, stale, or unsupported

**A1 — Minor — "Restore has never been executed" is stale.**
`DEPLOY.md:128-131` now records a completed local rehearsal ("Last rehearsed: 2026-09-22 … 26 tables,
all 58 migration rows, row counts matched"). DB-REVIEW P0-1 and DB-01 §1/§5(task 5)/§7(criterion 4)
still treat the restore as never-executed.
*Change:* re-scope DB-01 to its two remaining deliverables (off-box copy + uploads archive) and note the
rehearsal is done; update the P0-1 row to "rehearsed locally 2026-09-22; prod restore still unexercised".

**A2 — Minor — post-DB-04 doc drift.**
The tree now has **59** migrations (verified count), not 58; DB-02's acceptance criterion 4
("still lists 58 migrations") is stale. `SCHEMA.md:30` still lists the shadow `ProjectId1` as present
("(DB-04 drops)") although the snapshot no longer contains it (`rg ProjectId1 AppDbContextModelSnapshot.cs`
→ 0 hits) and the mapping fix is merged.
*Change:* one-line edit to SCHEMA.md's `project_app_urls` row; add a note to DB-02 that the frozen
baseline (58) predates `20260922080137_…`, which passes the guard via its approval marker (verified:
`20260922080137_DropShadowProjectAppUrlProjectId1.cs:11` satisfies the marker regex in
`Tests/MigrationSafetyTests.cs:80-83`).

**A3 — Minor — S-4's exact counts do not reproduce and should not be cited as constants.**
My recount over `Application/ API/ Infrastructure/` today: **233** `DeletedAt == null` predicates and
**148** `IgnoreQueryFilters()` calls — matching neither the review's 229/162 nor AGY's 231/178 (see
cross-check). Counts drift with every merge.
*Change:* in DB-REVIEW S-4, either drop the exact numbers or prefix "as of `03093ad`".

**A4 — Minor — the shipped DB-02 guard has two holes the doc doesn't mention.**
(a) A migration file with no `void Down(` is skipped entirely (`Tests/MigrationSafetyTests.cs:124-126`
`if (!upToDown.Success) continue;`) — a hand-written migration without `Down` bypasses the scan.
(b) The risky-op regex (`:76`) misses `DropCheckConstraint`, `DropSequence`, `AlterDatabase`.
*Change:* add a hardening note/task to DB-02: require `void Down(` to exist for non-baseline files, and
extend the alternation. Low urgency — historical precedent is scaffolded files.

### B. Problems the review missed

**B1 — Major — DB-03's "expected new indexes" list is wrong; the doc's stop-rule will misfire.**
DB-03 §3.2 predicts new conventional `IX_*_owner_id` indexes only for `extension_sites`,
`role_tenant_overrides`, `subscriptions` ("expect none") and then says *"Any other new index in the
generated migration → stop and report."* In fact at least **four more** of the 23 FKs will each emit a
new owner index, because these mappings declare no index with `owner_id` as leading column:

| Table | Evidence (mapping has only) | FK will add |
|---|---|---|
| `api_keys` | hash + user_id indexes (`ApiKeyMapping.cs:39-48`) | `IX_api_keys_owner_id` |
| `device_logins` | `ux_device_logins_device_code_hash`, `ix_device_logins_user_code` (`DeviceLoginMapping.cs:35,39`) | `IX_device_logins_owner_id` |
| `quick_access_links` | `IX_quick_access_links_token_hash` (`QuickAccessLinkMapping.cs:35`; `20260912120127:51`) | `IX_quick_access_links_owner_id` |
| `project_builds` | `(project_id, sha)` (`ProjectBuildMapping.cs:32`; `20260912193031:65`) | `IX_project_builds_owner_id` |

Every other owner-carrying table already has an owner-leading index (verified census: `users`
`UserMapping.cs:40`, `roles` `:31`, `app_environments` `:24`, `status_presentations` `:25`,
`page_context_snapshots` `:46`, `invites` `:36`, `replies` `:28`, `ai_rules` `:29`,
`predefined_actions` `:36`, `predefined_action_suggestions` `:34`, `usage_events` `:42`,
`extension_sites` `:26`, `subscriptions` `:24`, `workspace_settings` `:30`, `comments`, `notifications`
`:39`, `project_app_urls` `:43`, `projects` `:45`). A literal implementer following DB-03 task 6
**will** see four "unexpected" `CreateIndex` operations and either stall on the stop-rule or, worse,
"fix" the diff by deleting them.
*Change:* amend DB-03 §3.2 and acceptance criterion 2 to enumerate all expected new indexes
(role_tenant_overrides + api_keys + device_logins + quick_access_links + project_builds, extension_sites
optional, subscriptions none) and re-scope the stop-rule to anything beyond that list.

**B2 — Major — nothing enforces R7's "explicit step": marked destructive migrations still auto-apply on boot.**
`scripts/deploy-api.sh:26-27` does `up -d --build api`, and `API/Program.cs:165-169` runs `MigrateAsync()`
unconditionally when `DBMigrationEnabled=true` (prod: `docker-compose.prod.yml:26`). The DB-02 marker
documents approval but does not gate execution; DB-RULES R7 (`:87-88`) concedes "the reviewer of the PR
is the gate." A DB-07-class drop merged with its marker still ships unattended on the next ordinary
deploy. The mitigations (pre-deploy dump, R7 procedure) are real, but this is the un-enforced residual
core of P0-4.
*Change:* extend DB-02 (additive) with one mechanical gate, e.g. `deploy-api.sh` renders
`dotnet ef migrations script` and refuses to proceed when it contains `DROP COLUMN|DROP TABLE|DROP INDEX|`
`ALTER TABLE ... DROP` unless `POINTER_DESTRUCTIVE_OK=1` is exported; or `Program.cs` refuses to
`MigrateAsync()` past a marked migration without `DBApplyContractMigrations=true`. Keep it out of DB-03's
critical path; it can land any time before DB-07.

**B3 — Major — DB-01 adds copying but no failure/freshness signal.**
Today cron appends to `~/backups/backup.log` (`DEPLOY.md:104`); DB-01's offsite step only echoes
`offsite copy FAILED` to stderr (DB-01 §5.1). A silently-broken nightly job (full disk, expired creds,
VM rebooted) voids both P0-1 and P0-2 fixes, and pre-launch nobody reads the log.
*Change:* add to DB-01: (a) `deploy-api.sh` refuses to deploy if the newest `pointer-*.dump` is older
than ~26 h (one `find -mmin` guard — deploys already depend on backups existing); (b) the offsite step
pings a healthcheck URL (UptimeRobot et al.) on success, or at minimum the doc schedules a weekly
`rclone ls` eyeball. Cheap, and it converts the backup from hope into a monitored system.

**B4 — Minor — DB-03's Migration-1 recipe requires hand-surgery on generated files.**
§3.3 step 3 / tasks 6-7 instruct the implementer to *move* the generated `AddForeignKey`/`CreateIndex`
operations out of `AddWorkspaces` into a second, empty-scaffolded migration. A mid-tier model can lose
operations, mangle `Up`/`Down` pairs, or be tempted to touch `.Designer.cs`.
*Change:* replace with a comment-out recipe: write all 23 FK mapping lines but keep them commented out →
`just migrate name="AddWorkspaces"` (scaffolds CreateTable + backfill only) → uncomment the 23 lines →
`just migrate name="AddWorkspaceForeignKeys"` (scaffolds exactly the FKs + indexes). Zero hand-moving;
R13's "read the generated file" check still applies to both.

**B5 — Minor — DB-03 3a misses soft-deleted founding admins.**
3a selects Workspace Admins with `u.deleted_at IS NULL` (DB-03 §3.3). A workspace whose admin rows are
all soft-deleted falls into the 3b "Recovered" bucket even though the tenant is perfectly reachable via
its `owner_id` rows. Harmless (Q2's count covers it), but the doc should say so or the rehearsal's
`Recovered` count will be misread as data corruption.
*Change:* one sentence in §3.3 + Q2.

**B6 — Minor — shipped DB-04 nits.**
The approval marker names an agent, not the owner ("approved 2026-09-22 by orchestrator (owner
instruction …)", `20260922080137_…cs:11`); and `Down()` re-adds the shadow FK **without** the original
`onDelete: Restrict` (`:41-46` vs `20260911223426_AddTenantInviteFields.cs:38-40`) — `NO ACTION` ≈
`RESTRICT` for non-deferred constraints, so cosmetic. Frozen file; note only, do not edit.

**B7 — Minor — `project_app_urls.app_environment_id` Cascade is a latent cross-tenant footgun the review documents but never flags.**
`ProjectAppUrlMapping.cs:29-32` cascades from `app_environments`, whose global rows are shared by every
tenant. Today it is unreachable: the only delete path is a **soft** delete guarded by an in-use check
(`AppEnvironmentService.cs:101-124`, `:113-117`), soft deletes don't fire FK cascades, and the only hard
delete of environments is the tenant-scoped ordered routine (DB-03 deletes `ProjectAppUrl` before
`AppEnvironment`). But one future hard-delete endpoint (or manual SQL) turns "delete the global `prod`
environment" into wiping every tenant's URLs.
*Change:* add one line to DB-06 §10 or the review: "leave the Cascade only while no hard-delete path
exists for global environments; switch to `Restrict` the day one appears."

**B8 — Minor — DB-01's uploads tar has no consistency or size story.**
`docker run --rm … tar -C /u` on a live volume can capture partially-written uploads; there is no size
guard before `rclone`. Acceptable pre-launch.
*Change:* one sentence in DB-01 §3: uploads are archived best-effort (torn files possible); note the
volume size is expected to stay small until §33 blob deletion ships.

### C. DB-RULES sufficiency (PG15 + EF Core 8)

**C1 — Note — rules are correct; two small completions.**
R4's `CREATE INDEX CONCURRENTLY … suppressTransaction: true` is the right EF pattern (Npgsql wraps each
migration in a transaction; `suppressTransaction` opts out). R9's `AreNullsDistinct(false)` matches the
shipped precedent (`WorkspaceSettingMapping.cs:30` → `20260921215630:42-48`) and requires PG15+
(`docker-compose.prod.yml:6`). R3's "batched backfills cannot live in `migrationBuilder.Sql`" is correct
for the same transaction reason. Two additions would make R4 complete: (a) a failed `CONCURRENTLY` build
leaves an **INVALID** index — the rule should mandate `DROP INDEX IF EXISTS` + retry and discourage
mixing concurrent builds with other operations in one migration; (b) state that `VALIDATE CONSTRAINT`
cannot be skipped forever — a `NOT VALID` FK that is never validated still enforces new writes but
hides existing orphans from the planner's cascade reasoning. The real gap in the rules is enforcement
(B2), not correctness.

### D. Execution order

**D1 — Minor — order is right in shape; two adjustments.**
`DB-01 → DB-02 → DB-04 → DB-05 → DB-03 → DB-06 → DB-07 → DB-08` is the right dependency spine
(backups and the guard before any migration; warm-ups before DB-03; DB-06 after DB-03's single root;
DB-08 last, gated on product numbers). Adjust: (a) DB-04 is **shipped** — drop it from the queue;
(b) pull Q7's ~20-line CI `postgres:15` job (`dotnet ef database update` from empty + the DB-02 guard)
to immediately after DB-02. P0-5 correctly says first contact between a new migration and Postgres is
production boot; DB-03/05/06 all depend on hand-verified scaffolding, which is exactly what that CI job
would catch before the rehearsal. The planned product rename needs no schema work (R10/§11.1) — the
order is unaffected by it.

---

## Cross-check of REVIEW-AGY-2026-09-22.md

| AGY finding | My position |
|---|---|
| 1.1 `PredefinedActionSuggestion` filter is a Blocker tenant-isolation leak (`AppDbContext.cs:108-111`) | **Disagree.** The `(TenantId == null && !strict && OwnerId == null)` branch is the codebase-wide legacy null-bucket branch, documented at `AppDbContext.cs:11-17` and in `SCHEMA.md:19`, disabled in prod by `Tenancy:StrictNullTenantIsolation=true` (`docker-compose.prod.yml:30`), and present verbatim on ~20 strict-own entities (`:77-94`, `:130`, `:138-147`). It exposes only null-owner rows to null-tenant callers when strict is off, and null-owner suggestions are never written (`:108-110`). No cross-tenant path. At most a comment-accuracy nit on that one entity. |
| 1.2 `ProjectAppUrl → AppEnvironment` Cascade is a Blocker that wipes every tenant's URLs | **Disagree with severity/claim; agree there's a latent footgun.** The only delete path is soft and in-use-guarded (`AppEnvironmentService.cs:113-119`); soft deletes never fire FK cascades. The review did document the cascade (`SCHEMA.md:31`). Downgraded to my B7 (Minor, defense-in-depth). |
| 1.3 `plans`/`app_settings` unique indexes excluded from DB-05 though both are `BaseEntity` | **Agree in substance** (burn-on-soft-delete is real; fix is two lines inside DB-05). Severity depends on whether any soft-delete path exists for those tables — **not checked** by me. Cheap enough to just fold in. |
| 1.4 DB-02 is "falsely claimed" as a safety guard; marked destructive migrations still auto-apply | **Partially agree.** The enforcement gap is real (my B2) but the "falsely claims" framing is wrong: DB-02's stated goal is the marker gate, and R7 documents the explicit-step procedure; what's missing is a mechanical gate, which is an additive follow-up, not a Blocker refutation. |
| 2.1 Real counts are 178 `IgnoreQueryFilters()` / 231 `DeletedAt == null` | **Not verified / did not reproduce.** My recount over `Application+API+Infrastructure`: **148 / 233**. Neither AGY's numbers nor the review's reproduce today; exact counts are unstable (my A3). AGY asserting equally-precise different numbers is the same mistake the review made. |
| 3.1 DB-03's "loop over `Type[]`" + generic helper is self-contradictory and won't compile if taken literally | **Agree.** DB-03 §3.4 (`:118-120`) says both "loop over a `Type[]`" and "the helper is called once per type". Fix as AGY suggests: 22 explicit `await DeleteOwnedAsync<T>(…)` calls; keep `HardDeleteOrder` for the reflection test only. |
| 3.2 DB-RULES correct for PG15/EF8 | **Agree**, with my C1 completions. |
| §4 verdicts (DB-01/04/07/08 safe; 02/03/05/06 need edits) | **Partially agree** — see verdict below (differs on DB-01 and DB-06). |

---

## Verdict

| Doc | Verdict |
|---|---|
| **DB-01** | **Needs edits first** — small ones. Re-scope (rehearsal already done, A1); add the freshness/failure gate (B3) and the uploads-tar caveat (B8). The rclone/off-box core is implementable as written. |
| **DB-02** | **Shipped and sound.** Hand further work (A4 hardening, B2 enforcement gate) back to the same doc as additive tasks; update its stale "58" acceptance (A2). |
| **DB-03** | **Needs edits before handing to any implementer.** B1 (wrong expected-index list — will false-stop or corrupt the diff), B4 (hand-moving generated ops), AGY 3.1 (loop contradiction), B5 (Recovered-bucket caveat). The design itself (uuid PK = today's `owner_id`, two migrations, mint points, ordered delete + reflection test) is correct and well-evidenced. |
| **DB-04** | **Done.** Shipped code matches the doc (verified mapping, migration, marker, snapshot). Residual nits are documentation-only (A2, B6). |
| **DB-05** | **Safe to hand over after a two-line decision**: fold `plans`/`app_settings` in or keep them out with a stated reason (AGY 1.3). Everything else verified: the seven mappings match the doc's citations, the DropIndex names exist (`IX_users_email_owner_id` etc., `20260629130828:91-107`), the precedent and pre-checks are correct for PG15. |
| **DB-06** | **Safe to hand over as-is** (AGY's "must add the Restrict fix" is downgraded to B7's one-line note — the Cascade is unreachable today). Column inventory, cascade choices, orphan pre-checks, and the FK-behaviour table all verified against the mappings. Q4 must be answered first, as the doc says. |
| **DB-07** | **Safe to hand over as-is.** Verified `User.cs:33-40`, `UserMapping.cs:37-38`, `ApiKeyBackfillService.cs:31-34,69`, `Program.cs:171-173`. Correctly classified Destructive with approval line, pre-check, and honest rollback. |
| **DB-08** | **Safe to hand over as-is** (ships disabled; Q8 numbers gate enabling, by design). The `first_comment`/`first_apply` exclusion, live-comment snapshot guard, batching loop, and the InMemory/relational split are all correctly designed. |

**Should not be done:** none of the eight. The one thing that should **not proceed as written** is
DB-03 in the hands of a mid/low-tier implementer before B1/B4/AGY-3.1 are fixed — it is the only doc
whose failure mode is a botched structural migration under auto-apply, rather than a caught error.

*Report written incrementally per the hard constraints; 46 read-only tool calls, all under the repo root.*
