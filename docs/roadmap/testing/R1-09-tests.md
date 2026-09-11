# R1-09-tests — Project URLs belong to enabled workspace environments

## Covers
Acceptance criteria of [`../execution/R1-09-project-environment-urls.md`](../execution/R1-09-project-environment-urls.md):
AC-1 (fresh seed has `local`, no `default`) → **R1-09-01** · AC-2/AC-3/AC-4 (migration, unique-index
conflict, idempotence, tenant-owned `default` untouched) → **R1-09-02** (nightly `upgrade` job) ·
AC-5 (create with no environment id lands on `local`) → **R1-09-03** · AC-6 (explicit / disabled /
foreign environment id) → **R1-09-04** · AC-7 (`PUT …/app-urls` enabled vs disabled) → **R1-09-05** ·
AC-8 (disable with URLs attached, then re-enable) → **R1-09-06** · AC-9 (extension lookup and the
widget gate under a disabled environment) → **R1-09-07** · AC-10 (quick-access invite still works
post-migration) → **R1-09-08** · AC-11 (tenant-defined `qa` end-to-end + tenant-B isolation) →
**R1-09-09**, **R1-09-10**.

AC-12 (`just fmt` / `build` / `test` / dashboard regen) is a build gate, not an E2E scenario.

## Preconditions
- Prerequisite docs merged: **R1-09** itself. No dependency on R1-02, R1-07 or the mail harness —
  **no scenario here sends e-mail**, so the `email_enabled` DB toggle (00-HARNESS §3) is irrelevant and
  the **`signup` budget spend for this whole document is 0** (nothing touches `register`,
  `forgot-password`, `reset-password`, `register-admin`, `register-invite` or `GET /api/invites/{code}`).
  R1-09-08 creates a quick-access **invite** via `POST /api/admin/invites`, which is `Policies.Admin`
  and carries no rate-limit policy — verified against `API/Controllers/Admin/InvitesController.cs`.
- Seed complete (`state/credentials.json`, `state/keys.json`). Personas used: `TENANT_OWNER` (WA —
  owns `e2e-alpha`/`e2e-beta`), `USERS.developer` (DEV — non-admin negative), `TENANT_B_OWNER` (TB —
  cross-tenant), `SUPER_ADMIN` (SA — the global catalog).
- **Project isolation:** every scenario that writes URLs or comments uses its **own** project
  `e2e-r109-<runId>`, created 409-tolerantly (the pattern at `e2e/widget/widget.spec.ts:22-36`).
  Nothing here touches `e2e-alpha` — `e2e/scripts/probe-visibility.mjs:51-70` asserts exact set
  equality on alpha's comments, and `e2e/scripts/seed.mjs:67,71` owns alpha's and beta's `appUrl`.
  **R1-09-06 is the one exception** and it only *reads* alpha (see its Flake note).
- **Environment isolation:** scenarios create their own tenant-owned environments named
  `r109-<runId>-<n>`. They must **never** disable a *global* environment (`local`, `prod`, `staging`,
  `testing`) — that is workspace-wide for every tenant in the database and would poison the rest of the
  run. Decision 10 makes it a 403 for a tenant anyway; only SA could, and no scenario asks SA to.
- `lib/api.mjs` helpers required: the existing `get`/`post`/`patch`/`put`/`del` plus
  **`postRaw`/`putRaw`/`delRaw`** returning `{ status, body }` without throwing (00-HARNESS §4) —
  every negative below asserts an exact status.
- Layer note: this document is almost entirely **api** layer. Only R1-09-07 opens a browser, and only
  to prove the widget gate; everything else is cheaper as a node spec.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-09-01 | fresh seed has `local`, not `default` | PR | api | WA, SA | 1. WA `GET /api/admin/environments`. 2. Filter `isGlobal === true`. 3. SA `GET /api/admin/environments` (super admin sees every tenant's too — filter `isGlobal`). | 2 → names include `local`, `prod`, `staging`, `testing`; **either no global `default` row at all (fresh DB) or exactly one with `isEnabled === false && isRetired === true`** (upgraded DB — both are valid, the assertion is "never an enabled global `default`"). Every global row has `isEnabled === true` except a retired `default`; `canManage === false` for WA on globals, `true` for SA. 3 → same global set. | report row listing the global names + flags |
| R1-09-02 | `default` URLs migrate to `local`, idempotently | nightly (`upgrade` job) | api | WA | Runs in the two-image `upgrade` job R1-06-tests defines (same `LEGACY_REF` / `pointer-api:legacy` → `:candidate` swap, **same volume, never `down -v` between halves**). **Legacy half:** 1. boot `:legacy`, seed. 2. WA `POST /api/admin/projects { key: 'e2e-r109-mig-a-<runId>', name: 'Mig A', appUrl: 'https://mig-a.test' }` (legacy code writes a `default` row). 3. WA creates `e2e-r109-mig-b-<runId>` the same way, then `PUT /api/admin/projects/{b.id}/app-urls/{localId}` — **skip if `:legacy` predates `local`; instead create the conflict by `PUT`ting to the `default` env id explicitly and, on the candidate half, seeding a `local` row before the migration cannot be done** → *Decision:* build the conflict row on the **candidate** half instead is impossible (the migration runs at boot), so build it on the legacy half by `PUT`ting a URL to **both** the `default` env id **and** a tenant-owned env the migration ignores, and assert only the non-conflict path here; the unique-index conflict branch is covered by the unit test `DefaultEnvironmentMigrationTests` named in the execution doc. 4. WA `POST /api/admin/environments { name: 'default' }` → tenant-owned `default` (id `tOwn`); `PUT /api/admin/projects/{a.id}/app-urls/{tOwn}` with `https://tenant-default.test`. 5. Record `GET /api/admin/projects/{a.id}/app-urls`. **Candidate half:** 6. `docker compose stop api` → `API_TAG=candidate … up -d api` → re-wait `/swagger/v1/swagger.json` (≤120 s; boot runs migrations then the seeder). 7. WA `GET /api/admin/projects/{a.id}/app-urls`. 8. WA `GET /api/admin/environments`. 9. Restart the api container once more (`stop` + `up -d`, same tag) and repeat 7-8. | 7 → the `https://mig-a.test` row is now on the environment named `local`, and **no row** on a global `default`; the tenant-owned `default` row (`https://tenant-default.test`) is **still present and unchanged**. 8 → global `default` is `isEnabled:false, isRetired:true`; the tenant's own `default` is `isEnabled:true, isRetired:false` (AC-4). 9 → byte-identical to 7/8 (idempotent). | report row + both `app-urls` payloads pasted into `detail`'s linked log (not `detail` itself — 00-HARNESS §11 forbids volatile values there) |
| R1-09-03 | create with a URL and no environment id → `local` | PR | api | WA | 1. WA `GET /api/admin/environments` → `localId` = id of the global `local`. 2. WA `POST /api/admin/projects { key: 'e2e-r109-a-<runId>', name: 'R109 A', appUrl: 'https://r109-a.test' }` (no `appEnvironmentId`). 3. WA `GET /api/admin/projects/{id}/app-urls`. | 2 → 200; response `appUrls` has **exactly one** row: `appEnvironmentId === localId`, `environmentName === 'local'`, `url === 'https://r109-a.test'`, `isActive === true`, `environmentIsEnabled === true`; `appUrl === 'https://r109-a.test'` (the deprecated mirror, Decision 4). 3 → same single row. | report row |
| R1-09-04 | create with an explicit / disabled / foreign environment id | PR | api | WA, TB | 1. WA `POST /api/admin/environments { name: 'r109-<runId>-staging2' }` → `envId`. 2. WA `postRaw /api/admin/projects { key: 'e2e-r109-b-<runId>', …, appUrl: 'https://r109-b.test', appEnvironmentId: envId }`. 3. WA `PATCH /api/admin/environments/{envId} { isEnabled: false }`. 4. WA `postRaw /api/admin/projects { key: 'e2e-r109-c-<runId>', …, appUrl: 'https://r109-c.test', appEnvironmentId: envId }`. 5. TB `POST /api/admin/environments { name: 'r109-<runId>-tb' }` → `tbEnvId`; WA `postRaw /api/admin/projects { key: 'e2e-r109-d-<runId>', …, appUrl: 'https://r109-d.test', appEnvironmentId: tbEnvId }`. 6. WA `postRaw` the same but `appEnvironmentId: 999999`. 7. WA `postRaw /api/admin/projects { key: 'e2e-r109-e-<runId>', name: 'R109 E' }` (no `appUrl`, but `appEnvironmentId: envId` — the disabled one). | 2 → 200, single `appUrls` row on `envId`. 4 → **400**, message key `AppEnvironment.NotEnabled`, and `GET /api/admin/projects` shows **no** project `e2e-r109-c-<runId>` (the create is rejected, not half-applied). 5 → **404** `AppEnvironment.NotFound` (tenant B's environment is invisible, not forbidden). 6 → **404**. 7 → **200** — no `appUrl` means `appEnvironmentId` is ignored entirely (Decision 5), `appUrls` is empty. | report row; the four status codes in the CI log |
| R1-09-05 | `PUT …/app-urls/{envId}` respects the enabled flag | PR | api | WA, DEV | Using `e2e-r109-a-<runId>` from R1-09-03 and a fresh env `r109-<runId>-qa` (id `qaId`): 1. WA `put /api/admin/projects/{a.id}/app-urls/{qaId} { url: 'https://r109-qa.test', isActive: true }`. 2. WA `GET …/app-urls`. 3. WA `PATCH /api/admin/environments/{qaId} { isEnabled: false }`. 4. WA `putRaw …/app-urls/{qaId} { url: 'https://r109-qa-2.test' }`. 5. WA `GET …/app-urls`. 6. DEV (non-admin, not the creator) `putRaw …/app-urls/{qaId} { url: 'https://x.test' }`. | 1 → 200. 2 → two rows (`local`, `qa`), both `environmentIsEnabled: true`. 4 → **400** `AppEnvironment.NotEnabled`; the stored URL is still `https://r109-qa.test` (the write did not partially apply). 5 → still two rows; the `qa` row present with `environmentIsEnabled: false` and its original url — **blocked, not hidden** (Decision 1). 6 → **403** (the existing `ProjectService.SetAppUrlAsync` admin-or-creator gate, unchanged by this item). | report row |
| R1-09-06 | disabling an environment with URLs, and re-enabling | PR | api | WA | 1. WA `GET /api/admin/environments` → note `projectUrlCount` for `qaId` (from R1-09-05). 2. WA `PATCH /api/admin/environments/{qaId} { isEnabled: true }` → re-enable. 3. WA `put …/app-urls/{qaId} { url: 'https://r109-qa-3.test' }`. 4. WA `PATCH /api/admin/environments/{qaId} { isEnabled: false }`. 5. WA `GET /api/admin/environments`. 6. WA `delRaw /api/admin/environments/{qaId}`. 7. WA `PATCH /api/admin/environments/{qaId} { isEnabled: true }` (restore, for a clean tree). | 1 → `projectUrlCount >= 1`. 2 → 200, `isEnabled: true`. 3 → **200** — re-enabling restores writes. 4 → 200. 5 → the row has `isEnabled: false` and the **same** `projectUrlCount` (disabling deletes nothing). 6 → **409** `AppEnvironment.InUse` — `DeleteAsync` still refuses an in-use environment (execution Decision 9, `AppEnvironmentService.cs:97-102`); disabling is the soft alternative. | report row |
| R1-09-07 | disabled environment: extension lookup misses, widget gate stays active | nightly | api + widget | WA, TESTER | **api half:** 1. WA creates `e2e-r109-w-<runId>` with `appUrl: 'http://localhost:<PORTS.beta>'` (no env id → `local`). 2. WA `POST /api/admin/environments { name: 'r109-<runId>-ext' }` → `extId`; `put …/app-urls/{extId} { url: 'https://r109-ext.test' }`. 3. WA `POST /api/extension/projects/lookup { origin: 'https://r109-ext.test' }` (the `FindProjectForOriginAsync` route — resolve its exact path from `API/Controllers/ExtensionController.cs` at implementation time). 4. WA `PATCH /api/admin/environments/{extId} { isEnabled: false }`; repeat step 3. **widget half:** 5. `node e2e/fixture-app/serve.mjs beta <PORTS.beta>` already runs for R1-05-06 — reuse it; the page's project attribute is overridden to `e2e-r109-w-<runId>` via `?project=` (smoke fixture only) **or**, if the beta fixture has no `?project=` support, serve a copy through `e2e/scripts/serve-dir.mjs` on `PORTS.beta` with the key inlined. 6. `GET /api/public/projects/e2e-r109-w-<runId>/widget-status?origin=http://localhost:<PORTS.beta>` before and after disabling `local`… **Decision: do not disable `local`** (global, would affect every tenant). Instead: `put …/app-urls/{extId} { url: 'http://localhost:<PORTS.beta>', isActive: true }` so the fixture origin is described by the *tenant-owned* `ext` environment, then toggle **that**. 7. With `ext` **enabled** and its row `isActive: false` → `GET …/widget-status?origin=…`. 8. With `ext` **disabled** (row still `isActive: false`) → same call. | 3 → 200, `key === 'e2e-r109-w-<runId>'`. 4 → **404** `Project.NoneForOrigin` — a URL on a disabled environment no longer resolves (AC-9, and the documented live behaviour change). 7 → `active: false` — an explicit per-mapping `isActive: false` on an **enabled** environment still blocks. 8 → **`active: true`** — a row whose environment is disabled is treated as if it did not exist, never as a block (execution Decision 7; disabling an environment must not take a customer's widget offline). | report row; both `widget-status` payloads |
| R1-09-08 | quick-access invite still works after the migration | nightly | api | WA | 1. WA `GET /api/admin/projects` → pick `e2e-r109-a-<runId>` (its URL arrived via the `local` default path, the same shape the migration produces). 2. WA `POST /api/admin/invites { projectId: a.id, email: 'r109-cl-<runId>@example.com', roleId: <QuickAccess role id>, createNewWorkspace: false }` — resolve the QuickAccess role id from `GET /api/admin/roles`. 3. Inspect the response. | 2 → **200**, not `Invite.QuickAccessAppUrlRequired`. `InviteService.cs:525-526` reads `Project.AppUrl`, which Decision 4 keeps mirroring the `local` row — this scenario is the guard against the deprecation breaking invites. 3 → the response's app-url field equals `https://r109-a.test`. | report row |
| R1-09-09 | a tenant-defined environment works end-to-end | PR | api | WA | 1. WA `POST /api/admin/environments { name: 'r109-<runId>-qa2' }` → 200, `isGlobal: false`, `canManage: true`, `isEnabled: true`, `isRetired: false`, `projectUrlCount: 0`. 2. WA `put /api/admin/projects/{a.id}/app-urls/{id} { url: 'https://r109-qa2.test' }`. 3. WA `GET …/app-urls`. 4. WA `GET /api/admin/environments`. | 2 → 200. 3 → the row is listed with `environmentName === 'r109-<runId>-qa2'` and `environmentIsEnabled: true`. 4 → that environment's `projectUrlCount === 1`; global rows still listed first, then tenant rows by name, retired last. | report row |
| R1-09-10 | cross-tenant isolation | PR | api | TB, WA | 1. TB `GET /api/admin/environments` → the global set **plus only TB's own** rows. 2. TB `getRaw /api/admin/projects/{a.id}/app-urls` (WA's project). 3. TB `putRaw /api/admin/projects/{a.id}/app-urls/{qaId} { url: 'https://evil.test' }`. 4. TB `patchRaw /api/admin/environments/{qaId} { isEnabled: false }` (WA's environment). 5. TB `patchRaw /api/admin/environments/{localId} { isEnabled: false }` (the **global** `local`). 6. WA `GET …/app-urls` for its project. | 1 → contains no `r109-<runId>-*` environment created by WA. 2 → **404** (`ProjectAppUrl` is strict-own; the project itself is invisible). 3 → **404**. 4 → **404** (WA's environment is outside TB's own-plus-global view). 5 → **403** `AppEnvironment.NotManageable` — a tenant may not disable a global environment for everyone (execution Decision 10); the row must still read `isEnabled: true` afterwards. 6 → unchanged from R1-09-09 step 3 — nothing TB did mutated WA's data. | report row; the five status codes |

## Spec files
- `e2e/api/project-env-urls.spec.mjs` — R1-09-01, 03, 04, 05, 06, 09, 10 (node `node:test`, `lib/api.mjs`,
  `lib/report.mjs`). Runs in the **api** phase, before the widget phase.
- `e2e/api/project-env-urls-nightly.spec.mjs` — R1-09-08 (nightly api phase).
- `e2e/widget/project-env-urls.spec.ts` — R1-09-07's widget half; reuses `preAuthWidget` from
  `e2e/widget/lib/auth.ts` (the extraction R3-01/R3-03/R3-04 also depend on — 00-HARNESS §4) and the
  `waitForResponse('**/capture-config')`-before-`goto` rule.
- `e2e/scripts/upgrade-assert.mjs` — **extend** with R1-09-02's assertions (the file R1-06-tests
  introduces for the two-image `upgrade` job; do not create a second job).
- New helpers: `postRaw`, `putRaw`, `delRaw`, `patchRaw`, `getRaw` in `lib/api.mjs` (00-HARNESS §4 lists
  `getRaw`/`postRaw`/`patchRaw`/`put`/`delRaw`; `putRaw` is added here).
- New `PORTS` entry: none — R1-09-07 reuses `PORTS.beta` (4182, R1-05-06) in the same nightly phase,
  sequentially.

## Not covered here
- **The unique-index conflict branch of the migration** (a project holding both a `default` and a
  `local` row) — unit-level in `Tests/DefaultEnvironmentMigrationTests.cs`. The legacy image in the
  `upgrade` job cannot reliably create that state (it predates `local`), so an E2E assertion would be
  vacuous. R1-09-02 says so explicitly rather than pretending.
- `Tests/ProjectAppUrlSyncTests.cs` rewrite, `Tests/AppEnvironmentEnabledFlagTests.cs`,
  `Tests/ProjectAppUrlEnvironmentGuardTests.cs`, `Tests/WidgetActivationTests.cs` extensions — unit
  level, named in the execution doc's Tests section.
- Dashboard behaviour (the create dialog rendering its environment-URL table by default, the disable
  confirm dialog, greyed rows) — `DASH-` prefixed, lives in the `pointer-dashboard` repo and reports
  **SKIP** when `DASHBOARD_DIR` is unset (00-HARNESS §10).
- `MaxEnvironments` enforcement — execution Decision 8 keeps it display-only, so there is nothing to
  assert. Do not add a cap scenario without changing that decision first.
- The fixed `EnvironmentTag` enum, comment tagging and the widget's environment switcher — a different
  concept, untouched by this item, covered by `Tests/ProjectPerEnvironmentActivationTests.cs` and
  `Tests/EnvironmentSelectorRoleTests.cs`.

## Flake notes
- **Never disable a global environment.** `local`/`prod`/`staging`/`testing` are shared by every tenant
  in the database; disabling one would break unrelated scenarios for the rest of the run. Only
  tenant-owned `r109-<runId>-*` environments are toggled, and R1-09-10 step 5 asserts a tenant *cannot*
  do it.
- **Ordering inside `project-env-urls.spec.mjs` is load-bearing**: 03 creates the project 05/06/08/09
  reuse, and 05 creates the `qa` environment 06 and 10 reference. Run them in file order with
  `concurrency: 1`; do not let the runner parallelise within the file.
- **R1-09-06 restores state** (step 7 re-enables `qa`) so 09/10 see a predictable catalog. If the
  scenario fails mid-way, the next run's `<runId>`-suffixed names avoid collision — the suite never
  relies on a clean catalog, only on its own rows.
- **R1-09-02 is the only scenario that restarts the API**, and it does so inside the dedicated
  `upgrade` job, which is outside the normal phase order (00-HARNESS §9's ≤3 restarts budget is not
  affected).
- **R1-09-07 must run after R1-05-06 releases `PORTS.beta`**, or serve its own copy via
  `serve-dir.mjs`. Both are nightly, so pin them to the same sequential widget sub-phase.
- No fixed sleeps anywhere; every assertion is a direct request/response. No mail, no rate-limit
  buckets, no `signup` spend — this document costs 0 tokens against 00-HARNESS §9's budget.
