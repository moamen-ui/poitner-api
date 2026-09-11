# H-tests — harness self-checks

## Covers
`00-HARNESS.md` §2 (stack), §3 (personas, `keys.json`), §5 (mail), §8 (phase order), §11 (report). These
run at the start of every tier and gate the rest of the run: if `H-01`/`H-03` fail, the run aborts with
a clear "harness broken, not product broken" line in `report.md`.

## Preconditions
- `e2e/scripts/reset.sh` starts with `cp -n .env.example .env` — `docker-compose.yaml:12` declares
  `env_file: .env` and `.env` is gitignored (`.gitignore:3`), so `docker compose up -d` fails on a
  fresh checkout without it.
- `docker compose down -v && up -d` completed by `e2e/scripts/reset.sh`; `mailpit` service added to
  `docker-compose.yaml` (`axllent/mailpit`, host `8025`), API env `Email__Provider=smtp`,
  `Email__Smtp__Host=mailpit`, `Email__Smtp__Port=1025`.
  **`Email__Enabled` is NOT a thing** — nothing reads it (only `docker-compose.prod.yml:43` sets it);
  `EmailService.SendAsync` gates on the **DB** setting `email_enabled`
  (`Application/Services/Implementation/EmailService.cs:22`, key at `ISettingsService.cs:11`), which
  defaults to `false`.
- **E-mail enabled in the DB by `seed.mjs`** (without this every mail scenario silently receives
  nothing): as `superAdmin`, `GET /api/admin/settings` → drop the response-only
  `emailApiKeyConfigured` → set `emailEnabled: true` → `PUT /api/admin/settings` with that **full**
  body. The PUT is a replace-all writer (`API/Controllers/Admin/SettingsController.cs:36-53`), so a
  partial body would blank demo/extension settings — always read-modify-write. `email_from_email` /
  `email_from_name` may stay empty (`SmtpEmailSender.cs:32-35` falls back to `dev@pointer.local` /
  `Pointer (local)`) and `email_daily_cap` defaults to 250 (`EmailService.cs:17,26`).
- `e2e/scripts/seed.mjs` ran (writes `state/credentials.json`, `state/expected.json`, **new** `state/keys.json`).
- Node ≥ 18 on the host; `npx playwright install chromium` done.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| H-01 | stack-up | PR | api/mail | — | 1. `GET http://localhost:8090/swagger/v1/swagger.json` 2. `GET /api/meta` (R1-04; before R1-04 lands: `GET /api/branding`) 3. `GET http://localhost:8025/api/v1/messages` | 1 → 200 JSON with `paths` 2 → 200, `data.productName` non-empty 3 → 200 JSON `{ total: 0, messages: [] }` (mailbox cleared by `reset.sh`) | `report.md` Stack line: api version + mailpit message count |
| H-02 ⛓ | determinism | nightly | api | — | Run `bash run-e2e.sh --ci --pr` twice from a wiped DB (reset → seed → PR-tier specs) | both runs green; `state/report.md` scenario tables identical **after stripping the `ms` and `detail` columns** (both carry per-run values by design — see Flake notes); `state/expected.json` byte-identical across runs | both reports attached as artifacts |
| H-03 | all personas log in + have API keys | PR | api | all 9 | For each of `superAdmin, wsAdmin, deputy, developer, pm, tester, client, tenantBOwner, flood` in `credentials.json`: 1. `POST /api/auth/login {email,password}` (client: **until R2-05 ships** the seeded password login — `seed.mjs:94-106` invites the client, finds the auto-provisioned user via `GET /api/admin/users` and `PATCH`es a known password; there is **no** `register-invite` call in the suite today. **After R2-05** `POST /api/auth/login-with-invite {token}` from `state/credentials.json.client.inviteToken`) 2. `GET /api/auth/me` 3. `GET /api/me/api-key` 4. `POST /api/auth/login-with-key {apiKey}` | 1 → `data.status == "ok"`, token present 2 → `roleName` equals the literal expected map (**not** the §3 persona labels): `superAdmin`→`Admin`, `wsAdmin`→`Workspace Admin`, `deputy`→`Workspace Admin Deputy`, `developer`→`Developer`, `pm`→`PM`, `tester`→`Tester`, `flood`→`Tester`, `tenantBOwner`→`Workspace Admin`, `client`→`Client` (`e2e/scripts/lib/constants.mjs:28-36`); `isQuickAccess` true only for `client` 3 → `data.apiKey` matches `/^ptr_[0-9a-f]{40}$/` **for every persona incl. `superAdmin`** — `ProfileService.GetOrCreateApiKeyAsync` (`:40-50`) has no super-admin guard, so there is no SKIP branch 4 → `status == "ok"` and `user.email` equals the persona | one row per persona |
| H-04 ⛓ | password-reset email arrives with link + brand | PR | mail | developer | 1. `mail.clear()` 2. `POST /api/auth/forgot-password {email:"dev@example.com"}` 3. `mail.awaitMessage({ to: "dev@example.com", subjectIncludes: "eset" })` 4. `extractLink(html, "/reset")` | 2 → 200 always 3 → message within 10 s; `to` = dev@example.com; **subject** equals `Reset your <productName> password` where `<productName>` comes from `GET /api/branding` (`AuthService.cs:66` — the **body has no product name**, only the `{appUrl}/reset?token=…` link, so never assert the brand on the body) 4 → link contains `token=` with ≥ 20 chars; `POST /api/auth/reset-password {token, newPassword:"DevPass2!"}` → 200; `POST /api/auth/login` with the new password → ok; **restore `DevPass1!` with `PATCH /api/admin/users/{id} {password}` as `wsAdmin`** — never a second `forgot-password`/`reset-password` pair (each is a `signup`-policy request; see Flake notes) | Mail evidence row |
| H-05 | report.md written by zero-AI phases | PR | api | — | After `bash run-e2e.sh --ci --pr` (no `--with-ai`): 1. `test -s e2e/state/report.md` 2. grep the file | file exists and non-empty; contains `## Scenarios` and one row per executed scenario id; `## Phases` has a row for **every phase in the tier's list** — `reset`, `seed`, `probe`, `api`, `cli`, `widget` — where a phase that did not run (e.g. `cli` on an `API/**`-only PR under path filtering) is present with result `SKIP` and a reason, never absent | the file itself (CI artifact) |
| H-06 | cross-tenant seed present | PR | api | tenantBOwner | 1. login as `tenantBOwner` 2. `GET /api/admin/projects` | exactly `[e2e-gamma]`; none of `e2e-alpha/beta` visible | row |
| H-07 ⛓ | flood user isolated | PR | api | flood | 1. login as `flood` 2. `GET /api/admin/projects` 3. `GET /api/projects/e2e-alpha/comments` | 2 → sees tenant A projects (same tenant, Tester role) 3 → 200 and **zero comments authored by flood** (the flood user must never have comments outside the 429 phase) | row |

## Spec files
- `e2e/api/harness.spec.mjs` — H-01, H-03, H-05, H-06, H-07 (node `node:test`, uses `lib/api.mjs`, `lib/report.mjs`).
- `e2e/mail/mail.spec.mjs` — H-04 (uses **new** `lib/mail.mjs`: `clear`, `awaitMessage`, `assertNoMail`, `extractLink`).
- `run-e2e.sh --determinism` — H-02 wrapper (nightly): runs the PR path twice and diffs `report.md`
  with the **`ms` and `detail` columns removed** (both are per-run by design — `detail` carries
  attempt seconds, 429 boundary indices and `Retry-After`). **Decision:** `lib/report.mjs` exposes
  `renderStable()` which emits the scenario table without those two columns; H-02 diffs that output,
  never a `sed` over the rendered markdown.
- New helpers: `lib/mail.mjs`; `lib/report.mjs` (`record(...)`, `phase(...)`, `renderStable()`, `flush()`); `seed.mjs` additions for `TENANT_B_OWNER` (`POST /api/admin/tenants` as `superAdmin`, then project `e2e-gamma`), `FLOOD` (`POST /api/admin/users` as `wsAdmin`, role Tester), `keys.json` (`GET /api/me/api-key` per persona — every persona mints one, `ProfileService.cs:40-50` has no super-admin guard), and the **e-mail enable step** (read-modify-write `PUT /api/admin/settings`, see Preconditions).

## Not covered here
- Unit tests of the harness libs (none; they are exercised by every scenario).
- Dashboard availability (see `R1-03-tests.md` — SKIP when `DASHBOARD_DIR` unset).

## Flake notes
- H-01 step 3 must run **after** `reset.sh` cleared the mailbox — never assume an empty mailbox otherwise.
- H-04 is the only PR-tier mail scenario: keep it first in the `mail` phase so a broken SMTP wiring is reported once, clearly.
- **`signup` budget: 5 requests / hour / IP** (`API/Extensions/RateLimitingExtensions.cs:30-38`),
  shared by `register`, `forgot-password`, `reset-password`, `register-admin`, `register-invite`
  (`API/Controllers/AuthController.cs:41,63,74,94,113`). H-04 spends **2** (forgot + reset); restoring
  the password via `PATCH /api/admin/users/{id}` spends **0**. Any new scenario touching those five
  endpoints must declare its spend. A local re-run inside the same hour needs a container recreate,
  or `forgot-password` answers 429 where this doc says "200 always".
- H-03 performs ≤ 27 login calls. `POST /api/auth/login` is **deliberately unlimited** — it carries no
  `[EnableRateLimiting]` (`API/Controllers/AuthController.cs:15-19`) and
  `Tests/AuthRateLimitingTests.cs:22-29` (`Login_IsNotRateLimited`) exists to keep it that way. R1-05's
  new `login` policy applies to `login-with-key` only, so H-03 step 4 (9 calls) is the metered one.
- H-02 compares `renderStable()` output, not the rendered `report.md` — see Spec files.

## State coupling
```
H-02 <- reset               # re-runs the whole tier twice; a solo retry means re-running everything
H-04 <- reset               # spends 2 of the 5 `signup` tokens/hour — a retry cannot get fresh ones without a container recreate
H-07 <- reset               # asserts the flood user has zero comments, which stops being true once R1-05-05 has run
```
