# Opus review — harness + R1 test scenario documents (2026-09-11)

Reviewer: Opus 5 subagent, read-only. Scope: `docs/roadmap/testing/00-HARNESS.md`, `H-tests.md`,
`R1-01-tests.md` … `R1-07-tests.md`, cross-checked against `docs/roadmap/execution/R1-*.md` and the
real code.

## 1. COVERAGE GAPS

1. **Mail is structurally impossible as specified — no scenario notices.** `EmailService.SendAsync`
   returns false unless the **DB** setting `email_enabled` is true
   (`Application/Services/Implementation/EmailService.cs:22`); the key is never seeded
   (`API/Seed/AdminSeeder.cs` seeds no `AppSetting` for it; default is `false` via
   `SettingsService.GetBoolAsync` fallback, `Application/Services/Implementation/SettingsService.cs:31-35`).
   `Email__Enabled=true` (00-HARNESS.md:31, H-tests.md:11) is **not read by any code** — only
   `docker-compose.prod.yml:43` sets it and nothing consumes it. So H-04 (and every later mail
   scenario) can never see a message. Fix: add to 00-HARNESS §3 seed list — as `superAdmin`,
   `PUT /api/admin/settings` with a **full** `UpdateSettingsRequest` (it is a replace-all writer,
   `API/Controllers/Admin/SettingsController.cs:36-53`) setting `emailEnabled:true`.
2. **R1-02 AC-1's *interactive* claim is never proven.** R1-02-01 runs `--yes --json` only; the
   prompt flow (R1-02 §C texts, 3-attempt retry, "✔ Signed in as …") is pushed entirely to
   `cli/test/init-yes.test.ts` against a stub (R1-02-tests.md:50-51). The scenario asserts something
   weaker than AC-1 ("interactive, no AI tool, under 5 minutes").
3. **R1-04 AC-2 vs. R1-04-02 step 5**: R1-04 §doctor says `POST /api/events {type:"doctor_run",
   meta:{…}}` — **no `projectKey`** (R1-04-doctor-and-meta.md:64), so `ProjectId` is null;
   R1-04-tests.md:29 step 5 asserts `counts.doctor_run ≥ 1` from
   `GET /api/admin/events/summary?projectId=<alpha id>`, which cannot contain it. Either add
   `projectKey` to the doctor event in the exec doc, or drop step 5.
4. **R1-05-01 step 6 asserts an optional behaviour.** R1-05 Design D makes `comment_rejected` a
   `UsageEvent` *"if present; otherwise a structured ILogger warning"*
   (R1-05-allowed-origins-ratelimit.md:110). `counts.comment_rejected ≥ 2` (R1-05-tests.md:47) fails
   whenever the implementer picks the logger branch. Also `comment_rejected` is not in R1-02's client
   whitelist nor its server-emitted set (R1-02-cli-init.md:188-205) — the exec docs don't agree on
   who may write it.
5. **R1-03 proves nothing of R1-03 in this repo** (declared, harness §10) — and its one local
   scenario (R1-03-01) is red on arrival, see 2.1–2.3 below. Net: AC-1…AC-6 are covered only by a
   suite that reports SKIP by default.
6. **H-tests covers no harness rule that will actually break**: nothing asserts the `PORTS` registry
   (§2/§9), `--strictPort`, or the trap-kill of fixture servers — the two concrete port bugs in 4.3
   below would be caught by neither H-01 nor H-05.
7. **R1-05 AC-2's "staff *key* via curl without Origin"** is proven with a password-login JWT
   (R1-05-tests.md:50 step 2), not an API key. Equivalent under the current design (the gate reads
   `is_quick_access`), but weaker than the AC text.

## 2. FACTUAL ERRORS

**Wrong about today's code (and the post-change state doesn't fix it):**

1. R1-03-tests.md:28 step 2 requires a `$ref`-shaped 200 schema for
   `get /api/projects/{key}/comments` and `post /api/projects/{key}/comments` — both actions carry
   **no `[ProducesResponseType]` at all** (`API/Controllers/CommentsController.cs:13-24`), so
   Swashbuckle emits a 200 with no `content` → the step throws on `responses['200'].content` before
   asserting. Nothing in R1-02..R1-07 annotates them.
2. R1-03-tests.md:28 step 3 ("no `isSuccess`/`data` components") fails on `get /api/branding`: it is
   annotated with the **wrapper** — `[ProducesResponseType(typeof(Result<BrandingResponse>), 200)]`
   (`API/Controllers/BrandingController.cs:35`).
3. R1-03-tests.md:28 step 2 also fails for `get /api/admin/projects`: the inner type is
   `List<ProjectResponse>` (`API/Controllers/Admin/ProjectsController.cs:21`) → the 200 schema is
   `type: array` with `items.$ref`, not `schema.$ref`.
4. H-tests.md:21 (H-03): "`POST /api/auth/register-invite` … is what `seed.mjs` uses today" —
   **false**. `seed.mjs:94-106` creates the invite, finds the auto-provisioned user via
   `GET /api/admin/users`, then `PATCH /api/admin/users/{id} {password}`; `InviteService` provisions
   the quick-access user with a generated password (`InviteService.cs:546-552`). No `register-invite`
   call exists in the suite.
5. H-tests.md:40: "the per-IP `login` policy (60/min)" — **there is no `login` policy today**;
   `POST /api/auth/login` carries no `[EnableRateLimiting]` (`API/Controllers/AuthController.cs:15-19`)
   and 60/min is the `plans` policy (`API/Extensions/RateLimitingExtensions.cs:51-59`). Per R1-05 the
   `login` policy is applied to `login-with-key` **only** and deliberately never to `Login`.
6. H-tests.md:22 (H-04): "body contains the product name from `GET /api/branding`" — the reset e-mail
   body has no product name; it is only in the **subject** (`AuthService.cs:66`:
   `$"Reset your {resetProductName} password"`). The body carries a link to
   `https://app.pointer.moamen.work/reset?token=…` (`BrandingService.cs:13`), lowercase — an exact
   `"Pointer"` match fails. Assert on the subject.
7. H-tests.md:21 step 2: "`roleName` matches the persona table in §3" — the §3 labels are not the role
   names. Actual: super admin = `Admin`, tenant owner = `Workspace Admin`, deputy =
   `Workspace Admin Deputy` (`e2e/scripts/lib/constants.mjs:29`). Needs an explicit expected map.
8. H-tests.md:21 step 3 hedge ("skip for `superAdmin` if the API returns 400/403") —
   `ProfileService.GetOrCreateApiKeyAsync` has **no** super-admin guard (`ProfileService.cs:40-50`), so
   the key always mints and the SKIP branch is dead; R1-02-tests.md:36 sub-case 6's "else record
   sub-SKIP" is likewise dead.
9. R1-02-tests.md:36 sub-case 6 (via R1-02-cli-init.md:91): "403/`isForbidden` (super-admin)" —
   `ProjectsController.Create` has **no `IsForbidden` branch**
   (`API/Controllers/Admin/ProjectsController.cs:30-38`), so a super-admin create surfaces as **400**
   with `isForbidden` in the envelope. The CLI must key off the envelope; as written an implementer
   maps a status that never arrives.
10. R1-06-tests.md:33 step 5: `SELECT count(*) … WHERE prefix ~ 'ptr_' …` returns **the full row
    count, never 0** — `Prefix` is by design the first 12 chars, i.e. `ptr_########` (R1-06 §A,
    `ProfileService.cs:73`). Drop the `prefix ~ 'ptr_'` term; keep `hash`/`encrypted`.
11. R1-04-tests.md:32 (R1-04-05): steps say issue the 121 requests **concurrently** via `Promise.all`,
    expected column says "i ≤ 120 → 200; i = 121 → 429". Mutually exclusive — under concurrency only
    "exactly 120 × 200 and 1 × 429" is assertable.
12. R1-07-tests.md:40 step 3: `gh run view --json jobs,jobsRuntime` — `jobsRuntime` is not a
    `gh run view` JSON field. Use `--json jobs` and compute from each job's `startedAt`/`completedAt`.
13. R1-07-tests.md:40 steps 4/7: `gh run download -n report` / `-n playwright-report` assume artifact
    **names** that R1-07's Design never fixes (R1-07-e2e-schedule.md:19 names only paths). One-line
    fix: pin `name: report`, `name: playwright-report`, `name: test-results` in the upload steps and
    reference those.
14. R1-07-tests.md:28-31 repeats the workflow path filter **without `cli/**`** — but
    R1-02-tests.md:33/34 tier the two fresh-app scenarios as "PR (paths `cli/**`)". With the stated
    filter they never run.
15. R1-02-tests.md:35 step 3: "`git -C <app> status --porcelain` — every line starts with `.pointer/`"
    — porcelain lines are prefixed with a 2-char status + space (`?? .pointer/…`). Assert on
    `line.slice(3)`.
16. R1-05-tests.md:41 cites `beta/index.html:18-23`; the element is at
    `e2e/fixture-app/beta/index.html:17-21` (content claim — `environment="production"` — is correct).

**Correctly describes the post-change state (no action needed, listed so they aren't re-litigated):**
R1-06-tests.md:20-25's snake_case correction of the exec doc's PascalCase SQL is right
(`Infrastructure/Migrations/20260831183037_AddUserApiKey.cs:14` → `api_key`). R1-05-tests.md:52's
"`#pf-env` renders (only `environment`, not `fixed-environment`, is set)" is right against
`web-component/src/element.ts:112,660` — it is **`e2e/README.md:55-57` that is stale**; fix the README
in the same PR or an implementer will follow the wrong note.

## 3. UNIMPLEMENTABLE STEPS

1. **R1-02-tests.md:33 step 6 has the widget flow backwards.** Login is deferred: nothing renders a
   login modal at boot or on submit — `showLoginModal` fires from `activateAddComment()` when there is
   no token (`element.ts:264-268, 344-354`), and its `afterLogin` callback re-runs `init()` **and
   auto-enters picking**. Correct order: `#pf-add` → fill `#pf-email`/`#pf-password` →
   `#pf-login-submit` → (picking is already active — **do not** click `#pf-add` again, it toggles
   picking off) → click `<h1>` → `#pf-comment-text` → `#pf-submit`. The "Decision" hedge describes
   neither real branch.
2. **R1-02-tests.md:36 sub-case 4 asserts `git diff --stat` on untracked files.** `tempRepo()` only
   does `git init` + `git config` (harness §6, R1-02-tests.md:27); after sub-case 1 every written file
   is untracked, so `git diff` is empty and "lists only `.pointer/config.json`" cannot hold. Add
   `git add -A && git commit -m base` between sub-cases 1 and 4 (as R1-02-03 explicitly does).
3. **Exact success statuses are not observable through `lib/api.mjs`.** `call()` returns
   `json?.data ?? json` and never the status (`e2e/scripts/lib/api.mjs:13-38`); failures do carry
   `ApiError.status`. R1-02-tests.md:37 asserts "→ **204**" for `POST /api/events`, R1-04/R1-05 assert
   200-vs-403 pairs. Add `getRaw`/status passthrough to harness §4's `lib/api.mjs` bullet
   (R1-01-tests.md:38 asks for `getRaw` for GETs only).
4. **`docker compose up -d` cannot run on a fresh checkout**: `docker-compose.yaml:12` has
   `env_file: .env` and `.env` is gitignored (`.gitignore:3`). Neither `reset.sh`, 00-HARNESS §2,
   H-01's preconditions nor R1-07's workflow creates it. One line: `cp -n .env.example .env` at the
   top of `e2e/scripts/reset.sh`. (The `.env.example` defaults match `constants.mjs:10-13`, so nothing
   else changes.)
5. **R1-06-tests.md:34 step 4 runs the *candidate's* `seed.mjs` against the *legacy* image.**
   `LEGACY_REF` is "the last commit before `AddApiKeysTable`", which may predate R1-05; but seed must
   by then perform the R1-05 beta fixture (`PATCH … {enforceAllowedOrigins:true}`, harness §3) and
   mint `keys.json` — both unknown to a legacy build. Pin `LEGACY_REF` to a commit **after** every
   seed-visible API change, or run a reduced seed for this job.
6. **R1-05-tests.md:52 (R1-05-06) cannot share port 4173** with the widget phase: `run-e2e.sh:23-28`
   already holds 4173 with `serve.mjs smoke` for the whole Playwright phase, and
   `playwright.config.ts:11` defaults `baseURL` there. Assign the beta fixture its own port in the §2
   registry (e.g. 4178) and pass it explicitly.
7. **H-05 asserts `## Phases` contains `cli` and `widget` rows** (H-tests.md:23) while R1-02's CLI
   scenarios are path-gated to `cli/**` — on an `API/**`-only PR the `cli` phase produces no rows and
   H-05 fails. Either always emit a phase row (result SKIP) or relax the assertion.

## 4. FLAKE RISKS

1. **`signup` = 5 requests / hour / IP** (`API/Extensions/RateLimitingExtensions.cs:30-38`) covers
   `forgot-password`, `reset-password`, `register`, `register-admin`, `register-invite`
   (`AuthController.cs:41,63,74,94,113`). H-04 alone burns 2, and its "restore `DevPass1!` via reset
   again" burns 2 more. Any second mail scenario, any local re-run inside the hour without a container
   recreate, and any later `register-invite` scenario gets a 429 where the doc says "200 always".
   H-tests.md:40 worries about the wrong policy. Add an explicit note + budget to 00-HARNESS §9, and
   restore the password via `PATCH /api/admin/users/{id}` (0 signup tokens), never via a second reset.
2. **The toast asserted by R1-05-06 lives 2200 ms** (`element.ts:1523-1529`,
   `setTimeout(() => t.remove(), 2200)`). An `expect.poll` with a default/large interval can miss it
   entirely. State "poll interval ≤ 250 ms" in the flake note.
3. **Port 4173 double-booked** (see 3.6). Also 4174 is claimed by both R1-02-01 (`vite preview`) and
   R1-02-02 (`serve-dir`) — fine only because they are sequential; state that.
4. **H-02's "report tables identical except timings" will never hold.** R1-02-01/02 write "per-attempt
   seconds" into `detail`, R1-05-05 writes the boundary index and `Retry-After`, R1-04-05 writes the
   boundary index (R1-02-tests.md:33-34, R1-05-tests.md:51, R1-04-tests.md:32). The prescribed
   `sed -E 's/\| [0-9]+ ms \|/| - |/'` (H-tests.md:30) also doesn't match §11's bare-number `ms`
   column. Fix: strip the whole `ms` and `detail` columns before diffing, or forbid volatile values in
   `detail`.
5. **`reset.sh`'s readiness loop has no timeout** (`e2e/scripts/reset.sh:34-36`):
   `until curl -sf …/swagger.json; do sleep 2; done`. The dev image is `dotnet watch run` over a bind
   mount (`Dockerfile:1-4`, `docker-compose.yaml:11-15`), so a compile error hangs the loop until the
   25-min job timeout with no diagnostic. Add a bounded wait + `docker compose logs api` on expiry.
6. **R1-06-02's compose override sets `image:` while the base service defines `build:`**
   (`docker-compose.yaml:11`). `docker compose up -d api` will build-and-tag into
   `pointer-api:legacy`/`:candidate` whenever the tag is absent, silently replacing the two-image
   fixture with a `dev`-target build. Add `--no-build`/pull-never semantics or a `build: !reset`
   override.
7. **`docker compose stop api` / `up -d api` in R1-06-02 need `API_TAG` exported for every
   invocation**, including the `stop` — an unset var resolves the image to `pointer-api:` and errors.

## 5. TIERING / BUDGET

1. **The PR tier does not plausibly fit 15 minutes on a cold CI runner.** Unaccounted: pulling
   `mcr.microsoft.com/dotnet/sdk:8.0` + `postgres:15` + `axllent/mailpit`, then a full
   `dotnet restore`/build inside `dotnet watch` before `/swagger` answers (`Dockerfile:1-4`) — several
   minutes on its own, repeated on every `down -v`. Add to that `cd cli && npm run build`, `npm ci` +
   `playwright install`, `npm create vite` + `npm install` + `vite build` (R1-02-01), a second app
   build (R1-02-02), two `init` runs in R1-04-02/03, and `retries: 1` in CI doubling any red spec.
   Either cache the API image (build `--target final` once and reuse), or move R1-02-01/02 to nightly
   from the start rather than "first to move if the budget is exceeded" (R1-02-tests.md:33).
2. **Three slowest PR scenarios**: (a) **R1-02-01** `init-vite-no-ai` (~2–4 min, budgeted 300 s + one
   full retry); (b) **R1-02-02** `init-static-no-ai` (CLI + static server + a second Playwright run,
   ~60–90 s); (c) **R1-04-02** `doctor-green-after-init` (a full `init` incl. three skill downloads
   plus two `doctor` runs). The uncounted leader is the `reset` phase itself.
3. **Tier mismatches**: R1-06-03 is PR-tier yet shells out to `docker compose exec -T db psql`
   (R1-06-tests.md:35 step 5) and **mutates `state/keys.json`** mid-run — a PR-tier scenario with a
   cross-spec side effect; either make it nightly or move the psql assertion into the nightly
   R1-06-01. R1-01-01, R1-02-06, R1-03-01 are correctly cheap PR scenarios. R1-04-05/R1-05-05
   correctly nightly/`--429`.
4. **Per-scenario path filters are a new mechanism with no owner**: "PR (paths `cli/**`)"
   (R1-02-tests.md:33-34) has no representation in `run-e2e.sh`'s flag list (00-HARNESS.md:62) or in
   R1-07's workflow. Specify it (e.g. `--cli` gated on `dorny/paths-filter`) or drop it.

## 6. VERDICT

- **00-HARNESS.md — READY-WITH-EDITS.** (a) §3 seed list: add "enable e-mail:
  `PUT /api/admin/settings` as superAdmin with `emailEnabled:true` (full request body) —
  `Email__Enabled` env is not read" and delete `Email__Enabled=true` from §2. (b) §2/§4: add
  `cp -n .env.example .env` to `reset.sh` (compose requires the gitignored `.env`). (c) §2 port
  registry: give the beta-origin fixture its own port (4178); note 4173 is held for the whole widget
  phase. (d) §4 `lib/api.mjs` bullet: add "returns `{status, data}`/`getRaw` so 204-vs-200 and
  403-vs-400 are assertable". (e) §9: add the `signup` 5/hour/IP bucket to the rate-limit rules.
  (f) §8: define how per-scenario path filters are implemented. (g) §11: forbid volatile values in
  `detail` (or exclude `detail` from the H-02 diff).
- **H-tests.md — READY-WITH-EDITS.** H-04: assert the product name in the **subject**, not the body;
  restore the password with `PATCH /api/admin/users/{id}`; note the 5/hour `signup` budget. H-03:
  replace "roleName matches the persona table" with the literal map (`Admin`, `Workspace Admin`,
  `Workspace Admin Deputy`, `Developer`, `PM`, `Tester`, `Client`); delete the
  `register-invite`-in-seed claim (seed uses invite + `PATCH` password, `seed.mjs:94-106`); delete the
  super-admin-key SKIP hedge; replace "per-IP login policy (60/min)" with "`POST /api/auth/login` is
  deliberately unlimited". H-02: strip `ms` **and** `detail` before diffing. H-05: allow a SKIP row
  for a phase that did not run.
- **R1-01-tests.md — READY.** Verified end-to-end: `API/Program.cs:197-223` (injected files, exact
  content types, `<POINTER_SERVER>` rewrite), `API/wwwroot/install.sh:76-80` (gitignore block,
  `!.pointer/config.json` is R1-01 task 4), `skill.md:19` (`.pointer/credentials.env`). Only nit:
  `beta`-style line refs are not used here, nothing to fix.
- **R1-02-tests.md — READY-WITH-EDITS.** Rewrite R1-02-01 step 6 to the real deferred-login order
  (2.1/3.1); add `git add -A && git commit` before the idempotency sub-case (3.2); fix
  `git status --porcelain` prefix handling (2.15); change sub-case 6's expectation from "403" to
  "exit 3 on an `isForbidden` envelope (the controller returns 400)"; either add `projectKey` to
  R1-02-05's event assertions or note the status-code helper need (3.3); flag that AC-1's interactive
  path is unproven (1.2).
- **R1-03-tests.md — NOT-READY.** R1-03-01, the only scenario this repo runs, is red against both
  today's tree and the post-R1-03 tree for three independent reasons (2.1–2.3): two contract endpoints
  carry no `ProducesResponseType`, `/api/branding` is annotated with the `Result<T>` wrapper, and
  `GET /api/admin/projects` returns an array (no `schema.$ref`). Fix by either (i) adding an R1-03
  task to annotate `CommentsController.Create/List` and de-wrap `BrandingController`, and
  (ii) relaxing step 2 to accept `schema.$ref || schema.items.$ref`. Also list R1-04 as a prerequisite
  (the endpoint list includes `/api/meta`).
- **R1-04-tests.md — READY-WITH-EDITS.** Drop or fix R1-04-02 step 5 (`doctor_run` carries no
  `projectKey` per the exec doc — 1.3). Rewrite R1-04-05's expectation to "exactly 120 × 200 and
  1 × 429 with `retry-after`" to match its own concurrent-issue instruction (2.11). Everything else
  checked out: `[ResponseCache]` header assertion, restart-via-override (volume preserved), check-id
  list, `git ls-files` semantics.
- **R1-05-tests.md — READY-WITH-EDITS.** Give R1-05-06 its own fixture port (3.6) and a ≤250 ms poll
  interval for the 2.2 s toast (4.2); demote R1-05-01 step 6 (`comment_rejected` counts) to "assert
  only if the implementer chose the `UsageEvent` branch — Design D allows a log line" or make Design D
  mandatory (1.4); fix the `beta/index.html:18-23` line ref; optionally note that AC-2's "staff key via
  curl" is exercised with a JWT. Preconditions otherwise verified against
  `AppEnvironmentsController.cs:10-23`, `ProjectsController.cs:71-88`,
  `ProjectService.SetAppUrlAsync:280-332` (`IsActive` defaults true), `AdminSeeder.cs:27-30` (global
  `default` env).
- **R1-06-tests.md — READY-WITH-EDITS.** Remove `prefix ~ 'ptr_'` from the plaintext scan — it matches
  every row by design (2.10). Pin `LEGACY_REF` to a commit whose API the *candidate's* `seed.mjs` can
  still drive, or ship a reduced legacy seed (3.5). Add `--no-build`/image-only semantics and an
  exported `API_TAG` to every compose call in the upgrade job (4.6/4.7). Consider moving R1-06-03 out
  of the PR tier given the `psql` shell-out and the `keys.json` rewrite (5.3). The snake_case
  correction of the exec doc is right and should be mirrored back into
  `R1-06-api-key-hardening.md`'s acceptance SQL.
- **R1-07-tests.md — READY-WITH-EDITS.** Replace `--json jobs,jobsRuntime` with `--json jobs` +
  computed duration (2.12); pin artifact names in R1-07's upload steps and reference them (2.13); add
  `cli/**` to the PR path filter or delete R1-02's path-scoped PR tiering (2.14); add a workflow step
  creating `.env` (3.4); temper AC-4 by budgeting the cold `dotnet watch` first build (5.1).
