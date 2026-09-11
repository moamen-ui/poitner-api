# R1-07 — Schedule the existing E2E suite in CI (NEW-4a · Release 1 · ½ day)

## Goal
The zero-AI part of the existing `e2e/` suite (reset → seed → probe-visibility → widget spec) runs
automatically on every PR to `main` and nightly, so regressions in the widget/API contract are caught
without spending AI tokens. R2-00 later adds the fresh-app `init` scenarios and the white-label job to
the same workflow.

## Out of scope
- The AI layer (`--with-ai`, TC1–TC5) — never in CI (real token spend).
- New scenarios (R2-00). Unit tests in CI (add a separate `dotnet test` job only if none exists — check first; today the only workflow is `.github/workflows/publish-clients.yml`).

## Prerequisites
- None. Facts: `e2e/run-e2e.sh` phases 1–4 (`e2e/run-e2e.sh:13-31`); **`e2e/scripts/reset.sh`** (there is no repo-root `scripts/`; `run-e2e.sh` `cd`s into `e2e/` first) does `docker compose down -v` then `up -d` and waits for `/swagger/v1/swagger.json` (`e2e/scripts/reset.sh:9,34`); seed creates tenant `e2e-owner@example.com`, roles Deputy/Developer/PM/Tester, a QuickAccess Client, projects `e2e-alpha`/`e2e-beta`; widget spec needs `fixture-app/serve.mjs smoke 4173`; Playwright chromium.

## Design
- New workflow `.github/workflows/e2e.yml`:
  - Triggers: `pull_request` (paths: `API/**`, `Application/**`, `Domain/**`, `Infrastructure/**`, `web-component/**`, `e2e/**`, `docker-compose.yaml`), `schedule: cron "0 3 * * *"`, `workflow_dispatch`.
  - Runner `ubuntu-latest`; steps: checkout → `docker compose version` → Node 20 setup → `cd e2e && npm ci && npx playwright install --with-deps chromium` → `bash run-e2e.sh --ci` (no `--with-ai`) → always upload artifacts: `e2e/state/report.md` (every phase writes it via `e2e/scripts/lib/report.mjs`, harness §11, so the zero-AI phases do produce it once the harness lands — keep `if-no-files-found: ignore` on it only as a safety net, since `e2e/state/` is gitignored and pre-harness runs have no file), `e2e/test-results/**`, `e2e/playwright-report/**` → on failure, `docker compose logs api --tail 300` to the job log.
  - Timeout 25 min. Concurrency group per ref (cancel in progress).
- `e2e/run-e2e.sh`: add `--ci` flag → exports `CI=1` and skips any interactive prompt (verify none exists). Exit code must propagate (script already uses `set -euo pipefail`). No reporter env vars — the reporter is configured in code (next line).
- `e2e/playwright.config.ts`: `retries: process.env.CI ? 1 : 0`, `trace: 'retain-on-failure'`, `reporter: process.env.CI ? [['github'], ['html', { open: 'never', outputFolder: 'playwright-report' }]] : 'list'`.
- Badge line in `README.md` and a "CI" paragraph in `e2e/README.md`.
- Also add the `dotnet test` job to the same workflow (`Tests/`) if no other workflow runs it — `just test` equivalent: `dotnet test --configuration Release --logger "trx"`. **Decision:** include it; a PR gate without unit tests is incomplete.

## Tasks
1. Confirm no existing unit-test workflow (`ls .github/workflows`). Create `.github/workflows/e2e.yml` with two jobs: `unit` (dotnet test) and `e2e` (needs: unit).
2. `run-e2e.sh --ci`; Playwright config CI tweaks.
3. Run the workflow via `workflow_dispatch` on the feature branch; fix flakiness (waits on `/swagger`, fixture server readiness) until two consecutive green runs.
4. README badge + `e2e/README.md` CI section.
5. Add `mailpit` to `docker-compose.yaml` (the compose file this workflow boots) per harness §2 — `axllent/mailpit:latest`, HTTP 8025 / SMTP 1025 container-internal, `Email__*` SMTP env on `api` — so the CI stack matches the harness.

## Dashboard tasks
None.

## Tests
This doc *is* test infrastructure. Verification = two green scheduled/dispatched runs.

## Acceptance criteria
- [ ] A PR touching `web-component/src/**` triggers the workflow and both jobs (`unit`, `e2e`) are green on the PR check run.
- [ ] After merge, the nightly `schedule` run on the default branch is green and visible in Actions with artifacts (`playwright-report`, traces on failure, `report.md`).
- [ ] Introducing a deliberate widget selector break in a test branch makes the `e2e` job fail with a Playwright trace artifact.
- [ ] Total runtime < 15 min.

## Rollout / compatibility
CI only. Requires Docker on the runner (present on `ubuntu-latest`). If the repo becomes private/limited on Actions minutes, keep `schedule` and drop `pull_request` for `web-component` paths only.

## Report template
Workflow file · links to the two green runs · runtime · any flake fixes made.
