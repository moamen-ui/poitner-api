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
  (`e2e/playwright.config.ts`); fixture server `e2e/fixture-app/serve.mjs` on `:4173`.
- Branding is DB-backed and writable by a super admin: `API/Controllers/Admin/BrandingController.cs`
  (write DTO `Application/DTOs/Branding/BrandingWriteDto.cs`); default name `DefaultProductName = "Pointer"`
  in `Application/Services/Implementation/BrandingService.cs:10`.

## Design

### Fresh-app scenarios (`e2e/fresh-app/`)
Decision: generate the apps at test time into `e2e/state/fresh/<stack>/` (gitignored) so the test
proves real scaffolding, not a checked-in fixture. Templates are pinned so runs are deterministic:

| stack | generator (pinned) | expected `init` outcome |
|---|---|---|
| `vite` | `npm create vite@6 -- --template vanilla-ts` | `index.html` gains the env-guarded snippet (`pointer-init.md:71-91` shape); `.env.local` gains `VITE_POINTER_*`; `.pointer/config.json` + `credentials.env` written |
| `static` | `mkdir && printf '<!doctype html>…' > index.html` | `<script src=".../pointer.js" defer>` + `<pointer-feedback …>` appended before `</body>` (`pointer-init.md:107-116` shape) |
| `angular` | `npx @angular/cli@20 new fresh-ng --defaults --skip-git --skip-install` | `init` prints the **skill-routing message** (Angular is *not* deterministic in R1-02) and writes `.pointer/` only — assertion is on the message + files, not on injection |

Driver script `e2e/fresh-app/run.mjs` (Node, no Playwright) per stack:
1. Scaffold into `e2e/state/fresh/<stack>` (wipe first).
2. Obtain an API key for the seeded developer user (`USERS.developer`) via
   `POST /api/auth/login` → `GET /api/me/api-key`.
3. Run `node <repo>/cli/dist/cli.js init --server $API --key $KEY --project fresh-<stack>-<runId> --create --environment local --tool other --yes --json`
   (flags from R1-02; `--create` creates the project when missing; `--json` prints the init summary).
4. Assert exit code 0 and the summary JSON `{ injected: true|false, routedToSkill: boolean, files: [...] }`
   matches the table above.
5. Run `node cli/dist/cli.js doctor --json` in the app dir → `ok: true`.
6. For `vite` and `static` only: serve the app (`vite preview --port 4174` / `serve.mjs 4174`), then run
   Playwright spec `e2e/fresh-app/fresh.spec.ts` with `FRESH_URL=http://localhost:4174`:
   - widget host element present, shadow root attached, login modal opens;
   - log in as `USERS.developer`; click a `<h1>`; type `E2E fresh comment`; submit;
   - `GET /api/projects/fresh-<stack>-<runId>/comments?view=summary` returns 1 item with that body.
7. Wall-clock budget per stack: **≤ 5 min** measured from step 3 to step 6's API assertion; the test
   fails above 300 s (this *is* the product promise).

### White-label scenario (`e2e/whitelabel/`)
1. Super admin `PUT /api/admin/branding` with `{ productName: "Acme Review", tagline: "…", urls: { app: "https://app.acme.test" } }`
   (exact field names: read `BrandingWriteDto.cs` at implementation time and copy them).
2. Re-run the `static` fresh-app flow above (same script, `--stack static --brand-check`).
3. Assertions:
   - capture **all** CLI stdout/stderr from `init` and `doctor`; assert it contains `Acme Review` and
     does **not** match `/\bPointer\b/` (case-sensitive; the npm package name `pointer-feedback` and
     the `<pointer-feedback>` tag are allowed — regex: `/(?<![-\w])Pointer(?![-\w])/`).
   - Playwright: widget shadow-DOM `innerText` does not match the same regex; login modal title uses
     the branding name (`web-component/src/auth-ui.ts` reads `productName` — verify at implementation;
     if the widget does not yet consume `/api/branding`, add that to the report as an R3 follow-up and
     assert only on the CLI).
   - `GET /skill.md` and `/pointer-init.md` served bodies contain the server origin (placeholder
     rewrite, `API/Program.cs:197-223`) — brand name in skills is out of scope (they're prose).
4. Restore branding to defaults (`DELETE`/reset endpoint if it exists, else PUT the defaults back).

### CI wiring (`.github/workflows/e2e.yml`, extends R1-07)
- New job `fresh-app` (matrix `stack: [vite, static, angular]`) and job `whitelabel`, both `needs: seed`
  (the job R1-07 defines that boots compose + seeds). Both run on `schedule` (R1-07's cron) and
  `workflow_dispatch`; **not** on every PR (npm scaffolding is slow) — add `pull_request` only for
  paths `cli/**`.
- Node 20; `npm ci` in `cli/` then `npm run build`; Playwright browsers cached.
- Artifacts on failure: Playwright trace + the generated app dir (zip).

## Tasks
1. `e2e/fresh-app/run.mjs` — scaffold/init/doctor/serve driver; flags `--stack`, `--brand-check`, `--api`, `--keep`.
2. `e2e/fresh-app/fresh.spec.ts` — Playwright spec (login → click → comment → API assert), reuse helpers from `e2e/widget/widget.spec.ts`.
3. `e2e/fresh-app/templates/static/index.html` — the 20-line static page with an `<h1>` and a form.
4. `e2e/whitelabel/set-branding.mjs` + `reset-branding.mjs` — super-admin PUT helpers (read `BrandingWriteDto.cs` for field names; cite them in code comments).
5. `e2e/whitelabel/whitelabel.spec.ts` — the leak regex assertions over captured CLI output (`e2e/state/whitelabel/cli-output.txt`) and widget text.
6. `e2e/run-e2e.sh` — add `--fresh` and `--whitelabel` flags calling the above (default off; `--all` turns both on).
7. `.github/workflows/e2e.yml` — add the two jobs as designed.
8. `e2e/README.md` — document the new phases, the 5-minute budget, and how to run one stack locally.
9. `.gitignore` (repo root) — ensure `e2e/state/` is ignored (verify; add if missing).

## Dashboard tasks
none

## Tests
- These docs **are** tests. Names: `fresh-app: vite`, `fresh-app: static`, `fresh-app: angular (skill-routed)`, `whitelabel: cli-output has no brand leak`, `whitelabel: widget text has no brand leak`.
- Unit: none.

## Acceptance criteria
- [ ] `bash e2e/run-e2e.sh --fresh` passes locally for all three stacks with `cli/dist/cli.js` built from the branch.
- [ ] Each of `vite` and `static` completes init→first-comment in ≤ 300 s (time printed in the log).
- [ ] `angular` asserts the skill-routing message text exactly as R1-02 defines it and that no `index.html` edit happened.
- [ ] `bash e2e/run-e2e.sh --whitelabel` passes; the captured CLI output file contains `Acme Review` and zero matches of the leak regex.
- [ ] CI `fresh-app` matrix and `whitelabel` jobs are green on `workflow_dispatch`.
- [ ] Branding is restored after the white-label run (a following `GET /api/branding` returns `productName: "Pointer"`).

## Rollout / compatibility
No product change. CI minutes increase (~10 min/run); scheduled nightly only.

## Report template
- Files added/changed; the three per-stack wall-clock numbers; the whitelabel leak-regex result; CI run URL; anything skipped (e.g. widget branding consumption not yet implemented → R3 follow-up).
