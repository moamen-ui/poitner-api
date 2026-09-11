# R2-00 — Fresh-app init E2E + white-label CI job  (NEW-4b + §52 · Release 2 · 2 d + 1 d)

## Goal
Two automated proofs, run in CI on a schedule and on demand: (1) the 5-minute promise — a brand-new
Vite app, a brand-new static site and a brand-new Angular app each go from zero to a posted comment
using only `npx pointer-feedback init` and a browser, **no AI tool**; (2) the white-label hedge — the
whole CLI + widget flow runs against a server whose `/api/branding` returns a different product name,
and no literal "Pointer" leaks into any CLI output or widget UI text; and (3) **§52 — the rebrand
rehearsal**: the same stack served under a *different domain* (`pick-it.test`) as well as a different
name (`PickIt`), proving the widget, the served skills, `/embed.js`, the CLI, the landing page and the
**e-mails** all follow the new identity, then restoring both.

## Out of scope
- AI-under-test cases (`e2e/ai/`, `--with-ai`) — unchanged, never scheduled (real tokens).
- Next.js / monorepo init (routed to the skill by design, R1-02).
- Dashboard E2E (separate repo).
- (Mail is **not** out of scope: Mailpit is in the base stack from R1 — password-reset and staff-invite emails already exist today; see `docs/roadmap/testing/00-HARNESS.md` §5.)

## Prerequisites
- R1-02 (`init`), R1-04 (`doctor`) merged; R1-07 (`e2e/` scheduled in CI) merged — this doc **extends**
  the workflow R1-07 creates (`.github/workflows/e2e.yml`).
- Facts: `e2e/run-e2e.sh` phases and `e2e/scripts/seed.mjs` constants (`e2e/scripts/lib/constants.mjs`:
  `SUPER_ADMIN`, `TENANT_OWNER`, `USERS`, `CLIENT`, `PROJECTS`); Playwright already configured
  (`e2e/playwright.config.ts`). The existing fixture server `e2e/fixture-app/serve.mjs:9-15` accepts
  **only** `<alpha|smoke|beta>` site names — it cannot serve a generated app directory (see Task 3b).
- Branding is DB-backed and writable by a super admin: `API/Controllers/Admin/BrandingController.cs`
  (write DTO `Application/DTOs/Branding/BrandingWriteDto.cs`: `ProductName`, `Tagline`, `PrimaryColor`,
  `Urls { App, Demo, Docs, Landing }`); default name `DefaultProductName = "Pointer"`
  in `Application/Services/Implementation/BrandingService.cs:10`.
- **The widget already consumes `/api/branding`**: `web-component/src/constants.ts:131-143`
  (`loadBranding` → `getBrandName()`), used for the login-modal title (`templates.ts:17`), tooltips
  (`templates.ts:69`) and toasts (`element.ts:634,857`). One hard-coded leak remains: the launcher's
  `title="Open Pointer feedback" aria-label="Open Pointer feedback"` at `templates.ts:133` — fixed in Task 0.
- R1-07 defines exactly two CI jobs, `unit` and `e2e` (there is **no** `seed` job).

## Design

### Fresh-app scenarios (`e2e/fresh-app/`)
Decision: generate the apps at test time into `e2e/state/fresh/<stack>/` (gitignored) so the test
proves real scaffolding, not a checked-in fixture. Generators are pinned to a **minor** version (a
major-only pin is not deterministic); bump the pins deliberately in a PR:

| stack | generator (pinned minor) | expected `init` outcome |
|---|---|---|
| `vite` | `npm create vite@6.0 -- --template vanilla-ts` | `index.html` gains the env-guarded snippet (`pointer-init.md:71-91` shape) inside the `<!-- pointer-feedback:start/end -->` markers; **`.env`** (not `.env.local` — R1-02 §D Decision) gains `VITE_POINTER_*`; `.pointer/config.json` + `credentials.env` written |
| `static` | `mkdir && cp e2e/fresh-app/templates/static/index.html` | `<script src=".../pointer.js" defer>` + `<pointer-feedback …>` inserted before `</body>` inside the markers (`pointer-init.md:107-116` shape) |
| `angular` | `npx @angular/cli@20.0 new fresh-ng --defaults --skip-git --skip-install` | `init` prints the **skill-routing message** (Angular is *not* deterministic in R1-02 §F) and writes `.pointer/` + skills only — assertion is on the message + files, not on injection |

Driver script `e2e/fresh-app/run.mjs` (Node, no Playwright) per stack:
1. Scaffold into `e2e/state/fresh/<stack>` (wipe first).
2. Obtain an API key for the seeded developer user (`USERS.developer`) via
   `POST /api/auth/login` → `GET /api/me/api-key`.
3. Run `node <repo>/cli/dist/cli.js init --server $API --key $KEY --create "Fresh <stack> <runId>" --environment local --tool other --yes --json`
   (flags from R1-02 §B: `--create <name>` takes the project **name**; the key is derived by the CLI
   as `fresh-<stack>-<runid>`; `--yes` requires `--key` and `--project`-or-`--create`; `--json` implies
   `--yes` and prints the R1-02 §C summary object).
4. Assert exit code 0 and, from the R1-02 §C `--json` schema: `ok === true`, `project.created === true`,
   `project.key` matches `^fresh-<stack>-`, `injected`/`routedToSkill` per the table (`vite`/`static`:
   `injected: true, routedToSkill: false`; `angular`: `injected: false, routedToSkill: true`), `files`
   contains `.pointer/config.json`; record `product` for the brand check.
5. Run `node cli/dist/cli.js doctor --json` in the app dir → `ok: true`.
6. For `vite` and `static` only: build and serve the app —
   - `vite`: `npm install && npm run build` in the app dir, then `npx vite preview --port 4174 --strictPort`;
   - `static`: `node e2e/scripts/serve-dir.mjs e2e/state/fresh/static 4174` (Task 3b — a 10-line static
     file server; `fixture-app/serve.mjs` cannot serve arbitrary dirs);
   then run Playwright spec `e2e/fresh-app/fresh.spec.ts` with `FRESH_URL=http://localhost:4174`:
   - widget host element present, shadow root attached, login modal opens;
   - log in as `USERS.developer`; click a `<h1>`; type `E2E fresh comment`; submit;
   - `GET /api/projects/<project.key>/comments?view=summary` returns 1 item with that body.
7. Wall-clock budget per stack: **≤ 5 min** measured from step 3 to step 6's API assertion; the test
   fails above 300 s (this *is* the product promise). Shared CI runners jitter: the runner allows
   **one automatic retry** of the whole stack on a budget failure, and the measured seconds for every
   attempt are printed and pasted into the report.

### White-label scenario (`e2e/whitelabel/`)
1. Super admin `PUT /api/admin/branding` with `BrandingWriteDto` `{ productName: "Acme Review", tagline: "Review anything", urls: { app: "https://app.acme.test" } }`
   (field names verified against `Application/DTOs/Branding/BrandingWriteDto.cs`: `ProductName`, `Tagline`,
   `PrimaryColor`, `Urls.App/Demo/Docs/Landing` — patch semantics, null fields untouched).
2. Re-run the `static` fresh-app flow above (same script, `--stack static --brand-check`). The brand
   run executes `init` **twice**: once with `--json` (machine assertions, incl. `product === "Acme Review"`)
   and once **without** `--json` (human output capture — the brand name is only guaranteed in human mode).
3. Assertions:
   - capture **all** CLI stdout/stderr from the human-mode `init` and from `doctor`; assert it contains
     `Acme Review` and does **not** match the leak regex `/(?<![-\w])Pointer(?![-\w])/` (case-sensitive;
     the npm package name `pointer-feedback`, the `<pointer-feedback>` tag and `.pointer/` paths are
     allowed by construction — they are frozen contract names, R1-01).
   - Playwright (**on**, not optional — the widget consumes branding today, `constants.ts:131-143`):
     (a) widget shadow-DOM `innerText` does not match the leak regex; (b) the login-modal title equals
     `Acme Review` (`templates.ts:17` uses `getBrandName()`); (c) **every** `title` and `aria-label`
     attribute value inside the shadow root does not match the leak regex (this is what catches
     `templates.ts:133` if Task 0 regresses).
   - `GET /skill.md` and `/pointer-init.md` served bodies contain the server origin (placeholder
     rewrite, `API/Program.cs:197-223`) — brand name in skills is out of scope (they're prose).
4. Restore branding: `PUT /api/admin/branding` with the defaults from `BrandingService.cs:10-18`
   (`productName: "Pointer"`, tagline, `urls.app` default) — there is no reset endpoint.

### Mock-domain rebrand rehearsal (`e2e/rebrand/`) — §52

The white-label scenario above swaps only the **name**. This adds the **domain**: run the whole stack
as "PickIt at pick-it.test", prove every emitted URL and every e-mail follows, then restore. Full
design in `docs/roadmap/testing/00-HARNESS.md` §13 (binding); the essentials an implementer needs:

1. **`pick-it.test`, plain HTTP by default.** `.test` is RFC 6761-reserved (never resolves publicly).
   **`.dev` cannot be used locally** — Chrome HSTS-preloads it, so `http://pick-it.dev` is upgraded to
   HTTPS before any request leaves the browser. The production brand domain may still be `.dev`; the
   rehearsal only needs the same *shape*, because every assertion is on an emitted string or on host
   routing.
2. **Resolution without `sudo`**: Chromium `--host-resolver-rules=MAP pick-it.test 127.0.0.1` via
   `playwright.config.ts` `launchOptions.args` (the file has no `launchOptions` today, `:10-13`), gated
   on `E2E_MOCK_DOMAIN` so normal runs are unchanged. Node-side specs cannot use it — they send
   `Host: pick-it.test` to `127.0.0.1:8090` instead and assert on the response body.
3. **`Pointer__PublicUrl` is the emission knob.** `PointerUrlResolver.ResolvePublicUrl`
   (`API/Extensions/PointerUrlResolver.cs:15-20`) prefers `Pointer:PublicUrl` over `{scheme}://{host}`,
   and is the single source for `/embed.js` (`Program.cs:288`), the `<POINTER_SERVER>` placeholder
   rewrite (`Program.cs:213-214`) and branding asset URLs (`BrandingController.cs:38`). Set and cleared
   with the existing `restart-api.mjs`.
4. **Assert the runtime-brandable surfaces, never the frozen ones.** Brandable: `/api/branding` payload,
   widget text (`constants.ts:131-143` → `templates.ts:17,69,133`), landing `[data-brand-name]`
   (`landing/index.html:795-797`), CLI human output, e-mail subject/body, invitation join links, asset
   URLs. Frozen and therefore *expected* to still say "pointer": the `<pointer-feedback>` tag,
   `window.__pointerEmbedded`, `/pointer.js`, `.pointer/`, `POINTER_*`, `pointer_token`, the npm package
   (`R1-01-contract-freeze.md`). The existing leak regex `/(?<![-\w])Pointer(?![-\w])/g` already exempts
   them via its lookarounds — do not widen it.
5. **E-mail is the point.** With only `urls.app` set (never `app_base_url`), the invitation join link
   must come out as `http://pick-it.test:8090/join?code=` — that exercises the
   `app_base_url → brand_url_app → compiled default` chain shipped in `42e534e`, i.e. a rebrand that
   touches only branding still produces correct links. Reset subject carries the product name
   (`AuthService.cs:66` interpolates into the **subject**, not the body).
6. **TLS variant, manual only** (`--mock-domain-tls`): the R3-03 Caddy container gains a `pick-it.test`
   block with `tls internal` on 8443; Playwright sets `ignoreHTTPSErrors`. It is the only way to prove
   the `X-Forwarded-Proto` path (`Program.cs:130-140`) makes `/embed.js` emit `https://`.
7. **Teardown is a `finally` phase step, not a spec** (same reasoning as branding restore): reset
   branding → `restart-api.mjs` with no override → assert both. A killed run is cleaned by the next
   `reset.sh` (`down -v`), so the rehearsal must only ever run after a reset, never against a
   long-lived local stack.

**Relationship to the rebrand**: this is how `docs/rebranding/verify-no-pointer.sh` and the plan's
`NAME_LOWER`/`DOMAIN` answers get exercised *before* the rename. The grep proves no brand string remains
in source; the rehearsal proves the running product works under the new identity. Run both green as a
gate before executing `REBRANDING-PLAN.md`.

### CI wiring (`.github/workflows/e2e.yml`, extends R1-07)
- New job `fresh-app` (matrix `stack: [vite, static, angular, next]` — `next` asserts the hand-off message and that no app file changed (nightly only, no injection)) and job `whitelabel`, both
  `needs: e2e` (R1-07 defines exactly two jobs, `unit` and `e2e`; there is no `seed` job — the `e2e`
  job's `run-e2e.sh` performs reset + seed). Each new job boots its own compose stack + seed
  (`bash e2e/scripts/reset.sh && node e2e/scripts/seed.mjs`) — jobs run on separate runners and cannot
  share the `e2e` job's containers. Both run on `schedule` (R1-07's cron) and `workflow_dispatch`;
  **not** on every PR (npm scaffolding is slow) — add `pull_request` only for paths `cli/**`.
- Node 20; `npm ci` in `cli/` then `npm run build`; Playwright browsers cached.
- Artifacts on failure: Playwright trace + the generated app dir (zip).

## Tasks
0. **Widget brand leak fix** (`web-component/src/templates.ts:133`): replace the literal
   `title="Open Pointer feedback" aria-label="Open Pointer feedback"` with
   `title="Open ${getBrandName()} feedback" aria-label="Open ${getBrandName()} feedback"` (import from
   `./constants`); `npm run build`; commit the regenerated `API/wwwroot/pointer.{js,css}`. Grep
   `web-component/src/**/*.ts` for any other literal `Pointer` in user-visible strings and convert them
   the same way; list them in the report.
1. `e2e/fresh-app/run.mjs` — scaffold/init/doctor/build/serve driver; flags `--stack`, `--brand-check`, `--api`, `--keep`; prints per-attempt wall-clock seconds; one automatic retry on budget failure.
2. `e2e/fresh-app/fresh.spec.ts` — Playwright spec (login → click → comment → API assert), reuse helpers from `e2e/widget/widget.spec.ts`.
3. `e2e/fresh-app/templates/static/index.html` — the 20-line static page with an `<h1>` and a form.
3b. `e2e/scripts/serve-dir.mjs <dir> <port>` — ~10-line `node:http` static file server (content-type by extension for `.html/.js/.css/.json/.png/.svg`, `index.html` fallback for `/`); used for the `static` stack and reusable by later docs.
4. `e2e/scripts/set-branding.mjs` + `e2e/scripts/reset-branding.mjs` — super-admin PUT helpers using the `BrandingWriteDto` field names above (cite the DTO file in a code comment).
5. `e2e/whitelabel/whitelabel.spec.ts` — the leak-regex assertions over captured CLI output (`e2e/state/whitelabel/cli-output.txt`), widget `innerText`, login-modal title, and all shadow-root `title`/`aria-label` attributes.
6. `e2e/run-e2e.sh` — add `--fresh` and `--whitelabel` flags calling the above (default off; `--all` turns both on).
7. `.github/workflows/e2e.yml` — add the two jobs as designed (`needs: e2e`, own reset+seed).
8. `e2e/README.md` — document the new phases, the 5-minute budget + retry rule, and how to run one stack locally.
9. `.gitignore` (repo root) — ensure `e2e/state/` is ignored (verify; add if missing).
10. **`e2e/playwright.config.ts`** — add `launchOptions.args` with
    `--host-resolver-rules=MAP ${E2E_MOCK_DOMAIN} 127.0.0.1, MAP *.${E2E_MOCK_DOMAIN} 127.0.0.1` **only when
    `process.env.E2E_MOCK_DOMAIN` is set**, plus `ignoreHTTPSErrors: !!process.env.E2E_MOCK_TLS`. The file
    currently has a bare `use` block and no `launchOptions` (`:10-13`) — a default run must stay
    byte-identical.
11. **`e2e/rebrand/mock-domain.spec.mjs`** — R2-00-09 + R2-00-11: `Host`-header requests through
    `lib/api.mjs`, Mailpit assertions through `lib/mail.mjs`. Declares its 2-token `signup` spend.
12. **`e2e/rebrand/mock-domain.spec.ts`** — R2-00-10: resolver-rule browser run against
    `http://pick-it.test:8090/check?project=…` (the `/check` page from R1-02 §J, so one origin serves both
    page and widget), shadow-root text + launcher attribute sweep, `server` attribute assertion.
13. **`e2e/scripts/assert-origin-default.mjs`** — teardown check: `/embed.js` advertises the request host
    again and `Pointer__PublicUrl` is absent from the container env.
14. **`e2e/run-e2e.sh`** — `--mock-domain` flag (and `--mock-domain-tls` for the manual variant) wrapping
    the phase as `try { 09,10,11 } finally { reset-branding.mjs; restart-api.mjs (no override);
    assert-branding-default.mjs; assert-origin-default.mjs }`. The phase runs **after** the whitelabel
    phase's teardown, never overlapping its brand window.
15. **`e2e/compose.caddy.yaml`** (shared with R3-03) — add a `pick-it.test` site block, `tls internal`,
    `reverse_proxy api:8080`, published 8443. Manual variant only.
16. **`.github/workflows/e2e.yml`** — extend the `whitelabel` job (or add `rebrand-rehearsal`,
    `needs: e2e`) with `E2E_MOCK_DOMAIN=pick-it.test` / `E2E_MOCK_BRAND=PickIt`; schedule +
    `workflow_dispatch` only. The TLS variant is not scheduled.
17. **`docs/rebranding/REBRANDING-PLAN.md`** (on the `docs/rebranding-plan` branch — a **cross-branch
    follow-up**, not editable from here): add the rehearsal to §12's acceptance gate next to
    `verify-no-pointer.sh`. List it in the report rather than silently skipping it.

## Dashboard tasks
none

## Tests
- These docs **are** tests. Names: `fresh-app: vite`, `fresh-app: static`, `fresh-app: angular (skill-routed)`, `whitelabel: cli-output has no brand leak`, `whitelabel: widget text has no brand leak`, `whitelabel: widget title/aria-label have no brand leak`.
- Also hosts the scenarios other R1 docs name: `init-vite-no-ai`, `init-static-no-ai`, `init-next-handoff` (add a fourth matrix entry `next` using `npx create-next-app@15.0 --ts --app --no-eslint --use-npm --yes`, assertion = hand-off message + no `app/` edits), `init-yes-ci`, `doctor-green-after-init`, `doctor-detects-tracked-credentials`, `quickstart-copies-prefilled-command` (dashboard — out of scope here, note as follow-up), `legacy-key-still-logs-in-after-upgrade` / `regenerated-key-old-one-rejected` (API-level, in `e2e/scripts/probe-visibility.mjs` style), `origin-enforced-blocks-foreign-origin`, `origin-enforced-allows-localhost-local`, `comment-burst-429` (API-level probes).
- Unit: none.

## Acceptance criteria
- [ ] `bash e2e/run-e2e.sh --fresh` passes locally for all stacks with `cli/dist/cli.js` built from the branch.
- [ ] Each of `vite` and `static` completes init→first-comment in ≤ 300 s (seconds printed per attempt; at most one retry used).
- [ ] `angular` and `next` assert the hand-off message text exactly as R1-02 §F defines it and that no `index.html`/`app/` edit happened.
- [ ] `bash e2e/run-e2e.sh --whitelabel` passes; the captured human-mode CLI output contains `Acme Review` and zero matches of the leak regex; the widget assertions (innerText, modal title, `title`/`aria-label`) pass.
- [ ] Task 0 landed: `grep -n '"Open Pointer feedback"' web-component/src/templates.ts` returns nothing; rebuilt artifacts committed.
- [ ] CI `fresh-app` matrix and `whitelabel` jobs are green on `workflow_dispatch`.
- [ ] Branding is restored after the white-label run (a following `GET /api/branding` returns `productName: "Pointer"`).
- [ ] **`bash e2e/run-e2e.sh --mock-domain` passes**: with `Pointer__PublicUrl` set and branding swapped to
      `PickIt` / `pick-it.test`, `GET /api/branding`, `/embed.js`, `/skill.md` and `/pointer-init.md` all
      emit `http://pick-it.test:8090` and none contains `localhost:8090` or `<POINTER_SERVER>`.
- [ ] A browser reaching `http://pick-it.test:8090/check?project=…` through the resolver rule boots the
      widget; its shadow-root text contains `PickIt` and zero leak-regex matches; the launcher's
      `title`/`aria-label` are `Open PickIt feedback`; the `<pointer-feedback server=…>` attribute is the
      mock origin — while the element name, `/pointer.js` and `.pointer/` are unchanged (frozen contract).
- [ ] A password-reset subject contains `PickIt`, and a staff-invite (and, once R1-08 ships, a workspace
      invitation) join link starts `http://pick-it.test:8090/join?code=` **with `app_base_url` never set** —
      proving the `brand_url_app` fallback shipped in `42e534e`.
- [ ] Teardown: after the phase's `finally`, `GET /api/branding` returns the `BrandingService.cs:9-17`
      defaults, `/embed.js` advertises the request host again, and `Pointer__PublicUrl` is absent from the
      API container env — asserted even when the phase's specs failed.
- [ ] Manual `--mock-domain-tls` run recorded once: `/embed.js` emits `https://pick-it.test:8443` and the
      widget boots with no mixed-content error.

## Rollout / compatibility
No product change except the one-line widget `title`/`aria-label` fix (rebuilt `pointer.js`). CI minutes increase (~10 min/run); scheduled nightly only.
The mock-domain rehearsal adds **no product code at all** — it is config (`Pointer__PublicUrl`, already
read by `PointerUrlResolver`), DB branding (already writable) and test wiring. Its only risk is state
leakage, which the mandatory `finally` teardown plus the "only after a reset" rule contain. `.test` never
resolves publicly, so a mis-configured run fails closed rather than reaching a real host.

## Report template
- Files added/changed; per-stack wall-clock seconds (every attempt); the whitelabel leak-regex result and the list of widget strings converted in Task 0; CI run URL; anything skipped.
- For the rehearsal: the emitted origin from each of `/api/branding`, `/embed.js`, `/skill.md`; the join
  link from each asserted e-mail; confirmation that teardown restored both branding and origin; and the
  status of the cross-branch follow-up (task 17) on `REBRANDING-PLAN.md` §12.
