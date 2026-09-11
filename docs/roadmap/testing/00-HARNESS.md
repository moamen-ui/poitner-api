# 00 — E2E test harness (binding for every `R*-tests.md`)

> Designed 2026-09-11 in the three-expert testing meeting (transcripts: `../meetings/testing-review/`).
> Extends the existing `e2e/` suite (`e2e/README.md`, `run-e2e.sh`, `scripts/seed.mjs`,
> `widget/widget.spec.ts`). Every scenario document in this folder assumes this harness; implementers
> read this file first, then `../execution/01-OVERVIEW.md` for repo conventions.

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
| `api` | existing build | 8090 | env adds `Email__Provider=smtp`, `Email__Smtp__Host=mailpit`, `Email__Smtp__Port=1025`, `Email__Enabled=true`. Selected by `Infrastructure/DependencyInjection.cs:43-48`; sender `Infrastructure/Email/SmtpEmailSender.cs` |
| `mailpit` | `axllent/mailpit:latest` | 8025 (HTTP UI+API); SMTP 1025 **container-internal only** | **Decision: Mailpit** — the code already assumes it (`SmtpEmailSender.cs:12`, `.env.example:9-12`); MailHog is archived; smtp4dev is heavier. JSON API + parsed HTML/Text bodies |
| fixture apps | host node, `e2e/fixture-app/serve.mjs` + `scripts/serve-dir.mjs` (R2-00) | 4173 smoke · 4174 fresh-app preview · 4175 `vite-react` (R3-01) · 4176 `csp-nonce` · 4177 `pinned-tamper` | `--strictPort`; started/killed by `run-e2e.sh` with the existing `trap` pattern |
| caddy (nightly only) | `caddy:2-alpine` with the repo `Caddyfile`, upstream `api` | 8443 | R3-03 header matrix only |

Port registry lives in `e2e/scripts/lib/constants.mjs` (`PORTS`); nothing hard-codes a port elsewhere.
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

Seed also: mints an API key per persona → `e2e/state/keys.json` (`GET /api/me/api-key`); configures
`e2e-beta` as the origin-enforcement fixture (`enforceAllowedOrigins=true`, app-urls
`https://app.example.com`, `https://myapp-*.vercel.app`) once R1-05 lands; writes
`e2e/state/credentials.json` (existing).

## 4. Layout (additions under `e2e/`)

```
e2e/
  run-e2e.sh                 flags: --ci --pr --nightly --fresh --whitelabel --apply --mcp --mail --429 --upgrade --all
  scripts/
    reset.sh                 + waits for mailpit :8025, clears mailbox (DELETE /api/v1/messages)
    seed.mjs                 + TENANT_B, FLOOD, keys.json, e2e-beta origin fixture, post-R2-05 client redemption
    probe-visibility.mjs     + cross-tenant block
    restart-api.mjs          `docker compose up -d --force-recreate api` with env overrides (e.g. Cli__MinVersion), re-waits /swagger
    serve-dir.mjs            static server for generated apps (R2-00)
    smoke-widget.sh          R3-03 §E
    reset-branding.mjs       restores /api/branding defaults after white-label runs (always in `finally`)
    lib/
      api.mjs                existing get/post/patch/login/ApiError (+ del, put, header option for X-Pointer-Client)
      constants.mjs          personas, projects, PORTS, enums
      mail.mjs               Mailpit client (§5)
      cli.mjs                spawnCli({cwd, args, env}) → {stdout, stderr, code, json?}; uses CLI_ENTRY (§6)
      git.mjs                tempRepo(), bareRemote(), refsSnapshot(), assertRefsUnchanged()
      report.mjs             record(id, tier, layer, role, result, ms, detail) → state/report.md (every phase)
      mcp.mjs                @modelcontextprotocol/sdk stdio client for R2-02 (zero LLM)
  fixture-app/               alpha, beta, smoke (existing) + vite-react (R3-01), static-template, csp-nonce, pinned-tamper
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

- **PR tier**: `CLI_ENTRY=node <repo>/cli/dist/cli.js` after `npm run build` in `cli/` — zero global
  state, tests the built artifact.
- **Nightly `packaging` job**: `cd cli && npm pack` → `npm i -g ./pointer-feedback-*.tgz` in the job
  sandbox → run `pointer init|doctor` and `node -e "import('pointer-feedback/vite')"` — proves `bin`,
  `exports`, shebang. **Never `npm link`.**
- Every CLI scenario runs in a fresh temp dir with `git init`, `git config user.email e2e@example.com`,
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
| **PR** | every PR touching `API/**`, `Application/**`, `Domain/**`, `Infrastructure/**`, `web-component/**`, `cli/**`, `e2e/**` | ≤ 15 min | api + widget + cli specs marked PR; existing phases 1–4 |
| **Nightly** | `schedule` 03:00 UTC + `workflow_dispatch` | ≤ 45 min | PR set + fresh-app inits, white-label, mail, packaging, restart-dependent scenarios, header matrix, **429 phase last**, `upgrade` job |
| **Manual** | on demand | — | anything needing a real AI tool, a real deploy, or human judgement |

Phase order inside a run: reset → seed → probe → api specs → cli specs → widget specs → mail → fresh-app →
white-label (with `reset-branding` in `finally`) → **429 phase last** (flood user; optional isolated
compose project) → report. Restart-dependent scenarios (`restart-api.mjs`) are nightly and grouped so
the API restarts at most 3 times per run.

## 9. Flakiness rules

- No fixed sleeps: `expect.poll`/`toPass` with explicit timeouts; mail 10 s; widget notification badge
  ≤ 70 s (widget poll made configurable via `window.__POINTER_CONFIG__.notifyPollMs`, suite sets 1000).
- Rate-limit buckets: all 429 scenarios in the last phase with dedicated users; regular suite login
  calls stay far below 60/min/IP.
- Widget boot: always `waitForResponse('**/capture-config')` before interacting (`widget.spec.ts:61-66`).
- Ports: registry + `--strictPort`; fixture servers killed by trap.
- Determinism: pinned minor versions for generators (fresh-app), 1.1 s comment spacing in seed, one
  whole-run retry only for the 300 s fresh-app budget.
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
## Scenarios | id | tier | layer | role | PASS/FAIL/SKIP | ms | detail |
## Mail evidence | to | subject | scenario id |
## Failures  trace paths · `docker compose logs api --tail 100`
```
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
```
Scenario ids: `<doc>-<nn>` (e.g. `R1-05-03`); the human-readable names used in the execution docs are
kept as the `intent` column so both cross-reference.
