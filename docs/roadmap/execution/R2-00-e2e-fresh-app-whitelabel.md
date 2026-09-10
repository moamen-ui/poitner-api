# R2-00 — Fresh-app init E2E + white-label CI job  (NEW-4b · Release 2 · 2 d)

## Goal
Two automated proofs, run in CI on a schedule and on demand: (1) the 5-minute promise — a brand-new
Vite app, a brand-new static site and a brand-new Angular app each go from zero to a posted comment
using only `npx pointer-feedback init` and a browser, **no AI tool**; (2) the white-label hedge — the
whole CLI + widget flow runs against a server whose `/api/branding` returns a different product name,
and no literal "Pointer" leaks into any CLI output or widget UI text.

## Out of scope
- AI-under-test cases (`e2e/ai/`, `--with-ai`) — unchanged, never scheduled (real tokens).
- Next.js / monorepo init (routed to the skill by design, R1-02).
- Dashboard E2E (separate repo).
- Mail server (no email is sent in R2; see R2-05).

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

### CI wiring (`.github/workflows/e2e.yml`, extends R1-07)
- New job `fresh-app` (matrix `stack: [vite, static, angular]`) and job `whitelabel`, both
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
4. `e2e/whitelabel/set-branding.mjs` + `reset-branding.mjs` — super-admin PUT helpers using the `BrandingWriteDto` field names above (cite the DTO file in a code comment).
5. `e2e/whitelabel/whitelabel.spec.ts` — the leak-regex assertions over captured CLI output (`e2e/state/whitelabel/cli-output.txt`), widget `innerText`, login-modal title, and all shadow-root `title`/`aria-label` attributes.
6. `e2e/run-e2e.sh` — add `--fresh` and `--whitelabel` flags calling the above (default off; `--all` turns both on).
7. `.github/workflows/e2e.yml` — add the two jobs as designed (`needs: e2e`, own reset+seed).
8. `e2e/README.md` — document the new phases, the 5-minute budget + retry rule, and how to run one stack locally.
9. `.gitignore` (repo root) — ensure `e2e/state/` is ignored (verify; add if missing).

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

## Rollout / compatibility
No product change except the one-line widget `title`/`aria-label` fix (rebuilt `pointer.js`). CI minutes increase (~10 min/run); scheduled nightly only.

## Report template
- Files added/changed; per-stack wall-clock seconds (every attempt); the whitelabel leak-regex result and the list of widget strings converted in Task 0; CI run URL; anything skipped.
