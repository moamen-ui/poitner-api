# R1-04-tests — `pointer doctor` + `GET /api/meta`

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-04-doctor-and-meta.md`](../execution/R1-04-doctor-and-meta.md).

## Covers

- AC-1 (`/api/meta` anonymous, fields, cache header) → **R1-04-01**; the `meta` policy
  (120/min/IP → 429) → **R1-04-05**.
- AC-2 (fresh install → `doctor` all ✔ exit 0) → **R1-04-02**; AC-3 (`git add
  .pointer/credentials.env` → ✘ gitignore, exit 1) → **R1-04-03**; AC-4 (`Cli__MinVersion=99.0.0` →
  doctor exit 5 with upgrade hint; `init` refuses) → **R1-04-04**; AC-5 (`doctor --json` valid JSON,
  nothing else on stdout) → asserted in every CLI scenario below.

## Preconditions

- Seed complete; CLI built (`CLI_ENTRY` per harness §6); developer key from `keys.json`;
  `e2e-alpha` active for Local.
- Fixture source `cli/test/fixtures/vite` copied into a fresh `tempRepo()` per CLI scenario.
- `e2e/scripts/restart-api.mjs` exists (harness §4) for R1-04-04 — nightly, grouped restarts (see
  Flake notes).
- `/api/meta` live (R1-04 merged) — `minCliVersion` default `0.1.0` from `appsettings.json`
  (`Cli:MinVersion`).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-04-01 | `/api/meta` anonymous, all fields, ResponseCache | PR | api | — | 1. `GET /api/meta` (no `Authorization`); capture headers. 2. `GET /api/branding` and compare `productName`. 3. **Decision:** do *not* assert two rapid calls return identical `serverTime` — `[ResponseCache]` only sets response headers (no output caching), bodies legitimately differ. | 1 → 200; `data.version` non-empty and matches `/^(\d+\.\d+\.\d+([+\-][0-9A-Za-z.\-]+)?\|0\.0\.0-dev)$/` (InformationalVersion; dev default `0.0.0-dev`); `data.apiVersion` integer ≥ 1; `data.minCliVersion` matches `/^\d+\.\d+\.\d+$/` (dev default `0.1.0`); `data.skillVersion === null` (until R2-03); `abs(Date.now() − Date.parse(data.serverTime)) < 5 min`. Header `cache-control` contains `max-age=60` and `public`. 2 → `data.productName === branding.data.productName` (`"Pointer"`). | report row; versions echoed into the report `## Stack` line |
| R1-04-02 | `doctor-green-after-init` | PR | cli | developer | 1. `tempRepo()` ← copy `cli/test/fixtures/vite`. 2. `spawnCli({ cwd, args: ['init','--server','http://localhost:8090','--key',K,'--project','e2e-alpha','--environment','local','--tool','other','--yes','--json'] })`. 3. `spawnCli({ cwd, args: ['doctor','--json'] })`. 4. `spawnCli({ cwd, args: ['doctor'] })` (human mode). 5. **Decision — dropped.** The exec doc's `doctor` posts `POST /api/events {type:"doctor_run", meta:{ok, failed}}` with **no `projectKey`** (`../execution/R1-04-doctor-and-meta.md` §doctor), so `ProjectId` is null and the event can never appear in `GET /api/admin/events/summary?projectId=…`. Either drop this assertion (done here) or add `projectKey` to the doctor event in the exec doc first; the `doctor_run` emission itself is unit-tested in `cli/test/doctor.test.ts`. 6. Stale-key negative: overwrite `.pointer/credentials.env` to `POINTER_API_KEY=ptr_<40 zeros>` → `doctor --json` → exit **3** (precedence rule 2: key ✘ outranks other ✘), `key` check status `error`. | 2 → exit 0. 3 → exit 0; `JSON.parse(stdout.trim())` succeeds (AC-5 — nothing but JSON on stdout); `ok === true`; `checks` ids are exactly `config, server, meta, clock, key, project, widget, widget-served, skills, gitignore, stack`, every `status === 'ok'` (`stack` ok because init wrote `.pointer/stack.json`). 4 → exit 0; every check line starts `✔`. 6 → as listed. | report row; both stdout captures in `detail` |
| R1-04-03 | `doctor-detects-tracked-credentials` | PR | cli | developer | 1. Reuse the repo from R1-04-02 (or repeat its steps 1–2). 2. `git add .pointer/credentials.env && git commit -m leak`. 3. `spawnCli doctor --json`. 4. `git rm --cached .pointer/credentials.env` (untrack, file stays) → `doctor --json`. 5. **Decision:** no `--fix` rescue is asserted — `--fix` can add ignore lines but cannot untrack an already-committed file; the check must keep failing until git state changes. | 3 → exit **1**; the gitignore line matches `/credentials\.env is tracked/` with `✘`; `ok === false`; a check with id `gitignore` has `status === 'error'`. 4 → exit 0 again (proves the check reads `git ls-files`, not file existence). | report row |
| R1-04-04 | min-version gate (`Cli__MinVersion=99.0.0`) | nightly | cli + api | developer | 1. Fresh fixture+init repo (R1-04-02 steps 1–2); baseline `doctor --json` → exit 0. 2. `node e2e/scripts/restart-api.mjs --env Cli__MinVersion=99.0.0`: writes `e2e/state/api-env.override.yml` (`services: { api: { environment: ["Cli__MinVersion=99.0.0"] } }`), then runs **`docker compose -f docker-compose.yaml -f e2e/state/api-env.override.yml up -d --force-recreate api`**. *Preserved:* the `pgdata` named volume and the `db` + `mailpit` containers (only `api` is recreated — no `down`, no `-v`), so every seeded row, minted key and issued JWT stays valid. *Re-wait:* polls `GET http://localhost:8090/swagger/v1/swagger.json` until 200 (2 s interval, 120 s timeout), then verifies `GET /api/meta` → `data.minCliVersion === '99.0.0'`. 3. `spawnCli doctor --json`. 4. `spawnCli init --server http://localhost:8090 --key K --project e2e-alpha --yes`. 5. Restore (grouped with R1-06-04 — see Flake notes): `restart-api.mjs` with no `--env` (override file removed) → `docker compose -f docker-compose.yaml up -d --force-recreate api` → same re-wait → `GET /api/meta` `minCliVersion === '0.1.0'`; `doctor --json` → exit 0. | 2 → override effective. 3 → exit **5**; message contains `older than the server requires` and `npx -y pointer-feedback@latest`; checks that ran are exactly `config, server, meta` (precedence rule 1 — stop immediately). 4 → exit **5**, same upgrade message (init refuses). 5 → exit 0. | report row; `docker compose logs api --tail 50` captured on failure |
| R1-04-05 | meta 121st/min/IP → 429 | nightly | api | — | Runs in the **final `--429` phase**, and the 121 requests are issued **concurrently** (`Promise.all` over 121 fetches, not a sequential loop) so the fixed 60 s window cannot reset mid-run — a sequential loop is the known flake here together with R1-05-05 (an IP bucket burn must be the last thing to touch `/api/meta`). 1. Loop i = 1…121: anonymous `GET /api/meta`. 2. `expect.poll` (≤ 70 s) until `GET /api/meta` → 200 again (fixed 60 s window released). 3. **Decision:** main stack, not the `-p e2e-429` isolated project — nothing after this phase reads `/api/meta`; the isolated project stays reserved for IP-partitioned policies later specs still need (e.g. R2-05-06's login burst). | Under concurrent issue the arrival order is not defined, so assert the **multiset**: exactly **120 responses with status 200 and exactly 1 with status 429**, and the 429 carries header `retry-after` matching `/^\d+$/` with value ≥ 1. (Never "the 121st is the 429" — that only holds for a sequential loop, which is the flake this concurrency rule exists to avoid.) Step 2 → 200 within the window. | report row; the 200/429 counts go to the CI log, not `detail` (H-02 determinism) |

## Spec files

- `e2e/api/meta.spec.mjs` — R1-04-01.
- `e2e/cli/doctor.spec.mjs` — R1-04-02/03/04 (CLI half; `spawnCli`, `tempRepo`).
- `e2e/api/rate-limits.spec.mjs` — R1-04-05 (+ R1-05-05); runs only under `run-e2e.sh --429`.
- New script: `e2e/scripts/restart-api.mjs` (harness §4) — env-override compose file + force-recreate
  + `/swagger` re-wait; shared with R1-06-04 and R2-03-03.

## Not covered here

- `MetaService` internals (config read, branding product name, informational version) —
  `Tests/MetaEndpointTests.cs`.
- Semver compare helper, each check's ✘/⚠ unit paths (404-meta ⚠, clock-skew ⚠, `--fix` repairs) —
  `cli/test/doctor.test.ts` + `checks.test.ts` against the stub server (forcing real clock skew or a
  404-meta server is stub work, not worth stack restarts).
- Reflection assertions that `login-with-key` carries `[EnableRateLimiting("login")]` and `Login`
  does not — `Tests/CommentRateLimitingTests.cs` / `Tests/AuthRateLimitingTests.cs` (R1-05 owns
  them); a *live* login-burst burn would 429 the whole suite's IP for a minute — deliberately not
  an E2E scenario (the analogous isolated-phase test is R2-05-06).
- `skill.md` Step-1 doctor hook, `cli/README.md` — docs, review-level.
- Dashboard "Settings → About" version display — dashboard repo follow-up (R1-04 Dashboard tasks).

## Flake notes

- **Nightly restart group (harness §8, ≤ 3 `api` recreates per run):** ① `Cli__MinVersion=99.0.0`
  (R1-04-04 steps 2–4) → ② `Auth__ApiKeyEncryptionKey=<rotated>` with **no** Cli override
  (R1-06-04 — this recreate also restores `minCliVersion` to default) → ③ clean recreate (R1-04-04
  step 5 + R1-06-04 restore-verify). R1-04-04's step 5 therefore executes *after* R1-06-04's
  rotation steps in the nightly order; both docs state this.
- R1-04-04's re-wait must observe `/api/meta` reflecting the override before any CLI call — dotnet
  watch restarts can serve stale 200s on `/swagger` first; the meta read is the gate.
- R1-04-05 must be the last phase; H-01's `/api/meta` probe and R1-04-01 run long before it. Do not
  parallelise specs within the `--429` phase.
- R1-04-02's `tempRepo` git identity (`e2e@example.com`) is required for the commit in R1-04-03.
