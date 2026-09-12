# 00 — E2E test harness (binding for every `R*-tests.md`)

> Designed 2026-09-11 in the three-expert testing meeting (transcripts: `../meetings/testing-review/`).
> Extends the existing `e2e/` suite (`e2e/README.md`, `run-e2e.sh`, `scripts/seed.mjs`,
> `widget/widget.spec.ts`). Every scenario document in this folder assumes this harness; implementers
> read this file first, then `../execution/01-OVERVIEW.md` for repo conventions.

> **These documents specify tests for features that are being built.** A scenario is implementable only
> after its execution doc ships; each document's *Preconditions* names that dependency. "Endpoint/DTO does
> not exist yet" is expected, never a defect of the scenario.

## 1. Principles

1. **Zero AI tokens at run time.** Everything CI or a developer re-runs is a committed Playwright spec
   (`.spec.ts`) or a node script (`.spec.mjs`). AI browser tools are for authoring and debugging only
   (§7). A scenario expected to run twice must be a spec.
2. **Local, wiped, deterministic.** `docker compose down -v` → `up -d` → seed → run. Same result twice
   in a row (`H-02`).
3. **Every role, real.** Seven roles are seeded and used where behaviour differs (§3). Plus a second
   tenant for isolation and a sacrificial user for rate-limit tests.
4. **Cheapest layer that proves the claim.** API script > CLI spawn > widget browser > dashboard
   browser. A widget spec exists only when the behaviour lives in the widget.
5. **Evidence is a file.** `e2e/state/report.md` lists every scenario id with PASS/FAIL/SKIP; traces
   on failure; mail evidence rows. Uploaded unconditionally in CI.

## 2. Stack (`docker-compose.yaml` — the dev compose, never the prod one)

| Service | Image / source | Host ports | Notes |
|---|---|---|---|
| `db` | postgres (existing) | 5433 | unchanged |
| `api` | existing build | 8090 | env adds `Email__Provider=smtp`, `Email__Smtp__Host=mailpit`, `Email__Smtp__Port=1025` (all three ARE read: `Infrastructure/DependencyInjection.cs:43-48`, `SmtpEmailSender.cs:29-30`). **Do not set `Email__Enabled` — nothing reads it**; e-mail is gated by the DB setting `email_enabled` (§3). Selected by `Infrastructure/DependencyInjection.cs:43-48`; sender `Infrastructure/Email/SmtpEmailSender.cs` |
| `mailpit` | `axllent/mailpit:latest` | 8025 (HTTP UI+API); SMTP 1025 **container-internal only** | **Added by R1-07 task** (the service is not in `docker-compose.yaml` yet — adding it is the first harness task). **Decision: Mailpit** — the code already assumes it (`SmtpEmailSender.cs:12`, `.env.example:9-12`); MailHog is archived; smtp4dev is heavier. JSON API + parsed HTML/Text bodies |
| fixture apps | host node, `e2e/fixture-app/serve.mjs` + `scripts/serve-dir.mjs` (R2-00) | 4173 smoke (**reserved** — the only page honouring `?project=`, `smoke/index.html` + `widget.spec.ts:19-20`) · **4181 `alpha`** (R2-04/R2-06) · 4174 fresh-app preview · 4175 `vite-react` (R3-01) · 4176 `csp-nonce` (R3-03) · 4177 `pinned-tamper` (R3-03) · 4178 `privacy` (R3-04) · 4179 recording proxy (R3-02) · 4180 pinned pages, route-fulfilled — no listener (R3-03) · **4182 `beta` origin fixture** (R1-05-06) · 8099 `landing` (R3-05) | `--strictPort`; started/killed by `run-e2e.sh` with the existing `trap` pattern |
| caddy (nightly only) | `caddy:2-alpine` with the repo `Caddyfile`, upstream `api` | 8443 | R3-03 header matrix; **also the §13 TLS variant** (a `pick-it.test` site block with `tls internal`) |
| `verdaccio` (nightly only) | `verdaccio/verdaccio:6` + committed `e2e/verdaccio/config.yaml` | 4873 | **Decision: Verdaccio.** A local npm registry so the CLI's *published* behaviour — version resolution, `@latest`, the `minCliVersion` upgrade hint, the post-rebrand deprecate-stub — is testable without publishing for real. Fits the harness constraints: no sudo, disposable (`down -v` wipes its storage volume), CI-friendly, and it proxies npmjs through its default uplink so a temp app's transitive deps still resolve. Started only by the phases that need it (§6 level 3) |

The `api` service additionally accepts **`Pointer__PublicUrl`** (read by `PointerUrlResolver.ResolvePublicUrl`,
`API/Extensions/PointerUrlResolver.cs:15-20`; unset in normal runs). §13 sets it through `restart-api.mjs`
to pin the origin that `/embed.js`, the served skills and branding asset URLs advertise.

Port registry lives in `e2e/scripts/lib/constants.mjs` as a **new `PORTS` export — it does not exist there today**; introducing it is a harness task, and this table is its single source (`PORTS = { smoke: 4173, fresh: 4174, viteReact: 4175, cspNonce: 4176, pinnedTamper: 4177, privacy: 4178, alpha: 4181, beta: 4182, recorder: 4179, pinned: 4180, caddy: 8443, registry: 4873, landing: 8099 }`). Nothing hard-codes a port elsewhere.
Optional isolated compose project for the 429 phase: `docker compose -p e2e-429 …` with shifted ports.

## 3. Personas (seed) — `e2e/scripts/seed.mjs` + `lib/constants.mjs`

| Role | Identity | Exists today | Notes |
|---|---|---|---|
| Super admin | `SUPER_ADMIN` `admin@pointer.local` | ✔ (`constants.mjs:10`) | cannot author comments/projects by design |
| Workspace admin (tenant owner) | `TENANT_OWNER` `e2e-owner@example.com` | ✔ (`:17`) | owns `e2e-alpha`, `e2e-beta` |
| Deputy (admin-tier, `GrantsAdmin`) | `USERS.deputy` | ✔ (`:29`) | stands in for "Admin" comments |
| Developer (automation account) | `USERS.developer` | ✔ (`:30`) | CLI/MCP scenarios that must **not** be admin |
| PM | `USERS.pm` | ✔ (`:31`) | |
| Tester | `USERS.tester` | ✔ (`:32`) | default author for widget scenarios |
| Client (QuickAccess) | `CLIENT` | ✔ (`:36`), invited to `e2e-alpha` only | **after R2-05**: passwordless — seed must switch from password login (`seed.mjs:104-106`) to redeeming the magic link (`POST /api/auth/login-with-invite`) |
| **Tenant B owner** | `TENANT_B_OWNER` `e2e-b-owner@example.com`, project `e2e-gamma` | **add** | every cross-tenant negative (notifications, events, api keys, builds, origins, MCP) |
| **Flood user** | `FLOOD` `flood@example.com`, Tester role | **add** | sole author for `comment-burst-429`; never used elsewhere |

**Enable e-mail (required for every mail scenario):** as `superAdmin`, `GET /api/admin/settings` →
drop `emailApiKeyConfigured` (response-only) → set `emailEnabled: true` → `PUT /api/admin/settings`
with that full body. The endpoint is a **replace-all** writer (`API/Controllers/Admin/SettingsController.cs:36-53`),
so a partial body blanks the demo/extension settings — always read-modify-write. `email_from_email` /
`email_from_name` may stay empty (`SmtpEmailSender.cs:32-35` falls back to `dev@pointer.local` /
`Pointer (local)`); `email_daily_cap` defaults to 250 (`EmailService.cs:17,26`). `EmailService.SendAsync`
returns false — silently, no exception — whenever `email_enabled` is false (`EmailService.cs:22`).

Seed also: mints an API key per persona → `e2e/state/keys.json` (`GET /api/me/api-key`); configures
`e2e-beta` as the origin-enforcement fixture (`enforceAllowedOrigins=true`, app-urls
`https://app.example.com`, `https://myapp-*.vercel.app`) once R1-05 lands; writes
`e2e/state/credentials.json` (existing).

## 4. Layout (additions under `e2e/`)

```
e2e/
  run-e2e.sh                 **built by R1-07 task 0a** (today it parses only --with-ai); flags: --ci --pr --nightly --fresh --whitelabel --apply --mcp --mail --429 --upgrade --all
  scripts/
    reset.sh                 + `cp -n .env.example .env` FIRST (compose has `env_file: .env`, gitignored), waits for mailpit :8025, clears mailbox (DELETE /api/v1/messages), bounded wait on /swagger with `docker compose logs api` on timeout
    seed.mjs                 + TENANT_B, FLOOD, keys.json, e2e-beta origin fixture, post-R2-05 client redemption
    probe-visibility.mjs     + cross-tenant block
    restart-api.mjs          `docker compose up -d --force-recreate api` with env overrides (e.g. Cli__MinVersion), re-waits /swagger
    serve-dir.mjs            static server for generated apps (R2-00)
    smoke-widget.sh          R3-03 §E
    reset-branding.mjs       restores /api/branding defaults after white-label runs (always in `finally`)
    lib/
      api.mjs                existing get/post/patch/del/login/ApiError (`lib/api.mjs:40-43`) + **`put`** (does not exist today; the replace-all `PUT /api/admin/settings` needs it), the header option for X-Pointer-Client, and `getRaw`/`postRaw`/`patchRaw`/`putRaw`/**`delRaw`** → `{ status, body }` without throwing, so 204-vs-200, a 403 on a DELETE, and `isForbidden`-inside-a-400 are assertable — `call()` drops the status today (`api.mjs:13-38`) and every verb throws on non-2xx
      constants.mjs          personas, projects, PORTS, enums
      mail.mjs               Mailpit client (§5)
      cli.mjs                spawnCli({cwd, args, env}) → {stdout, stderr, code, json?}; uses CLI_ENTRY (§6)
      git.mjs                tempRepo(), bareRemote(), refsSnapshot(), assertRefsUnchanged()
      report.mjs             **built by R1-07 task 0b** (today only the AI phase writes a report); record(id, tier, layer, role, result, ms, detail) → state/report.md (every phase)
      mcp.mjs                @modelcontextprotocol/sdk stdio client for R2-02 (zero LLM)
  fixture-app/               alpha, beta, smoke (existing) + vite-react (R3-01), static-template, csp-nonce, pinned-tamper
  widget/lib/auth.ts     preAuthWidget(page, token, user) — extracted from widget.spec.ts:41-51 (R2-00 task);
                             sets localStorage pointer_token/pointer_user AND sessionStorage pointer_visible.
                             Without pointer_visible the widget renders only #pf-launcher (element.ts:161-163,649-655),
                             so #pf-toggle/#pf-add/#pf-user/#pf-env do not exist — click #pf-launcher first in
                             any flow that does not pre-auth.
  widget/                    widget.spec.ts (existing) + notifications, quick-access, privacy-snapshot, whitelabel, release-eng specs
  fresh-app/                 run.mjs + fresh.spec.ts + templates/
  cli/                       init.spec.mjs, doctor.spec.mjs, update.spec.mjs
  apply/  mcp/  mail/  api/  one `*.spec.mjs` per area
  state/                     gitignored: credentials.json, keys.json, expected.json, report.md, junit.xml, fresh/, apply-work/
```

## 5. Mail assertions (`lib/mail.mjs`)

| Function | Call | Behaviour |
|---|---|---|
| `clear()` | `DELETE http://localhost:8025/api/v1/messages` | in `reset.sh` and before every mail scenario |
| `awaitMessage({ to, subjectIncludes, timeoutMs = 10000, intervalMs = 250 })` | `GET /api/v1/search?query=to:<to>` then `GET /api/v1/message/<ID>` | returns `{ id, subject, to, html, text, headers }`; polls, **never a fixed sleep**; throws with the last inbox listing on timeout |
| `assertNoMail({ to, withinMs = 3000 })` | same search | passes if zero messages after the window |
| `extractLink(html, pathPrefix)` | regex on `href` | e.g. `/reset?token=`, `pointer_invite=` |

Email scenarios that exist **today** (R1 tier): password reset (`AuthService.cs:66`), staff invite
(`InviteService.cs:171`), account approval / password-changed. **Notification e-mail is held** by the
roadmap; scenarios assert **no** mail on notify events until §32/email un-hold. Quick-access invite
mail (R2-05) is asserted with the setting on (link present, **no** `Password:`), and asserted absent by default.
`SmtpEmailSender` never throws (`:52-56`) — mail failures only surface via the poll timeout, so also
assert the API-side `emailSent` flag where a response carries it.

## 6. CLI under test (`lib/cli.mjs`)

Three fidelity levels. Each exists because the one below it cannot prove something; use the cheapest
that proves the claim (§1 rule 4). The package is `cli/` per `../execution/R1-02-cli-init.md` §A —
**it does not exist yet**, so every level is blocked on R1-02.

| Level | How | Used by | Proves what the level below cannot |
|---|---|---|---|
| **1 — direct** | `CLI_ENTRY=node <repo>/cli/dist/cli.js` after `npm run build` in `cli/` | **PR tier**, every CLI scenario | — (the baseline: zero global state, seconds per run) |
| **2 — tarball** | `cd cli && npm pack` → `npm i -g ./pointer-feedback-*.tgz` in the job sandbox → run `pointer init\|doctor` and `node -e "import('pointer-feedback/vite')"` | **nightly `packaging` job** | packaging itself: a missing `bin`, a broken shebang, files excluded by `files`/`.npmignore`, a bad `exports` map (which would break the `pointer-feedback/vite` subpath, R3-01) |
| **3 — local registry** | publish the *same* tarball to Verdaccio (§2), then `npx --registry http://localhost:4873 pointer-feedback@<v>` | **nightly `--registry` phase** (R1-04-06) | anything that only exists *between versions*: `@latest` resolution, the `minCliVersion` upgrade hint end-to-end with a genuinely old CLI, and the post-rebrand deprecate-stub forwarding to the new name |

**Never `npm link`** — it adds a global symlink that hides exactly the packaging bugs level 2 exists to
catch, and it leaks between runs.

### 6.1 Registry isolation (level 3)

A local registry must never become the default for anything else on the machine. Four rules, all
asserted in teardown:

1. **`npm_config_userconfig=<tmp>/.npmrc`** on every `npm`/`npx` invocation — npm then never reads or
   writes the developer's `~/.npmrc`. This is the guarantee; `--registry` alone is not, because
   `npm publish` writes auth tokens to the user config by default.
2. **`npm_config_cache=<tmp>/npm-cache`** — a `99.1.0` test build must not land in the real npm cache.
3. **`--registry http://localhost:4873` explicitly on every call**, plus a project-level `.npmrc` in the
   temp app dir. Belt and braces: the env var scopes config, the flag scopes the request.
4. **Teardown asserts the developer's config is untouched**: `npm config get registry` (user location)
   is captured before the phase and compared after, and `~/.npmrc` is hashed before/after. A mismatch
   fails the phase — it means rule 1 was not applied somewhere.

**Auth, non-interactively.** `npm publish` always sends a bearer token, even to a registry that allows
anonymous publish, so a token must exist. The committed `e2e/verdaccio/config.yaml` grants
`access`/`publish`/`unpublish` to `$all` for the `pointer-feedback*` package pattern, and the temp
`.npmrc` carries a dummy token Verdaccio ignores:

```
registry=http://localhost:4873/
//localhost:4873/:_authToken=e2e-local-only
```

No `npm adduser`, no interactive login, no real credential anywhere in the repo.

**Version bumping without dirtying the repo.** `cli/package.json` stays at its real version. To publish a
second version the spec **extracts the packed tarball into a scratch dir**, runs
`npm version <v> --no-git-tag-version` *there*, and re-packs — so the published artifact keeps the same
file list and layout as level 2's, and `git status` in `cli/` is clean afterwards. (Rejected: editing
`cli/package.json` in place — a killed run leaves the repo on a fake version; and building from a copied
source tree — that is a different artifact, defeating the point.)

### 6.2 Common to every level

Every CLI scenario runs in a fresh temp dir with `git init`, `git config user.email e2e@example.com`,
and (for apply) a bare remote from `lib/git.mjs`; never-push is asserted by comparing
`git for-each-ref` output of the bare remote before/after.

## 7. Token rule (AI tools)

| Use | Tool | Allowed when | Est. tokens |
|---|---|---|---|
| Any repeatable assertion | Playwright spec / node script | always — the default | 0 per run |
| Selector / timing recon while writing a spec | `playwright-cli` | once per spec; findings frozen into the spec | 15–60 k |
| Debugging a red run (same seed state) | `playwright-cli` or `chrome-devtools` MCP | red runs only | 20–80 k |
| Judgement audits with no programmatic oracle | `chrome-devtools` MCP (`lighthouse_audit`, dark-mode/390 px visual check — R3-05) | nightly/manual; the one place MCP is cheaper than writing a runner | 10–40 k |
| Visual regression | Playwright `toHaveScreenshot()` | never MCP | 0 |
| Real AI tool applying comments (R2-02-05) | real CLIs | **manual only**, budgeted (`run-e2e.sh --with-ai`) | real spend |

**Rule:** if it will run ≥ 2 times, it is a spec. Never drive the same scenario interactively twice.

## 8. Tiers and phase order

| Tier | Runs | Budget | Contents |
|---|---|---|---|
| **PR** | every PR touching `API/**`, `Application/**`, `Domain/**`, `Infrastructure/**`, `web-component/**`, `cli/**`, `e2e/**` | ≤ 15 min | api + widget + cli **+ mail** specs marked PR; existing phases 1–4. **Decision:** mailpit runs on every PR run (a ~20 MB container started by `reset.sh`), so PR-tier mail rows such as `R2-05-08` (a 3 s absence check) are legal; slower mail rows stay nightly. **Budget headroom:** R3-04 adds three browser scenarios + a fourth fixture server and R3-05 two more, roughly tripling today's two-spec widget phase — still inside 15 min, but the next doc to add PR-tier browser work should re-measure before doing so. |
| **Nightly** | `schedule` 03:00 UTC + `workflow_dispatch` | ≤ 45 min | PR set + fresh-app inits, white-label **incl. the mock-domain rebrand rehearsal (§13)**, mail, packaging, restart-dependent scenarios, header matrix, the **`--registry` phase** (§6 level 3, folded into the `Cli__MinVersion` restart window — zero extra recreates), **429 phase last**, `upgrade` job |
| **Manual** | on demand | — | anything needing a real AI tool, a real deploy, or human judgement — plus the **§13 TLS variant** (`--mock-domain-tls`) and the pre-rename rebrand gate (§13.5) |

**Per-scenario path filters** ("PR (paths `cli/**`)") are implemented with `dorny/paths-filter` in
the R1-07 workflow: it sets `run_cli`, which gates a `--cli` phase in `run-e2e.sh`. No other
path-scoped tiering exists; everything else is gated at job level.

**Every phase in the tier's list emits a report row**, including one that did not run — recorded
`SKIP` with the reason (a path filter, a missing `DASHBOARD_DIR`, an unmerged prerequisite doc).

Phase order inside a run: reset → seed → probe → api specs → cli specs → widget specs → mail → fresh-app →
white-label (with `reset-branding` in `finally`) → **429 phase last** (flood user; optional isolated
compose project) → report. Restart-dependent scenarios (`restart-api.mjs`) are nightly and grouped so
the API restarts at most 3 times per run.

## 9. Flakiness rules

- No fixed sleeps: `expect.poll`/`toPass` with explicit timeouts; mail 10 s; widget notification badge
  ≤ 70 s (widget poll made configurable via `window.__POINTER_CONFIG__.notifyPollMs`, suite sets 1000).
- Rate-limit buckets: all 429 scenarios run in the last phase with dedicated users. The poisoned
  bucket for R2-05-06 is **`login-with-key`** (R1-05 applies its new `login` policy there only), so
  every CLI/MCP/apply phase must run **before** the `--429` phase. There is **no** rate limit on
  `POST /api/auth/login` and there must not be — `Tests/AuthRateLimitingTests.cs:22-29`
  (`Login_IsNotRateLimited`) exists to keep it that way.
- **`signup` budget: 5 requests / hour / IP** (`API/Extensions/RateLimitingExtensions.cs:30-38`)
  shared by **six** endpoints: `register`, `forgot-password`, `reset-password`, `register-admin`,
  `register-invite` (`AuthController.cs:41,63,74,94,113`) **and the anonymous invite preview
  `GET /api/invites/{code}`** (`API/Controllers/InvitesController.cs:23`) — an easy one to miss, since
  every preview *and* every accept spends a token. H-04 spends 2; R1-08 spends 7 (PR) / 10 (nightly) and
  therefore runs in its own `-p e2e-429` compose project (`docs/roadmap/testing/R1-08-tests.md`). Never
  restore a password with a second reset — use `PATCH /api/admin/users/{id}`. Any new scenario touching
  those six endpoints declares its spend here; a local re-run inside the hour needs a container recreate.
- Widget boot: always `waitForResponse('**/capture-config')` before interacting (`widget.spec.ts:61-66`).
- Ports: registry + `--strictPort`; fixture servers killed by trap.
- Determinism: pinned minor versions for generators (fresh-app), 1.1 s comment spacing in seed.
- **Flake confirmation — per-scenario retry, one per scenario per run.** A scenario that fails is
  re-run **alone** via `run-e2e.sh --only <id>` (R1-07 task 0a) with no intervening code change. Same
  code, different result ⇒ it is a **flake**, never a pass. Rules:
  - **Cap: one retry per scenario per run**, enforced by the runner, not the caller — a human
    re-running by hand cannot loop past it (R1-07 task 0a).
  - **State-coupled scenarios are never retried alone.** A scenario listed in its doc's
    `## State coupling` section (§12) depends on a sibling having run first, so running it in
    isolation proves nothing: it is recorded **`FLAKE-SUSPECTED`** with no retry and settled
    statistically by the next nightly. The report names which ones were refused and why.
  - **Budget:** a per-scenario retry costs seconds, not a suite — this is the whole reason for
    choosing it. The single **whole-run** retry stays reserved for the 300 s fresh-app budget
    (R2-00) and is not available for anything else.
  - A retry that fails again is a plain `FAIL`, not a flake.
- Restart safety: `restart-api.mjs` preserves the volume and re-waits `/swagger/v1/swagger.json`.
- Two-image `upgrade` job (R1-06): old API image → seed → mint key → stop `api` only → new image on the
  **same volume** → assert legacy key logs in. Never `down -v` between the two halves.

## 10. Dashboard (separate repo)

Three layers, no UI mocking: (1) **contract guard** in this repo — fetch `/swagger/v1/swagger.json`
and assert the endpoint/DTO names the dashboard regenerates from exist with inner-type annotations;
(2) dashboard behaviours decomposed to their endpoints and tested at the API layer here; (3) the
`pointer-dashboard` repo's own Playwright suite runs against **this** compose stack, consuming
`e2e/state/credentials.json` + `keys.json`; its scenario ids are prefixed `DASH-` and cross-referenced.
When `DASHBOARD_DIR` is unset, dashboard-layer scenarios are reported **SKIP**, never silently green.

## 11. Report (`e2e/state/report.md`)

```
# E2E report — <UTC ts> · git <sha> · flags <…>
## Stack   api /api/meta {version, apiVersion, minCliVersion, skillVersion} · mailpit messages=<n>
## Phases  | phase | result | duration | notes |
## Scenarios | id | tier | layer | role | result | attempts | ms | detail |
## Mail evidence | to | subject | scenario id |
## Failures  trace paths · `docker compose logs api --tail 100`
```
**`result`** is one of `PASS` · `FAIL` · `SKIP` · **`FLAKE`** (failed, then passed on the
per-scenario retry with no code change) · **`FLAKE-SUSPECTED`** (failed once, state-coupled, not
retried — §9). A `FLAKE` is **never** reported as a `PASS`; a tier containing one is not green.

**`attempts`** is `1` or `2` and lives in its own column, deliberately **not** in `detail`:
`detail` must not contain timings, counters or boundary indices — those go to the CI log — and the
determinism diff (H-02) strips the `ms`, `attempts` **and** `detail` columns before comparing, so a
flaky night still diffs clean against a clean one.

Also `state/junit.xml` for CI annotations.

## 12. Scenario document template (each `R*-tests.md`)

```
# <id>-tests — <execution doc title>
## Covers                acceptance criteria of ../execution/<id>.md this file proves (checkbox refs)
## Preconditions         seed state, personas, fixtures, env overrides, prerequisite docs
## Scenarios             table: id · tier · layer · role · steps (numbered, exact calls/selectors) · expected · evidence
## Spec files            paths under e2e/ and the helper functions they need (new helpers listed)
## Not covered here      what is unit-level (Tests/, cli/test/) or manual, and why
## Flake notes           scenario-specific timing/ordering constraints
## State coupling        machine-readable; omit the section entirely when nothing is coupled
```
Scenario ids: `<doc>-<nn>` (e.g. `R1-05-03`); the human-readable names used in the execution docs are
kept as the `intent` column so both cross-reference.

### 12.1 Declaring state coupling — the `⛓` marker

Some scenarios cannot be run on their own: they need a sibling to have run first in the same run.
Running one in isolation does not test it — it fails for the missing setup, which reads as a product
bug. So each one says so, in two places:

**1. In the row, so you can see it at a glance.** A coupled scenario carries **`⛓`** immediately after
its id in the `id` column:

| id | intent | … |
|---|---|---|
| R2-04-01 | `notify: applied shows badge to author` | … |
| R2-04-02 ⛓ | `notify: thumbs-down reopens with note` | … |

Read `⛓` as: **this one cannot be retried alone.** No marker means the scenario is independently
runnable — which is what `--only` and the flake retry (§9) rely on.

**2. In a `## State coupling` section at the end of the doc, so a machine can parse it and a human can
see *what* it is tied to.** One line per coupled scenario, `<id> <- <ids it needs>`, with the reason as
a trailing comment:

```
## State coupling
R2-04-02 <- R2-04-01        # reopens the comment R2-04-01 applied
R2-04-03 <- R2-04-01, R2-04-02
```

**Decision:** marker *and* section, rather than either alone — the marker is what makes a table
scannable, the section is what the runner (`run-e2e.sh --only`, R1-07 task 0a) and the
`e2e-tester-agent` actually read, and the trailing comment is why a reviewer can tell whether the
coupling is real or an artefact worth removing. A doc with no coupled scenarios omits the section
entirely; absence is a positive claim that every scenario there stands alone.

The right-hand side is sibling ids, the literal token **`reset`**, or both. **`reset`** means *this
scenario needs a freshly reset stack* — it is not re-runnable inside a run that already executed it,
because it consumed a one-shot resource: a rate-limit bucket (`signup`, `login-with-key`, the 429
phases), one of the three permitted `api` restarts, a global row it created, or a "first ever" event it
asserted. Those are exactly as unretryable as a missing sibling, and for the same practical reason.

Coupling is transitive in effect but written **directly**: list only what a scenario immediately needs,
not the whole chain — the runner walks it. Cross-doc coupling is allowed and written the same way
(`R1-09-07 <- R1-05-06`).

## 13. Mock domains (rebrand rehearsal)

The product is a SaaS with a rebrand planned (`docs/rebranding-plan` → `docs/rebranding/REBRANDING-PLAN.md`),
and the white-label rule (`../execution/01-OVERVIEW.md`, rule 4) is today only proven at the **string**
level: R2-00-05/06/08 swap `productName` and grep for leaks. Nothing proves the product works under a
different **domain**. §13 adds that dimension: rehearse "we are now **PickIt** at **pick-it.test**"
against the local stack, then tear it down. It is the dress rehearsal for §52 and the gate the
rebranding plan's `verify-no-pointer.sh` is run beside.

### 13.1 The mock domain

**Decision: `pick-it.test`, over plain HTTP.** `.test` is reserved by RFC 6761 §6.2 and never resolves
publicly, so a stray request can't leak to a real host. **`.dev` is deliberately not used locally** — it
is on Chrome's HSTS preload list, so `http://pick-it.dev` is force-upgraded to HTTPS before a request is
made and cannot be served plainly. The *real* brand domain may still be `pick-it.dev`; the rehearsal only
needs a stand-in with the same shape (hyphenated multi-label name, non-localhost host), because every
assertion is about the **emitted string** and the **host routing**, not the TLD.

**Decision: `https` is an opt-in variant, not the default** (`--mock-domain-tls`). HTTP keeps the
rehearsal to one container and no cert trust; the TLS variant exists because `X-Forwarded-Proto` is the
only way to prove `/embed.js` and the served skills emit `https://pick-it.test/...` rather than
`http://`. Wiring: the R3-03 Caddy container (§2, `caddy:2-alpine`, port 8443) gains a `pick-it.test`
site block with `tls internal` (Caddy's local CA) reverse-proxying `api:8080`; Playwright opts out of
cert validation with `ignoreHTTPSErrors: true` in `use`. Node-side specs point at the Caddy port and
send `Host: pick-it.test`. This variant is **nightly + manual only**.

### 13.2 Resolution without `sudo`

Never `/etc/hosts` — it needs root and is hostile to CI runners.

| Layer | Mechanism |
|---|---|
| **Browser (Playwright)** | Chromium's `--host-resolver-rules`. `e2e/playwright.config.ts:10-13` has a single `use` block and no `launchOptions` today; add `launchOptions: { args: [`--host-resolver-rules=MAP ${MOCK_DOMAIN} 127.0.0.1, MAP *.${MOCK_DOMAIN} 127.0.0.1`] }` gated on `process.env.E2E_MOCK_DOMAIN`, so a normal run is byte-identical to today. `MAP` sends DNS to loopback; the **port** still comes from the URL, so the spec navigates to `http://pick-it.test:8090` (or `:8443` in the TLS variant). |
| **Node (API/CLI specs)** | No browser, no resolver flag. Requests go to `http://127.0.0.1:8090` with an explicit **`Host: pick-it.test`** header (`lib/api.mjs`'s existing header option); the assertion is then on the **emitted** links in the response body, which is what actually matters. |
| **Emitted-origin pinning** | For surfaces that must advertise the domain regardless of how they were reached, set **`Pointer__PublicUrl=http://pick-it.test:8090`** on the `api` service. `PointerUrlResolver.ResolvePublicUrl` (`API/Extensions/PointerUrlResolver.cs:15-20`) prefers `Pointer:PublicUrl` over `{scheme}://{host}`, and it is the single source for `/embed.js` (`Program.cs:288`), the `<POINTER_SERVER>` placeholder rewrite (`Program.cs:213-214`) and branding asset URLs (`BrandingController.cs:38`). Applied via `restart-api.mjs` (§4), which already exists for env overrides. |

**Decision:** a mock-domain scenario asserts **either** through the browser with the resolver rule
**or** through node with the `Host` header — never both for the same claim, because the resolver rule
proves routing and the header proves emission, and conflating them hides which one broke.

### 13.3 What a rebrand can and cannot change

Driven by `PUT /api/admin/branding` (super-admin, `Admin/BrandingController.cs`; patch semantics —
only non-null fields are written, `BrandingService.cs:33-53`).

**Runtime-brandable — the rehearsal asserts these change:**

| Surface | Mechanism |
|---|---|
| `GET /api/branding` | `BrandingService.BuildResponseAsync:67-109` — `productName`, `tagline`, `primaryColor`, `urls.{app,demo,docs,landing}`, `assets.*`, `extension.*` |
| Widget UI text | `loadBranding()` → `getBrandName()` (`web-component/src/constants.ts:131-143`), used at `templates.ts:17,69,133` and `element.ts:634,857` |
| Landing page | `[data-brand-name]` / `[data-brand-logo]` rewritten from `/api/branding` (`landing/index.html:795-797,838`) |
| CLI human output | `productName` from `/api/branding`; branding unreachable is a **hard exit 1**, never a literal fallback (R1-02 §C) |
| E-mail subjects/bodies | product name resolved per send (`AuthService.cs:66` reset subject; `InviteService` invite body) |
| Invitation join links | `app_base_url` → **`brand_url_app`** → compiled default (`InviteService.GetAppBaseUrlAsync`, shipped `42e534e`) — so setting only `urls.app` rebrands the links |
| Branding asset URLs | `{publicBase}/api/branding/asset/{kind}?v=` (`BrandingService.cs:114-119`) — picks up `Pointer__PublicUrl` |

**Compiled-in or frozen — the rehearsal must NOT assert these change:**

| Surface | Why |
|---|---|
| `<pointer-feedback>` tag, `window.__pointerEmbedded`, `data-component-source` | frozen DOM/global contract (`../execution/R1-01-contract-freeze.md`) — renaming breaks every installed host page |
| `/pointer.js`, `/pointer.css`, `/embed.js`, `/skill.md`, `/pointer-init.md` | frozen served URLs — permanent aliases post-rebrand |
| `.pointer/` dir, `POINTER_*` env vars, `pointer_token`/`pointer_user` storage keys | frozen on-disk contract |
| npm package `pointer-feedback`, bin `pointer` | frozen; post-rebrand a deprecate-stub forwards |
| CLI `DEFAULT_SERVER` (`01-OVERVIEW.md:41`), `InviteService.DefaultAppBaseUrl`, `BrandingService.cs:10-17` defaults | build-time constants — only ever the **fallback**, replaced by config/DB at runtime |
| Landing static prose, `docker-compose.prod.yml`, `Caddyfile` host blocks | genuinely renamed by the rebranding plan itself, not by a runtime toggle |

The leak regex therefore stays `/(?<![-\w])Pointer(?![-\w])/g` (R2-00): the lookarounds already exempt
`pointer-feedback`, `.pointer/` and `pointer_token` by construction. **A rehearsal scenario that fails
on a frozen name is the scenario being wrong, not the product.**

### 13.4 Teardown

Branding is **global DB state** and the domain mapping is **process state**; both must be restored:

1. Branding → `e2e/scripts/reset-branding.mjs` in the phase's `finally`, followed by
   `assert-branding-default.mjs` (the existing R2-00-07 pattern, harness §8).
2. `Pointer__PublicUrl` → `restart-api.mjs` **without** the override, re-waiting `/swagger/v1/swagger.json`.
3. Resolver rule / `Host` header → per-process, dies with the run; nothing to clean.
4. TLS variant → `docker compose … stop caddy`.

**Killed mid-run:** the next `reset.sh` (`docker compose down -v`) wipes the DB, so branding returns to
`BrandingService.cs:10-17` defaults and the override dies with the container — the rehearsal cannot
poison a later run **as long as it never runs against a stack someone intends to keep**. Stated as a
rule: the mock-domain phase always runs after a reset, never against a long-lived local stack.

### 13.5 Relationship to the rebranding plan

This rehearsal is how the rebranding plan's answers get *exercised* before the rename: `NAME_LOWER` /
`DOMAIN` (`REBRANDING-PLAN.md` §1.1-1.2) are fed in as `E2E_MOCK_BRAND` / `E2E_MOCK_DOMAIN`, and
`docs/rebranding/verify-no-pointer.sh` is run in the same job — the grep proves no brand string remains
in the **source**, the rehearsal proves the **running product** works under the new identity. Neither
alone is sufficient: the grep cannot catch a runtime string served from the DB, and the rehearsal cannot
catch a hard-coded name on a path no scenario visits. **Run both green before executing the rename.**
