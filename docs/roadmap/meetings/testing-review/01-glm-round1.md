


ai
fixture-app
node_modules
package-lock.json
package.json
playwright.config.ts
README.md
run-e2e.sh
scripts
state
test-results
widget

e2e/ai:
cases
harness.mjs
run-cases.mjs

e2e/ai/cases:
manifest.json
tc1.txt
tc2.txt
tc3.txt
tc4.txt
tc5.txt

e2e/fixture-app:
alpha
beta
serve.mjs
smoke

e2e/fixture-app/alpha:
checkout.js
index.html
style.css

e2e/fixture-app/beta:
index.html

e2e/fixture-app/smoke:
checkout.js
index.html
style.css

e2e/node_modules:
@playwright
fsevents
playwright
playwright-core

e2e/node_modules/@playwright:
test

e2e/node_modules/@playwright/test:
cli.js
index.d.ts
index.js
index.mjs
LICENSE
---
#!/usr/bin/env bash
# Full deterministic reset: drops the Postgres volume, brings the stack back up, waits for the
# API to be ready. Zero AI involvement — see docs/E2E_TEST_PLAN.md's "Token-cost" framing.
set -euo pipefail
cd "$(dirname "$0")/../.."

echo "==> Resetting stack (docker compose down -v)"
docker compose down -v --remove-orphans

# `down -v` can report the volume "still in use" if a container is slow to fully exit (observed:
# a prior interrupted run's containers hadn't finished tearing down yet), which then makes the
# next `up -d` fail with a container-name conflict. Wait for both named containers to actually
# disappear before proceeding, retrying the teardown once if they haven't.
wait_for_removal() {
  for _ in $(seq 1 15); do
    if ! docker ps -a --format '{{.Names}}' | grep -qE '^pointer-api-(api|db)-1$'; then
      return 0
    fi
    sleep 1
  done
  return 1
}

if ! wait_for_removal; then
  echo "==> Containers still present after down -v — retrying teardown"
  docker compose down -v --remove-orphans
  wait_for_removal || { echo "==> Containers still present — remove them manually and re-run"; exit 1; }
fi

echo "==> Starting stack (docker compose up -d)"
docker compose up -d

echo "==> Waiting for API on :8090 ..."
until curl -sf http://localhost:8090/swagger/v1/swagger.json > /dev/null 2>&1; do
  sleep 2
done

echo "==> API ready."
---
no local-mail-server dir
---
ConnectionStrings__Default=Host=db;Port=5432;Database=pointer;Username=pointer;Password=pointer
JWT__SigningKey=CHANGE_ME_min_32_chars_long_secret_key_123456
JWT__Issuer=pointer-api
JWT__LifetimeHours=12
ADMIN__EMAIL=admin@pointer.local
ADMIN__PASSWORD=ChangeMe123!
DBMigrationEnabled=true

# Optional — local mail testing via Mailpit, see ../local-mail-server. Omit to have emails
# just log-and-skip locally (the default). Never set in production (that stays on Brevo).
# Email__Provider=smtp
# Email__Smtp__Host=host.docker.internal
# Email__Smtp__Port=1025
# Email__FromEmail=dev@pointer.local
# Email__FromName=Pointer (local)

Application/Services/Implementation/EmailService.cs:19:    public async Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default)
Application/Services/Implementation/EmailService.cs:38:        var ok = await sender.SendAsync(to, subject, htmlBody,
Application/Services/Implementation/UserService.cs:54:    private async Task SafeSendAsync(string to, string subject, string html)
Application/Services/Implementation/UserService.cs:56:        try { await _emailService.SendAsync(to, subject, html); }
Application/Services/Implementation/UserService.cs:194:        await SafeSendAsync(user.Email, $"Your {approveProductName} account is approved",
Application/Services/Implementation/UserService.cs:221:        await SafeSendAsync(user.Email, $"Your {rejectProductName} account request",
Infrastructure/Email/SmtpEmailSender.cs:26:    public async Task<bool> SendAsync(string to, string subject, string htmlBody,
Application/Services/Implementation/AuthService.cs:66:                    await _emailService.SendAsync(user.Email, $"Reset your {resetProductName} password",
Application/Services/Implementation/AuthService.cs:143:            await _emailService.SendAsync(user.Email, $"Your {brand.ProductName} password was changed",
Application/Services/Implementation/InviteService.cs:164:        // invite itself over a notification (mirrors UserService.SafeSendAsync).
Application/Services/Implementation/InviteService.cs:171:                emailSent = await _emailService.SendAsync(emailNormalized,
Application/Services/Implementation/InviteService.cs:593:            emailSent = await _emailService.SendAsync(emailNormalized,
Application/Services/Implementation/SuggestionService.cs:223:                try { await _emailService.SendAsync(to, subject, html); }
Application/Services/Implementation/DemoService.cs:182:        var emailSent = await _emailService.SendAsync(
Application/Services/Interfaces/IEmailService.cs:10:    Task<bool> SendAsync(string to, string subject, string htmlBody, CancellationToken ct = default);
Infrastructure/Email/BrevoEmailSender.cs:27:    public async Task<bool> SendAsync(string to, string subject, string htmlBody,
Infrastructure/Email/BrevoEmailSender.cs:54:            using var res = await _http.SendAsync(req, ct);
Application/Abstractions/IEmailSender.cs:13:    Task<bool> SendAsync(string to, string subject, string htmlBody,
---
Application/Services/Implementation/AuthService.cs:20:    private readonly IResetTokenService _resetTokens;
Application/Services/Implementation/AuthService.cs:30:        IResetTokenService resetTokens,
Application/Services/Implementation/AuthService.cs:39:        _resetTokens = resetTokens;
Application/Services/Implementation/AuthService.cs:44:    public async Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request)
Application/Services/Implementation/AuthService.cs:59:                var resetBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
Application/Services/Implementation/AuthService.cs:60:                var resetProductName = resetBrand.ProductName;
Application/Services/Implementation/AuthService.cs:61:                var resetAppUrl = resetBrand.Urls.App.TrimEnd('/');
Application/Services/Implementation/AuthService.cs:62:                var token = _resetTokens.Create(user.PublicId, user.SecurityStamp);
Application/Services/Implementation/AuthService.cs:63:                var link = $"{resetAppUrl}/reset?token={Uri.EscapeDataString(token)}";
Application/Services/Implementation/AuthService.cs:66:                    await _emailService.SendAsync(user.Email, $"Reset your {resetProductName} password",
---
// Pure-API, zero-AI checks: role-visibility matrix, private-comment exclusion on BOTH the list
// and admin apply-queue surfaces, cross-project isolation, and ?environment= filtering. Must run
// (and pass) right after seed.mjs, before any AI-under-test invocation — if these fail, the AI
// cases run against an invalid scenario and their results mean nothing.
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { get, login, ApiError } from './lib/api.mjs';
import { PROJECTS } from './lib/constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const expected = JSON.parse(readFileSync(join(STATE_DIR, 'expected.json'), 'utf8'));
const credentials = JSON.parse(readFileSync(join(STATE_DIR, 'credentials.json'), 'utf8'));

let pass = 0;
let fail = 0;
function check(label, condition) {
  if (condition) {
    pass++;
    console.log(`  ok    ${label}`);
  } else {
    fail++;
    console.error(`  FAIL  ${label}`);
  }
}
function setEqual(actual, expectedIds) {
  const a = new Set(actual);
  const e = new Set(expectedIds);
  return a.size === e.size && [...e].every((id) => a.has(id));
}
function setExcludes(actual, excludedIds) {
  const a = new Set(actual);
  return excludedIds.every((id) => !a.has(id));
}

async function listComments(token, projectKey, query = '') {
  const res = await get(`/api/projects/${projectKey}/comments${query}`, { token });
  return res.items.map((c) => c.id);
}
---
Application/Services/Implementation/AiRuleService.cs
Application/Services/Implementation/AuthService.cs
Application/Services/Implementation/PlanService.cs
Application/Services/Implementation/UserService.cs
Application/Services/Implementation/EmailService.cs
Application/Services/Implementation/DemoService.cs
Application/Services/Implementation/InviteService.cs
Application/Services/Implementation/ProfileService.cs
Application/Services/Implementation/TenantService.cs
Application/Services/Implementation/SuggestionService.cs

Read everything (execution docs R1-01→R3-05, e2e harness, compose, email infra). Deliverable below.

---

# 1. HARNESS (`e2e/scenarios/HARNESS.md`)

**1.1 Stack (extends `docker-compose.yaml` — dev compose, never the prod one)**

| Service | Image | Ports | Notes |
|---|---|---|---|
| `db` | postgres:15 | 5433 | unchanged |
| `api` | existing `target: dev` | 8090 | env gains `Email__Provider=smtp`, `Email__Smtp__Host=mailpit`, `Email__Smtp__Port=1025` |
| `mailpit` | `axllent/mailpit` | 8025 (HTTP), 1025 (SMTP, internal only) | **Pick: Mailpit.** Justification: (a) the repo already assumes it — `SmtpEmailSender.cs:12` ("Mailpit by default") and `.env.example` mail block; (b) actively maintained vs MailHog (unmaintained/archived ~2020, stale Docker image); (c) smtp4dev is a heavier .NET/Razel image, slower cold start, Windows-first; Mailpit is a ~20 MB Go binary with a stable JSON API (`/api/v1/messages`, `/api/v1/search`, `DELETE /api/v1/messages`), parsed HTML/Text bodies, and an inbox UI for debugging. |
| fixture apps | none (host node) | 4173 smoke, 4174 fresh preview, 4175 vite-react | `serve.mjs` / `serve-dir.mjs` (R2-00 3b), `--strictPort`, started/killed by `run-e2e.sh` with the existing trap pattern |

Caddy only appears in the nightly R3-03 header-matrix job (throwaway `caddy:2-alpine` upstream→api).

**1.2 Directory layout under `e2e/`** (additions to today's tree)

```
e2e/
  run-e2e.sh              # flags: --ci --fresh --whitelabel --apply --mcp --mail --429 --restart-phase --all
  scripts/
    reset.sh              # + brings up mailpit, clears mailbox (DELETE /api/v1/messages) after /swagger ready
    seed.mjs              # extended (see 1.3)
    probe-visibility.mjs  # + cross-tenant block
    restart-api.mjs       # docker compose up -d --force-recreate api with env overrides, re-wait /swagger
    serve-dir.mjs         # R2-00 3b
    smoke-widget.sh       # R3-03 §E
    lib/{api,constants,mail,cli,git,report}.mjs
      mail.mjs  = Mailpit client: awaitMessage({to,subjectPrefix,timeoutMs:10_000,intervalMs:250}), assertNoMail(to), clear()
      cli.mjs   = spawn `node $REPO/cli/dist/cli.js` with {cwd,env,argv}; captures stdout/exit code
      git.mjs   = bare-remote fixtures in os.tmpdir(), refsSnapshot()/refsUnchanged()
      report.mjs= appends rows to e2e/state/report.md from EVERY phase (fixes R1-07 gap, see §4.11)
  state/                  # gitignored: expected.json, credentials.json, keys.json, report.md, fresh/, apply-work/, mcp-work/
  fixture-app/{alpha,beta,smoke,vite-react,static-template,csp-nonce,pinned-tamper}/
  widget/{widget,notifications,quick-access,privacy-snapshot,whitelabel}.spec.ts
  fresh-app/{run.mjs,fresh.spec.ts,templates/static/}
  apply/apply.spec.mjs    mcp/mcp.spec.mjs    cli/{init,doctor,update}.spec.mjs    mail/mail.spec.mjs
  scenarios/              # ← this deliverable: HARNESS.md + R1-01.md … R3-05.md
```

**1.3 Seed extensions (`scripts/seed.mjs` + `lib/constants.mjs`)**

Existing users map 1:1 to the founder's role list — nothing to invent:

| Role | Seed identity | Source |
|---|---|---|
| Super admin | `SUPER_ADMIN` admin@pointer.local | constants.mjs:10 |
| Workspace admin / tenant owner | `TENANT_OWNER` e2e-owner@example.com | :17 |
| Deputy / admin-tier | `USERS.deputy` (role "Workspace Admin Deputy", GrantsAdmin, not IsSuperAdmin) | :29 |
| Developer (+ automation account) | `USERS.developer` | :30 |
| PM | `USERS.pm` | :31 |
| Tester | `USERS.tester` | :32 |
| Client / QuickAccess | `CLIENT`, invited, scoped to e2e-alpha | :36 |

**Missing (to add):**
1. `TENANT_B_OWNER` + project `e2e-beta-b` — a **second tenant** for true cross-tenant negatives (today only cross-*project* within one tenant exists; e2e/README.md:33-35).
2. **API key per persona** — after each login, `GET /api/me/api-key` → `state/keys.json` (R2-01:183 assumes a "tenant owner key" that seed never mints).
3. **Sacrificial flood user** (`flood@example.com`, Tester role) — sole author for `comment-burst-429`, so the 30/min/user limiter never taints other scenarios.
4. **Post-R2-05 rework**: client login via magic link (`PasswordlessOnly` makes today's password-overwrite login at seed.mjs:105-106 impossible — see Gap §4.5).
5. `e2e-beta` becomes the R1-05 fixture: `enforceAllowedOrigins=true` + app-url rows (`https://app.example.com`, `https://myapp-*.vercel.app`).

**1.4 Reset strategy** — unchanged core: `reset.sh` = `docker compose down -v` (with the existing removal-retry, reset.sh:15-30) → `up -d` → wait `/swagger/v1/swagger.json` → **clear Mailpit mailbox**. Seed stays non-idempotent by design (seed.mjs:4); every orchestrated run = reset→seed. `run-e2e.sh` gains phase flags so partial runs (e.g. `--apply` alone) skip reset and reuse `state/` when `E2E_REUSE=1`.

**1.5 CLI under test** — PR CI: `node <repo>/cli/dist/cli.js` (the R2-00:50 precedent; tests the built artifact, zero global state). Nightly adds a **packaging job**: `cd cli && npm pack` → `npm i -g <tarball>` in the job sandbox → run `pointer init|doctor` *and* `import 'pointer-feedback/vite'` (proves `bin`, `exports`, shebang — things `dist/cli.js` invocation can't catch). **Never `npm link`**: mutates global state, drifts between runs, needs cleanup on failure.

**1.6 Dashboard (separate repo)** — three layers, no UI mocking:
1. **This repo, PR CI**: contract guard — fetch `/swagger/v1/swagger.json`, assert the endpoints/DTO names Orval regen depends on exist and inner-type annotations are intact (the AGENTS.md convention, mechanically checked).
2. **This repo, API-level**: dashboard behaviours decomposed to their endpoints (R2-04 bell = `/api/me/notifications/unread-count`; R2-06 badge = header-gated detail GET).
3. **pointer-dashboard repo**: its own Playwright suite pointed at *this* compose stack, consuming `e2e/state/credentials.json` + `keys.json`. Scenario `R1-03-02` lives there (R2-00:13 correctly scopes it out; Gap §4.13). Ids prefixed `DASH-` and cross-referenced from our scenario docs.

**1.7 Email assertions** — endpoint + shape:

| Check | Call | Assertion |
|---|---|---|
| list/search | `GET :8025/api/v1/messages?to=<email>` | JSON `{total, messages:[{ID, Subject, To:[{Address}], …}]}`; poll 250 ms → 10 s timeout, **never fixed sleep** |
| body | `GET :8025/api/v1/message/<ID>` | `.Body.HTML` / `.Text` |
| password reset | after `POST /api/auth/forgot-password` | subject prefix `Reset your`, body contains `/reset?token=` and branding productName |
| invite (R2-05, setting on) | toggle `QuickAccessInviteEmailEnabled`, re-invite | body contains `pointer_invite=` + 43-char token; **must not contain** `Password:` |
| default-off | default invite | `assertNoMail(client@…)` + response `emailSent=false` |
| approval / password-changed | approve user; change password | subjects per UserService.cs:194 / AuthService.cs:143 |
| notifications (R2-04) | n/a — email explicitly held (R2-04:10) | assert **no** mail on notify events until §32 ships |

**1.8 Evidence — `e2e/state/report.md`** (written by every phase via `lib/report.mjs`, uploaded unconditionally — fixes R1-07's ai-only report):

```md
# E2E report — <UTC ts> · git <sha> · mode <flags>
## Stack: api /api/meta {version, apiVersion, minCli, skillVersion, widgetVersion} · mailpit msgs=<n>
## Phases: | phase | result | duration | notes |
## Scenarios: | id | tier | layer | role | PASS/FAIL | ms | detail |
## Mail evidence: | to | subject | scenario-id |
## Failures: playwright trace paths + `docker compose logs api --tail 100`
```

---

# 2. TOKEN RULE

**Rule (one sentence):** *anything that will be re-run (PR CI or nightly) is a committed spec; MCP browser tools are permitted only for (a) one-time authoring recon, (b) attaching to a failing run to debug, (c) audits with no programmatic oracle.*

| Use | Tool | When allowed | Est. tokens |
|---|---|---|---|
| Repeatable functional assertion | Playwright `.spec.ts` / node `.spec.mjs` | Always (the default) | ~0 per run; runtime 1–5 s |
| API-only assertion | node fetch (`lib/api.mjs`) | Always — cheaper than any browser | 0 |
| Selector/timing recon while writing a spec | playwright-cli | Once per spec; findings frozen into the spec; session is throwaway | 15–60k |
| Debugging a failing trace (same seed state) | playwright-cli / chrome-devtools MCP | On red runs only | 20–80k |
| Judgment audits with no oracle | chrome-devtools MCP (`lighthouse_audit` for R3-05's a11y ≥ 95; dark-mode/390px visual check) | Nightly/manual; the one place MCP is *cheaper* than writing a Lighthouse runner (~30k vs a brittle half-day script) | 10–40k |
| Screenshot regression | Playwright `toHaveScreenshot()` | Never MCP for this | 0 |
| AI-tool-under-test flows (R2-02 manual apply via Claude/opencode) | real CLIs | **manual-only**, never CI (R1-07:10) | real spend, budgeted 9 invocations/CLI (run-e2e.sh:32) |

Break-even: a scenario expected to run ≥ 2 times must be a spec; one MCP recon session amortizes over the spec's lifetime, so even authoring-heavy specs (widget flows) are worth it.

---

# 3. SCENARIO INVENTORY

Roles: SA super-admin · WA tenant owner · DP deputy · DEV · PM · QA tester · CL client/quick-access · TB tenant-B owner · FL flood. Layers: A api · W widget · C cli · D dashboard · M mail. Tiers: **PR** PR CI · **N** nightly · **M** manual-only.

**HARNESS**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| H-01 | stack-up: /swagger 200, /api/meta 200, mailpit API 200 | — | A/M | PR |
| H-02 | full suite green twice consecutively from wiped DB (determinism) | — | A | N |
| H-03 | login + API key obtainable for all 7 personas | all | A | PR |
| H-04 | password-reset email arrives, link+brand shape | DEV | M | PR |
| H-05 | report.md exists after a zero-AI run | — | A | PR |

**R1-01 contract freeze** — E2E: none (the guard test `Tests/OnDiskContractTests.cs` is unit-level and complete; a served-file re-check would duplicate it). Scenario doc lists this explicitly.

**R1-02 init**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R1-02-01 | `init-vite-no-ai`: scaffold→init→inject markers+env→≤300 s | DEV | C | PR (paths `cli/**`) |
| R1-02-02 | `init-static-no-ai` | DEV | C | PR |
| R1-02-03 | `init-next-handoff`: message + no `app/` edits | DEV | C | N |
| R1-02-04 | `init-yes-ci`: --yes; missing key exit 2; bad key exit 3; idempotent re-run (git diff clean) | DEV | C | PR |
| R1-02-05 | events: `installed`+`first_comment` exactly once (2 comments) | WA | A | PR |
| R1-02-06 | /check page renders productName | — | A | N |

**R1-03 dashboard quickstart**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R1-03-01 | `initCommand()` unit matrix (in pointer-dashboard) | DEV | D | PR (dashboard repo) |
| R1-03-02 | `quickstart-copies-prefilled-command`: masked key shown, copy yields full cmd | WA | D | N (dashboard repo, this stack) |

**R1-04 doctor & meta**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R1-04-01 | /api/meta anonymous, all fields, ResponseCache | — | A | PR |
| R1-04-02 | `doctor-green-after-init` | DEV | C | PR |
| R1-04-03 | `doctor-detects-tracked-credentials` (exit 1, ✘ gitignore) | DEV | C | PR |
| R1-04-04 | `Cli__MinVersion=99` restart → doctor exit 5, init refuses | DEV | C+A | N (needs restart-api.mjs) |
| R1-04-05 | meta 121st/min/IP → 429 | — | A | N |

**R1-05 origins + rate limit**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R1-05-01 | `origin-enforced-blocks-foreign-origin` (403) + widget toast on 403 | QA | A+W | PR (api), N (toast) |
| R1-05-02 | `origin-enforced-allows-localhost-local` | QA | A | PR |
| R1-05-03 | wildcard rows: `myapp-pr-12` matches, `other` doesn't; `*.vercel.app`→400 | WA | A | PR |
| R1-05-04 | no-origin: staff key 200 vs quick-access 403 | DEV/CL | A | PR |
| R1-05-05 | `comment-burst-429`: 31st 429+Retry-After, second user unaffected | FL | A | N, **run last, isolated phase** |

**R1-06 key hardening**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R1-06-01 | DB: users.ApiKey NULL, api_keys hash-only (psql in db container) | — | A | N |
| R1-06-02 | `legacy-key-still-logs-in-after-upgrade` (old image→key→new image, same volume) | WA | A | N (dedicated two-image job) / M fallback |
| R1-06-03 | `regenerated-key-old-one-rejected` | WA | A | PR |
| R1-06-04 | reveal round-trip; rotate `Auth__ApiKeyEncryptionKey` → display fails, login still ok, nothing minted | WA | A | N |

**R1-07 CI schedule** — meta: R1-07-01 two green dispatched runs + deliberate-break fails with trace (M); R1-07-02 PR-path triggers (M).

**R2-00 fresh-app + whitelabel**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-00-01/02 | fresh-app vite / static: init→first comment ≤300 s, one retry | DEV | C+W | PR (`cli/**`), N |
| R2-00-03/04 | angular (skill-routed) / next (handoff): message+files only | DEV | C | N |
| R2-00-05 | whitelabel CLI human output: "Acme Review", leak-regex 0 | SA | C | N |
| R2-00-06 | whitelabel widget: innerText, modal title, all title/aria leak-free | SA | W | N |
| R2-00-07 | branding restored (GET /api/branding → "Pointer") | SA | A | N (same run, teardown assert) |

**R2-01 apply**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-01-01 | `apply: plan makes no edits` (stdout ids + "PLAN ONLY", porcelain clean) | WA | C | PR |
| R2-01-02 | `apply: separate commits` (1 commit/id, status=3, commitUrl=HEAD sha) | WA | C | PR |
| R2-01-03 | `apply: single commit` (--mark all, shared URL) | WA | C | PR |
| R2-01-04 | `apply: never pushes` (bare-remote for-each-ref byte-identical pre/post) | WA | C | PR |
| R2-01-05 | empty index → exit 1, no PATCH | WA | C | PR |
| R2-01-06 | `pointer get --json` = exact AiCommentView key set | WA | C | PR |
| R2-01-07 | non-admin DEV key → summary fallback + note | DEV | C | N |

**R2-02 MCP** (SDK stdio client — zero LLM)
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-02-01 | `tools/list` = exactly the 9 frozen names | WA | C | PR |
| R2-02-02 | `get_queue partitions untrusted` | WA | C | PR |
| R2-02-03 | `get_comment is the whitelisted view` (no payloadFlag/authorId) | WA | C | PR |
| R2-02-04 | `commit_and_mark stages files and never pushes` (+2nd call isError `git`) | WA | C | PR |
| R2-02-05 | real Claude Code + opencode apply via MCP | WA | C | **M** (tokens) |

**R2-03 version stamp**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-03-01 | served stamps: md line1 `---` + stamp after frontmatter; sh line 2; no leftover placeholder | — | A | PR |
| R2-03-02 | `/api/meta.skillVersion` == stamp | — | A | PR |
| R2-03-03 | `doctor: flags stale skill copy` (stamp A → env `Pointer:SkillVersion=B` restart → warn → `update` → green) | DEV | C+A | N |

**R2-04 notifications**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-04-01 | `notify: applied shows badge to author` (poll ≤70 s) | CL | W | N (slow poll) |
| R2-04-02 | `notify: thumbs-down reopens with note` (status=1, reply, applier sees CommentReopened) | CL+WA | W+A | N |
| R2-04-03 | `notify: read-all clears badge` | CL | W | N |
| R2-04-04 | cross-user 404 on foreign MarkRead; MarkAllRead scoped; unread-count | QA vs DEV | A | PR |
| R2-04-05 | verify rules: QA-author allowed, note required on 👎, non-author 403, non-applied 400 | CL/QA | A | PR |

**R2-05 quick-access**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-05-01 | `magic link signs in` (no modal, name shown) | CL | W | PR |
| R2-05-02 | `url param stripped on success` + stored pageUrl has no token | CL | W+A | PR |
| R2-05-03 | `url param stripped on failure` (strip before network call) | CL | W | PR |
| R2-05-04 | `rotated link rejected` (old → notice+modal; new works) | WA | W+A | PR |
| R2-05-05 | `password login blocked` for provisioned client | CL | A | PR |
| R2-05-06 | `61st login-with-invite in a minute is 429` | — | A | N, isolated phase **last** |
| R2-05-07 | mail: setting on → invite email carries link, no password | WA | M | N |
| R2-05-08 | mail: default off → no email, `emailSent=false` | WA | M | PR |

**R2-06 secrets flag**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R2-06-01 | `flag: secret in comment shows badge in widget` (ghp_ token → pill+tooltip) | QA | W | PR |
| R2-06-02 | `flag: absent from pointer get --json and pointer.sh get` (0 `payloadFlag` matches) | WA | C | PR |
| R2-06-03 | header gate: with `X-Pointer-Client: widget` true; without → 0 substrings; summary+apply-queue 0 | QA/WA | A | PR |
| R2-06-04 | edit removes secret → flag cleared on reload | QA | A | PR |

**R3-01 vite plugin + deploy**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R3-01-01 | `source-stamp-prod-build` (hash on element.sourcePath; `get --json` resolvedSource) | DEV | C+W | N |
| R3-01-02 | `stale-hash-warning` (rename → warning + prev-manifest hint) | DEV | C | N |
| R3-01-03 | `deploy-awareness-widget` ("✓ live"; exactly 1 POST /builds/load) | QA | W | N |
| R3-01-04 | `deploy-awareness-cli` (`status --deployed HEAD` marks ancestor only) | WA | C | N |
| R3-01-05 | manifest determinism: two clean clones byte-identical entries | DEV | C | N (matrix) |
| R3-01-06 | /builds negatives: uppercase 400, QA 403, foreign key 404, repeat `[]` | CL/TB | A | PR |

**R3-02 design tokens**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R3-02-01 | `init-writes-design-tokens` (colors∋primary, `--brand` in cssVars, stack.json tracked, rerun byte-identical, <2 s) | DEV | C | N (needs R3-01 fixture) |

**R3-03 widget release eng**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R3-03-01 | `widget-pinned-sri-loads` (+tamper middleware → console integrity error, no toolbar) | — | W | N |
| R3-03-02 | `widget-pinned-older-build` (previous retained hash, older bytes) | — | W | N |
| R3-03-03 | `widget-pinned-unknown-404` (+mismatch header; 404 not SRI in console) | — | W | N |
| R3-03-04 | `widget-nonce-csp-styles` (constructed stylesheet path) | — | W | N |
| R3-03-05 | `deploy-smoke-local` (smoke-widget.sh exit 0) | — | A | PR |
| R3-03-06 | cache-header matrix incl. via local Caddy container | — | A | N |
| R3-03-07 | perf-init longtask metric (warn-only until PERF_HARD_FAIL flip) | — | W | N |

**R3-04 snapshot privacy**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R3-04-01 | `snapshot-no-form-values` (typed email → `•••`) | CL | W | PR |
| R3-04-02 | `snapshot-mask-attribute` (masked table cell; selector kept) | QA | W | PR |
| R3-04-03 | `project-no-text-capture` (toggle via API; no text; pageTitle masked) | WA | A+W | PR |
| R3-04-04 | `legacy-widget-sanitized` (raw POST `value="x"` → `•••`) | WA | A | PR |

**R3-05 privacy page**
| id | intent | role | layer | tier |
|---|---|---|---|---|
| R3-05-01 | `landing-data-page-links` (footer→/data.html; strings "never captured", "data-snapshot-mask", "self-hosted") | — | W (static) | PR |
| R3-05-02 | Lighthouse a11y ≥ 95 + dark/390px check | — | W | N via chrome-devtools MCP (see §2) or M |

---

# 4. GAPS (doc:line)

1. **R2-00:14** "Mail server (no email is sent in R2)" is false today: password-reset (AuthService.cs:66) and non-quick-access invite (InviteService.cs:171) emails already exist — mail must enter the harness in R1, not R2-05. Related: 00-API-INVENTORY.md:111 "no local mail server anywhere" vs SmtpEmailSender.cs:12 + `.env.example` already assuming Mailpit — only the compose service is missing.
2. **R1-05:134 / R1-06:139** delegate E2E to "R2-00", which lists them (R2-00:127) but its Tasks (R2-00:104-120) contain **no task** implementing the origin/429/legacy-key probes — unowned work.
3. **R2-00:95** CI matrix `stack: [vite, static, angular]` and design table (:42-44) omit `next`, while R1-02:255 names `init-next-handoff` and R2-00:127 says "add a fourth matrix entry `next`" — contradictory; the scenario doc must pin `next` explicitly.
4. **R1-06:139** `legacy-key-still-logs-in-after-upgrade` is impossible under the standard reset (R1-07:14 `down -v`; R2-00:97-98): it needs a two-phase run (old API image → mint key → new image, **preserved volume**). No doc defines that harness; nightly dedicated job required.
5. **seed.mjs:104-106** overwrites the client's password then logs in — R2-05 makes that account `PasswordlessOnly` (R2-05 task 6; AC :119), so the seed breaks on merge; R2-05:113 doesn't mention updating seed.mjs.
6. **R2-04:75** hardcodes the 60 s poll and **R2-04:109** compensates with a 70 s test timeout — no test-only poll-interval knob is specified; guaranteeing ≥60 s/scenario and CI flake. Add `POINTER_NOTIFY_POLL_MS` env override to the widget.
7. **Rate-limit bucket collisions**: `login-with-invite` and `login-with-key` share the per-IP `login` policy (R1-05:99-100, R2-05:75-78); no doc orders R2-05:113's 61-request test (or R1-05:141's burst) last/isolates the IP — mid-suite 429s would fail unrelated logins.
8. **R1-05:120/141**: `comment-burst-429` needs a dedicated sacrificial user; no doc designates one or forbids concurrent comment activity for that user.
9. **Mid-suite API restarts** are required by R1-04:90 (`Cli__MinVersion`), R2-03:70 (`Pointer:SkillVersion`), R1-06:147 (key rotation) — no doc defines an env-override restart helper or its re-wait semantics.
10. **R3-03:132** `widget-pinned-sri-loads` needs a "test middleware" that tampers served bytes; R3-03's Tasks (:110-120) never create it — unowned prerequisite (dev-only tamper flag).
11. **R1-07:19** uploads `report.md` but it's only written under `--with-ai` (run-e2e.sh:32-35) — zero-AI runs produce no evidence artifact at all; contradicts the evidence requirement for the scheduled runs R1-07 exists to create.
12. **R2-01:183** "logs in with the seeded tenant owner key, `TENANT_OWNER`" — `TENANT_OWNER` is email/password; seed.mjs never calls `/api/me/api-key`, so no owner key exists to log in with.
13. **R1-03:55** assigns `quickstart-copies-prefilled-command` to R2-00, which explicitly excludes dashboard E2E (R2-00:13) — the scenario is homeless; it must move to the pointer-dashboard repo against this stack.
14. **R2-04:109** "staff sees CommentReopened" — the notification goes to `AppliedBy` specifically (R2-04:43-45); the scenario must name the applier, not arbitrary staff.
15. **Cross-tenant coverage**: only cross-*project* isolation exists today (e2e/README.md:33-35); R2-05:110 keeps tenant isolation at unit level. No doc seeds a second tenant — the founder's cross-tenant negative cases need it.

---

# 5. RISKS & mitigations

| Risk | Mitigation |
|---|---|
| Widget 60 s notification poll (R2-04) | test-only `POINTER_NOTIFY_POLL_MS` knob (default 60 000, suite sets 1 000); PR CI asserts the API surface instead; 70 s Playwright poll kept as ceiling, never a fixed sleep |
| Rate-limit bucket burn (login/comments/events/meta) | 429 scenarios in a dedicated **last** phase, sacrificial `flood` user, optional isolated compose project (`docker compose -p e2e-429`) with its own ports; the `e2e` job's own login calls stay <60/min/IP |
| Mail delivery latency / lost messages | poll Mailpit API 250 ms→10 s with backoff; clear mailbox in reset **and** before each mail scenario; SmtpEmailSender never throws (SmtpEmailSender.cs:52-56) so failures surface only via timeout — assert on the API-side `emailSent` flag too where available |
| Port collisions (8090, 5433, 8025, 1025, 4173-4175, 8099) | port registry in `lib/constants.mjs`; `--strictPort` everywhere; fixture servers killed via existing trap (run-e2e.sh:25); mailpit SMTP left unpublished (container-internal only) |
| `docker compose down -v` flakes | already handled by removal-retry (reset.sh:15-30); keep |
| npm scaffolding nondeterminism (fresh-app) | pinned **minor** generator versions (R2-00:38), npm cache in CI, exactly one whole-stack retry on the 300 s budget (R2-00:68-70); angular/next nightly-only |
| Mid-suite API restarts | `restart-api.mjs` recreates only `api`, preserves volume, re-waits on `/swagger`; scenarios depending on restart are nightly-tier so PR runtime stays <15 min (R1-07:42) |
| Git environment in apply/MCP scenarios | temp clones + bare remotes under `os.tmpdir()`; explicit `git config user.email` (feeds `appliedByLabel`, R2-01:139); never-push asserted by refs snapshot, not absence of code |
| Widget boot races | keep the `waitForResponse('/capture-config')` pattern (widget.spec.ts:61-66, e2e/README.md:52-54); every new widget spec inherits the helper |
| Seed timing (createdAt ordering) | keep the 1.1 s comment spacing (seed.mjs:18-20); total seed ~13 s, deterministic |
| AI-token leakage into CI | `run-e2e.sh` never defaults to `--with-ai` (run-e2e.sh:8-11); CI invokes explicit flags only; MCP browser tools banned from CI by the §2 rule |
| Whitelabel run leaves branding dirty | R2-00-07 teardown assertion + `reset-branding.mjs` in a `finally` (R2-00:91-92 has no reset endpoint — restore is mandatory cleanup) |
