# R1-03-tests — Dashboard quick-start prints the pre-filled CLI command

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-03-dashboard-quickstart-key.md`](../execution/R1-03-dashboard-quickstart-key.md).

## Covers

- The contract the dashboard regenerates from (inner-typed endpoints survive; Orval output stays
  clean) → **R1-03-01** (this repo — 00-HARNESS §10 layer 1).
- AC-1 (first primary step shows `npx -y pointer-feedback init --server … --key ptr_••••••••
  --project …`; copy yields the full unmasked command) → **R1-03-02**, implemented in the
  **pointer-dashboard repo** against this compose stack (00-HARNESS §10 layer 3; `DASH-` id).
- AC-2 (no key ⇒ no `--key` + hint), AC-3 (demo sessions keep email/password), AC-4 (curl under
  "Alternative without Node"), AC-5 (Arabic/RTL), AC-6 (`npm test` green) — dashboard-repo unit
  tests (`install-guide.spec.ts`); see Not covered here.

## Preconditions

- Seed complete (`credentials.json`, `keys.json` — `wsAdmin.apiKey` exists, `/^ptr_[0-9a-f]{40}$/`).
- R1-03-01: stack up; nothing else.
- R1-03-02: env `DASHBOARD_DIR` pointing at a checkout of
  [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard). When it is unset the
  scenario is reported **SKIP**, never silently green (00-HARNESS §10).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-03-01 | swagger contract guard (Orval surface) | PR | api | — | 1. `GET http://localhost:8090/swagger/v1/swagger.json` (anonymous). 2. For each `(method, path)` in `DASHBOARD_CONTRACT_ENDPOINTS`: `['get','/api/me/api-key']`, `['post','/api/me/api-key/regenerate']`, `['get','/api/branding']`, `['get','/api/admin/projects']`, `['post','/api/admin/projects']`, `['post','/api/auth/login']`, `['post','/api/auth/login-with-key']`, `['get','/api/projects/{key}/comments']`, `['post','/api/projects/{key}/comments']`, `['get','/api/meta']` (R1-04, same release): assert `spec.paths[path][method]` exists and `responses['200'].content['application/json'].schema.$ref` matches `^#/components/schemas/` (the AGENTS.md inner-type convention — a `typeof(Result<T>)` regression drops the `$ref`). 3. For each referenced component: `components.schemas[<Name>]` does **not** define properties `isSuccess`/`data` (not the `Result` envelope). 4. **Decision:** the list lives in `e2e/scripts/lib/constants.mjs` as `DASHBOARD_CONTRACT_ENDPOINTS`; a PR that adds a dashboard-consumed endpoint adds its row here in the same PR (the R1-01 freeze rule, applied to routes). | 1 → 200 with `paths` + `components.schemas`; 2 → every endpoint present with a `$ref`-shaped 200 schema; 3 → no envelope-shaped components in the list. | report row; missing names listed in `detail` |
| R1-03-02 | `quickstart-copies-prefilled-command` | nightly | dashboard | wsAdmin | Implemented in pointer-dashboard as **`DASH-R1-03-01`** (cross-referenced); this repo's runner reports the row. Steps (dashboard suite against this stack): 1. `run-e2e.sh` `dashboard` phase: with `DASHBOARD_DIR` set → `npm ci && npx playwright test` inside `$DASHBOARD_DIR`, env `POINTER_STACK=http://localhost:8090`, `POINTER_E2E_STATE=<repo>/e2e/state`. 2. Log in as `wsAdmin` from `credentials.json`; open Projects → `e2e-alpha` → install guide. 3. Assert the first primary step's `<code>` renders `npx -y pointer-feedback init --server http://localhost:8090 --key ptr_•••••••• --project e2e-alpha` (masked). 4. Click copy → context launched with `grantPermissions: ['clipboard-read','clipboard-write']` → `page.evaluate('navigator.clipboard.readText()')` contains `--key ${keys.json.wsAdmin.apiKey}` (full key — AC-1). 5. Assert an "Alternative without Node" section contains `curl -fsSL http://localhost:8090/install.sh | sh`. | with `DASHBOARD_DIR`: steps 2–5 pass. Without: `report.record('R1-03-02','nightly','dashboard','wsAdmin','SKIP',…,'DASHBOARD_DIR unset')` — a SKIP row, never green. | report row (PASS or SKIP) + dashboard suite trace path on failure |

## Spec files

- `e2e/api/swagger-guard.spec.mjs` — R1-03-01 (plain node fetch on `/swagger/v1/swagger.json`;
  `DASHBOARD_CONTRACT_ENDPOINTS` in `lib/constants.mjs`).
- Dashboard side (separate repo): `angular/src/app/shared/install-guide/install-guide.e2e.spec.ts`
  running against this stack, consuming `e2e/state/credentials.json` + `keys.json`.
- `run-e2e.sh` gains a `dashboard` phase gated on `DASHBOARD_DIR` (records the R1-03-02 row either
  way).

## Not covered here

- `initCommand()` unit matrix (with/without key/project, environment flag, masking helper), steps
  order, demo-session credentials step, reveal toggle, curl-fallback ordering, i18n key parity +
  RTL — `install-guide.spec.ts` / `install-guide.service.spec.ts` in pointer-dashboard; they need no
  API stack.
- Key minting/reveal behaviour itself — H-03 (all personas have keys) and R1-06-03/04 (reveal
  round-trip, rotation) own it; R1-03 only consumes `GET /api/me/api-key`.
- Wizard curl-block rework (`:380-396`) — same dashboard-repo suite.

## Flake notes

- R1-03-01 runs first among api specs (anonymous, no state) — safe at any position, but keep it
  before anything that restarts the API.
- R1-03-02 clipboard assertions require the Playwright context-level `clipboard-read` permission;
  without it the read throws and the scenario would flake — grant it at context creation.
- The dashboard suite must target `http://localhost:8090` (this stack), never a deployed instance —
  the masked/unmasked comparison depends on `keys.json` minted by this seed.
