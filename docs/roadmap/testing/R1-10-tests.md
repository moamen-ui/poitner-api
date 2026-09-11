# R1-10-tests — Fully-local client loop

## Covers
Acceptance criteria of `../execution/R1-10-local-client-loop.md`: AC-1 (generate against the local API
picks up an endpoint that does not exist in production) → **R1-10-01** · AC-2 (all three published to
Verdaccio) → **R1-10-01** · AC-3 (dashboard compiles, working tree stays clean) → **R1-10-02** ·
AC-4 (`npm ci` restores the published client) → **R1-10-03** · AC-5 (works with `NODE_AUTH_TOKEN`
unset) → **R1-10-04** · AC-6 (release path byte-identical when `CLIENTS_REGISTRY` is unset) +
AC-7 (`~/.npmrc` untouched) → **R1-10-05**.

## Preconditions
- **Prerequisite docs merged:** R1-07 (the `run-e2e.sh` phase runner, `lib/report.mjs`, and task 6's
  `verdaccio` service + `e2e/verdaccio/config.yaml`) and R1-10 itself (`CLIENTS_REGISTRY`,
  `scripts/publish-clients-local.mjs`, `npm run clients:local`). Without R1-07 there is no registry
  container and no `--registry` phase to hang these on.
- Stack up after a reset; `verdaccio` healthy on **4873** (`GET /-/ping` → 200); API on **8090**.
- npm isolation per harness §6.1 — `npm_config_userconfig` and `npm_config_cache` point into
  `e2e/state/npm/`, never `~/.npmrc`.
- **`DASHBOARD_DIR`** must be set for R1-10-02/03 (harness §10). Unset ⇒ those rows report **SKIP**
  with the reason, never a silent pass.
- **The "not in production" endpoint.** Decision: use `GET /api/meta` (R1-04) — it is new in Release 1,
  carries `[Tags("Meta")]`, and is genuinely absent from the deployed spec. If R1-04 has not merged,
  substitute any endpoint whose tag is in `orval.config.ts` `filters.tags` and that
  `npm view @moamen-ui/pointer-angular --registry https://npm.pkg.github.com` does not expose, and name
  the substitution in the report. A scenario that "proves" local generation using an endpoint
  production already has, proves nothing.
- `orval.config.ts` `filters.tags` must contain the chosen endpoint's tag — otherwise Orval silently
  emits nothing for it and R1-10-01 fails for a reason that is not the loop (this is the failure mode
  the dashboard repo's `CLAUDE.md` already warns about).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-10-01 | local generate → build → publish | nightly (`--registry` phase) | cli | — | 1. Preflight: `curl -fsS http://localhost:4873/-/ping` and `curl -fsS http://localhost:8090/swagger/v1/swagger.json \| jq -e '.paths["/api/meta"]'`. 2. `npm run clients:local` from the repo root, capturing stdout. 3. Parse the printed version `V` (`/0\.0\.0-local\.\d+/`). 4. `grep -R "getApiMeta\|ApiMeta" clients/angular/src/index.ts clients/react/src/index.ts clients/vue/src/index.ts`. 5. For each of `angular\|react\|vue`: `npm view @moamen-ui/pointer-<fw> versions --registry http://localhost:4873 --json`. 6. `jq -r '.version' clients/react/package.json` and `clients/angular/dist/package.json`. | 1 → both 200 and the path present (if `/api/meta` is absent the scenario **aborts as ENVIRONMENT**, not FAIL — the substitution rule in Preconditions applies); 2 → exit 0; 4 → the symbol is exported by all three barrels (AC-1); 5 → each version list contains `V` (AC-2); 6 → both report `V`; stdout contains three `--no-save` install lines and the `npm ci` line | report row with `V` and the three `npm view` outputs |
| R1-10-02 ⛓ | dashboard consumes it without dirtying the repo | nightly | dashboard | — | **SKIP unless `DASHBOARD_DIR` is set.** 1. `git -C $DASHBOARD_DIR status --porcelain` → record as the baseline (must already be empty; if not, **ENVIRONMENT**, stop). 2. Run the exact angular install line R1-10-01 printed. 3. `jq -r '.version' $DASHBOARD_DIR/angular/node_modules/@moamen-ui/pointer-angular/package.json`. 4. `git -C $DASHBOARD_DIR status --porcelain`. 5. `git -C $DASHBOARD_DIR diff --stat -- angular/package.json angular/package-lock.json`. 6. `(cd $DASHBOARD_DIR/angular && npm run build)`. 7. `grep -R "ApiMeta" $DASHBOARD_DIR/angular/node_modules/@moamen-ui/pointer-angular/` | 3 → the local `V` (AC-3); 4 → **byte-identical to the step-1 baseline**; 5 → empty (`--no-save` touched neither file); 6 → build exits 0 against the local client; 7 → the new symbol is present in the installed package | `git status` before/after + the build tail |
| R1-10-03 ⛓ | opt-out restores the published client | nightly | dashboard | — | **SKIP unless `DASHBOARD_DIR` is set and `NODE_AUTH_TOKEN` is present** (the published packages live on GitHub Packages — `npm ci` cannot reach them anonymously; record the SKIP reason). 1. `(cd $DASHBOARD_DIR/angular && npm ci)`. 2. `jq -r '.version' .../node_modules/@moamen-ui/pointer-angular/package.json`. 3. `grep -R "ApiMeta" .../node_modules/@moamen-ui/pointer-angular/ \|\| echo ABSENT`. 4. `git -C $DASHBOARD_DIR status --porcelain`. | 2 → a `1.x.y` published version, **not** `0.0.0-local.*` (AC-4); 3 → `ABSENT` — the not-yet-deployed endpoint is gone, which is the proof the restore was real; 4 → still clean | versions before/after |
| R1-10-04 ⛓ | the whole loop with no GitHub token | nightly | cli | — | 1. In a shell with **`NODE_AUTH_TOKEN` and `GITHUB_TOKEN` unset** (`env -u NODE_AUTH_TOKEN -u GITHUB_TOKEN`), repeat R1-10-01 steps 2–5. 2. With the same unset env, run the printed angular install line and `npm run build` in the app (skip if `DASHBOARD_DIR` unset). 3. Assert no request went to `npm.pkg.github.com`: grep the npm debug log in `e2e/state/npm/_logs/` for `npm.pkg.github.com`. | 1,2 → exit 0 throughout (AC-5); 3 → **no** match — the `@moamen-ui` scope resolved at Verdaccio, peers came via its npmjs uplink | the npm log path + the grep result |
| R1-10-05 | release path unchanged, developer config untouched | nightly | cli | — | 1. `sha256sum ~/.npmrc` (or record "absent") and `npm config get registry` → baseline. 2. `env -u CLIENTS_REGISTRY npm run generate-clients` (API local). 3. `jq -r '.publishConfig.registry' clients/react/package.json` and `cat clients/react/.npmrc`. 4. `env -u CLIENTS_REGISTRY npm run build-clients`; `jq -r '.publishConfig.registry' clients/angular/dist/package.json`. 5. Re-record step 1. | 3,4 → `https://npm.pkg.github.com` in every case (AC-6 — the release path is byte-identical with the var unset); 5 → sha256 and `npm config get registry` **identical** to the baseline (AC-7); `e2e/state/npm/.npmrc` is where any auth was written | both sha256 values + the four registry strings |

## Spec files
- `e2e/cli/local-clients.spec.mjs` — R1-10-01, R1-10-04, R1-10-05 (`node:test`; `lib/cli.mjs` for
  spawning, `lib/report.mjs` for rows).
- `e2e/dashboard/local-clients.spec.mjs` — R1-10-02, R1-10-03; guarded on `DASHBOARD_DIR`, reports
  `SKIP` with the reason when unset (harness §10).
- New helper: `lib/npmlocal.mjs` — `publishLocal()` wrapping `npm run clients:local` and returning
  `{ version, installCommands[] }` parsed from stdout; `viewVersions(pkg)`; `npmrcFingerprint()`
  returning `{ sha256, registry }` for the AC-7 before/after comparison.
- `run-e2e.sh --registry` phase gains these specs after R1-04-06 (both need Verdaccio; neither
  restarts the API, so the restart budget is untouched).

## Not covered here
- That the **published** workflow still works end to end — it publishes to a real registry from
  production and cannot be exercised locally by design. AC-6 covers the only part that this doc could
  break (the hard-coded registry becoming a variable); the workflow itself is proven by its next run.
- `npm publish` semantics (duplicate-version refusal, integrity) — Verdaccio's behaviour, not ours.
- UI correctness of whatever the dashboard builds against the local client — that is the phase's own
  dashboard scenarios; R1-10-02 asserts only that it **compiles** against it.
- Angular/react/vue build-script asymmetry (**vue has no `test` script**) — R1-10-02 deliberately runs
  `build`, the one script all three have.

## Flake notes
- R1-10-01 is the only scenario that publishes; R1-10-02/03/04 consume what it produced, so they run
  **in order, in the same phase**. A re-run of R1-10-01 mints a new timestamp version, which is
  harmless (Verdaccio accumulates versions; `down -v` wipes the storage volume).
- `npm view` can read a stale cache: every call passes `--registry` explicitly and the phase's
  `npm_config_cache` is the scratch dir, so a wiped stack starts cold.
- R1-10-04's log grep depends on npm actually writing `_logs/` — it does on any non-trivial install;
  if the directory is empty, treat as **ENVIRONMENT**, not a pass.
- No `signup`-bucket spend, no API restarts, no branding writes: this phase cannot poison another.

## State coupling
```
R1-10-02 <- R1-10-01        # installs the version R1-10-01 published
R1-10-03 <- R1-10-02        # restores what R1-10-02 replaced
R1-10-04 <- R1-10-01        # re-runs the loop against the same stack and registry state
```
