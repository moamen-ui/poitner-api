# R1-07-tests — Schedule the E2E suite in CI

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-07-e2e-schedule.md`](../execution/R1-07-e2e-schedule.md).

## Covers

- AC-1 (a PR touching `web-component/src/**` triggers the workflow; `unit` + `e2e` jobs green on the
  PR check run) → **R1-07-02**.
- AC-2 (nightly `schedule` run on the default branch green and visible with artifacts) →
  **R1-07-02** step 4.
- AC-3 (a deliberate widget selector break makes the `e2e` job fail with a Playwright trace
  artifact) → **R1-07-01** steps 4–6.
- AC-4 (total runtime < 15 min) → **R1-07-01** step 3.
- The doc's "Tests" line ("Verification = two green scheduled/dispatched runs") → **R1-07-01**
  steps 1–3 — two green dispatched runs on the same commit, plus `report.md` uploaded from a
  zero-AI run (00-HARNESS §11; cross-ref H-05).

Both scenarios are **manual** tier: they verify CI infrastructure by driving GitHub Actions itself;
they are executed once at rollout (and re-run only when the workflow file changes). Their evidence
is the workflow run history + the rollout report — **Decision:** these meta ids do **not** appear as
rows in per-run `e2e/state/report.md` (nothing local executes); polluting every nightly report with
permanently-SKIP rows would dilute it.

## Preconditions

- `.github/workflows/e2e.yml` merged (two jobs: `unit` → `dotnet test`, `e2e` →
  `cd e2e && npm ci && npx playwright install --with-deps chromium && bash run-e2e.sh --ci`), with
  `pull_request` path filters `API/**, Application/**, Domain/**, Infrastructure/**,
  web-component/**, e2e/**, docker-compose.yaml` (**Decision:** the compose file is
  `docker-compose.yaml` — 00-HARNESS §2 — not R1-07 Design's `docker-compose.yml`, which would
  never trigger).
- `gh` CLI authenticated against the repo.
- Zero-AI enforcement: the workflow never passes `--with-ai` (00-HARNESS §1; grep the yml as
  step 0 below).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-07-01 | two green dispatched runs + deliberate-break trace + zero-AI report | manual | api (meta) | — | 0. `grep -c 'with-ai' .github/workflows/e2e.yml` → 0 (zero-AI rule). 1. On the feature branch: `gh workflow run e2e.yml --ref <branch>`; `gh run watch <id1> --exit-status`. 2. **Sequentially** (never in parallel — the per-ref concurrency group with cancel-in-progress would kill the first run): `gh workflow run e2e.yml --ref <branch>` again; `gh run watch <id2> --exit-status`. 3. `gh run view <id1> --json jobs,jobsRuntime` and `<id2>`: both jobs (`unit`, `e2e`) `conclusion === 'success'`; the `e2e` job runtime < 15 min (AC-4). 4. Zero-AI evidence: `gh run download <id2> -n report` → the downloaded `report.md` is non-empty, contains `## Scenarios` and one row per executed scenario id (H-05's contract, now satisfied by `lib/report.mjs` writing in every phase — 00-HARNESS §11). 5. Deliberate break: branch `ci/break-probe` off `<branch>`; edit `e2e/widget/widget.spec.ts` changing the locator `#pf-add` to `#pf-add-broken` (**Decision:** break the spec's locator, not product code — zero product change, same failure surface as a real selector regression); push. 6. `gh workflow run e2e.yml --ref ci/break-probe`; wait. 7. `gh run download <id3> -n playwright-report` (and/or the `test-results` artifact) → a `trace.zip` exists for the failed test. 8. Cleanup: delete `ci/break-probe`; confirm the next run on `<branch>`/`main` is green. | 0 → 0 matches. 1–2 → both runs green. 3 → jobs green, runtime < 15 min. 4 → report.md present with scenario rows (uploaded unconditionally — `if-no-files-found: ignore` stays as harmless belt-and-braces). 5–6 → `e2e` job `conclusion === 'failure'`, `unit` still green. 7 → trace artifact present. 8 → green. | run URLs `<id1>–<id3>` + artifact names in the R1-07 rollout report (its Report template asks for exactly this); break-probe diff pasted |
| R1-07-02 | PR-path triggers (positive, negative, schedule) | manual | api (meta) | — | 1. Branch touching only `web-component/src/element.ts` (e.g. a comment line) → open a PR. 2. Branch touching only `README.md` → open a PR. 3. Branch touching only `e2e/widget/widget.spec.ts` → open a PR. 4. After merging PR 1 to `main`: **Decision:** don't wait for the 03:00 UTC cron during verification — dispatch once on `main` as the equivalent green gate, then check the next morning that the real `schedule` run on `main` is green with artifacts (`playwright-report`; traces only on failure; `report.md` per 00-HARNESS §11). | 1 → check runs `e2e / unit` and `e2e / e2e` appear on the PR and are green (AC-1). 2 → **no** e2e check run appears (paths filter negative). 3 → triggers (`e2e/**` is in the filter). 4 → dispatched run green; the following scheduled run green with artifacts visible in Actions (AC-2). | run/PR URLs in the rollout report; screenshot of the Actions list showing the scheduled run |

## Spec files

- None under `e2e/` — these scenarios drive GitHub Actions, not the stack. The "spec" is the
  workflow file itself plus this checklist; re-execute manually whenever `.github/workflows/e2e.yml`
  or `run-e2e.sh` flags change.
- Supporting files this doc relies on (owned by R1-07's tasks, not re-specified here):
  `run-e2e.sh --ci` (exports `CI=1`, propagates exit codes), `e2e/playwright.config.ts`
  (`retries: process.env.CI ? 1 : 0`, `trace: 'retain-on-failure'`), artifact upload steps.

## Not covered here

- Correctness of the `unit` job's tests — `Tests/` (the job only runs `dotnet test --configuration
  Release`).
- Flakiness fixing of the underlying suite (R1-07 task 3's iterate-until-green loop) — that is the
  work item itself, not a scenario; H-02 (determinism) is the ongoing guard.
- README badge / `e2e/README.md` CI paragraph — docs, review-level.
- The AI layer (`--with-ai`, TC1–TC5) — manual-only forever, never in CI (00-HARNESS §7); `e2e/ai/`
  stays untouched by the workflow.

## Flake notes

- Dispatch the two green runs **sequentially** — the workflow's per-ref concurrency group
  (`cancel-in-progress: true`) would cancel the first when the second starts on the same ref.
- Runner needs Docker Compose v2 (`ubuntu-latest` has it; R1-07 Design's `docker compose version`
  step is the canary).
- The break-probe must change a **spec** locator; breaking product code would conflate a CI test
  with a product regression and leave `main`-touching branches around longer than needed.
- 25-min job timeout vs the < 15 min AC: the assert is on observed runtime, not the timeout; if a
  green run exceeds 15 min the scenario FAILS with the runtime printed (budget drift is a flake
  signal, per 00-HARNESS §8).
- After R2-00 extends the workflow, re-run R1-07-01 once — new jobs shifting the `e2e` job's
  runtime or artifact set is exactly what this scenario exists to catch.
