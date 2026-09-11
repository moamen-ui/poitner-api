# H-tests — harness self-checks

## Covers
`00-HARNESS.md` §2 (stack), §3 (personas, `keys.json`), §5 (mail), §8 (phase order), §11 (report). These
run at the start of every tier and gate the rest of the run: if `H-01`/`H-03` fail, the run aborts with
a clear "harness broken, not product broken" line in `report.md`.

## Preconditions
- `docker compose down -v && up -d` completed by `e2e/scripts/reset.sh`; `mailpit` service added to
  `docker-compose.yaml` (`axllent/mailpit`, host `8025`), API env `Email__Provider=smtp`,
  `Email__Smtp__Host=mailpit`, `Email__Smtp__Port=1025`, `Email__Enabled=true`.
- `e2e/scripts/seed.mjs` ran (writes `state/credentials.json`, `state/expected.json`, **new** `state/keys.json`).
- Node ≥ 18 on the host; `npx playwright install chromium` done.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| H-01 | stack-up | PR | api/mail | — | 1. `GET http://localhost:8090/swagger/v1/swagger.json` 2. `GET /api/meta` (R1-04; before R1-04 lands: `GET /api/branding`) 3. `GET http://localhost:8025/api/v1/messages` | 1 → 200 JSON with `paths` 2 → 200, `data.productName` non-empty 3 → 200 JSON `{ total: 0, messages: [] }` (mailbox cleared by `reset.sh`) | `report.md` Stack line: api version + mailpit message count |
| H-02 | determinism | nightly | api | — | Run `bash run-e2e.sh --ci --pr` twice from a wiped DB (reset → seed → PR-tier specs) | both runs green; `state/report.md` scenario tables identical except timings; `state/expected.json` byte-identical across runs | both reports attached as artifacts |
| H-03 | all personas log in + have API keys | PR | api | all 9 | For each of `superAdmin, wsAdmin, deputy, developer, pm, tester, client, tenantBOwner, flood` in `credentials.json`: 1. `POST /api/auth/login {email,password}` (client: **until R2-05 ships** the seeded password login; **after R2-05** `POST /api/auth/login-with-invite {token}` from `state/credentials.json.client.inviteToken` — `POST /api/auth/register-invite` with `{Code,Email,Password,DisplayName}` is the pre-R2-05 acceptance path and is what `seed.mjs` uses today) 2. `GET /api/auth/me` 3. `GET /api/me/api-key` (skip for `superAdmin` if the API returns 400/403 — record as SKIP, not FAIL) 4. `POST /api/auth/login-with-key {apiKey}` | 1 → `data.status == "ok"`, token present 2 → `roleName` matches the persona table in `00-HARNESS.md §3`, `isQuickAccess` true only for `client` 3 → `data.apiKey` matches `/^ptr_[0-9a-f]{40}$/` 4 → `status == "ok"` and `user.email` equals the persona | one row per persona |
| H-04 | password-reset email arrives with link + brand | PR | mail | developer | 1. `mail.clear()` 2. `POST /api/auth/forgot-password {email:"dev@example.com"}` 3. `mail.awaitMessage({ to: "dev@example.com", subjectIncludes: "eset" })` 4. `extractLink(html, "/reset")` | 2 → 200 always 3 → message within 10 s; `to` = dev@example.com; subject contains "reset" (case-insensitive); body contains the product name from `GET /api/branding` 4 → link contains `token=` with ≥ 20 chars; `POST /api/auth/reset-password {token, newPassword:"DevPass2!"}` → 200; `POST /api/auth/login` with the new password → ok; **restore** `DevPass1!` via reset again or `PATCH /api/admin/users/{id} {password}` as `wsAdmin` | Mail evidence row |
| H-05 | report.md written by zero-AI phases | PR | api | — | After `bash run-e2e.sh --ci --pr` (no `--with-ai`): 1. `test -s e2e/state/report.md` 2. grep the file | file exists and non-empty; contains `## Scenarios` and one row per executed scenario id; `## Phases` has `reset`, `seed`, `probe`, `api`, `cli`, `widget` rows | the file itself (CI artifact) |
| H-06 | cross-tenant seed present | PR | api | tenantBOwner | 1. login as `tenantBOwner` 2. `GET /api/admin/projects` | exactly `[e2e-gamma]`; none of `e2e-alpha/beta` visible | row |
| H-07 | flood user isolated | PR | api | flood | 1. login as `flood` 2. `GET /api/admin/projects` 3. `GET /api/projects/e2e-alpha/comments` | 2 → sees tenant A projects (same tenant, Tester role) 3 → 200 and **zero comments authored by flood** (the flood user must never have comments outside the 429 phase) | row |

## Spec files
- `e2e/api/harness.spec.mjs` — H-01, H-03, H-05, H-06, H-07 (node `node:test`, uses `lib/api.mjs`, `lib/report.mjs`).
- `e2e/mail/mail.spec.mjs` — H-04 (uses **new** `lib/mail.mjs`: `clear`, `awaitMessage`, `assertNoMail`, `extractLink`).
- `run-e2e.sh --determinism` — H-02 wrapper (nightly): runs the PR path twice and diffs `report.md` with timings stripped (`sed -E 's/\| [0-9]+ ms \|/| - |/'`).
- New helpers: `lib/mail.mjs`; `lib/report.mjs` (`record(...)`, `phase(...)`, `flush()`); `seed.mjs` additions for `TENANT_B_OWNER` (`POST /api/admin/tenants` as `superAdmin`, then project `e2e-gamma`), `FLOOD` (`POST /api/admin/users` as `wsAdmin`, role Tester), and `keys.json` (`GET /api/me/api-key` per persona; **Decision:** super admin key omitted if the endpoint refuses — record `null`).

## Not covered here
- Unit tests of the harness libs (none; they are exercised by every scenario).
- Dashboard availability (see `R1-03-tests.md` — SKIP when `DASHBOARD_DIR` unset).

## Flake notes
- H-01 step 3 must run **after** `reset.sh` cleared the mailbox — never assume an empty mailbox otherwise.
- H-04 is the only PR-tier mail scenario: keep it first in the `mail` phase so a broken SMTP wiring is reported once, clearly.
- H-03 performs ≤ 27 login calls; well under the per-IP `login` policy (60/min) — do not add a loop.
