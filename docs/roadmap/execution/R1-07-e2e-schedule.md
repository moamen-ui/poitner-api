# R1-07 — Harness runner + schedule the E2E suite in CI (NEW-4a · Release 1 · 2 days)

## Goal
**Scope note (grew during review):** this doc also builds the two harness components every later
doc and the `e2e-tester-agent` assume exist but nothing owns — the `run-e2e.sh` phase/tier flags and
`lib/report.mjs`. Without them there are no tiers to schedule and no evidence artifact to upload, so
they cannot be deferred past the doc that introduces CI tiers. Estimate raised ½ d → 2 d accordingly.

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
- **`verdaccio` service (nightly only)** — harness §2/§6 level 3. Added to `docker-compose.yaml` as
  `verdaccio/verdaccio:6` on port 4873 with the committed `e2e/verdaccio/config.yaml` (anonymous
  publish for the `pointer-feedback*` pattern, default npmjs uplink for transitive deps) and a named
  volume for its storage so `down -v` wipes it. **Decision: the workflow does not start it on PR
  runs** — the image pull plus two publishes cost ~60–90 s and prove nothing a PR can break, so the
  `e2e` job boots it only under `--nightly` (`docker compose up -d verdaccio` inside the
  `--registry` phase, not in `reset.sh`). It adds **no** `api` recreates: the registry phase runs
  inside the `Cli__MinVersion` window R1-04-04 already opens (harness §8).
- Badge line in `README.md` and a "CI" paragraph in `e2e/README.md`.
- Also add the `dotnet test` job to the same workflow (`Tests/`) if no other workflow runs it — `just test` equivalent: `dotnet test --configuration Release --logger "trx"`. **Decision:** include it; a PR gate without unit tests is incomplete.

## Tasks
0a. **Rewrite `e2e/run-e2e.sh` into a phase runner.** Today it parses exactly one flag (`--with-ai`,
   `:8-11`) and hardcodes five phases (`:13-36`), so a tier cannot be selected and a single phase
   cannot be re-run — which makes harness §8's tiers and the `e2e-tester-agent`'s targeted re-test
   (round 2 of its loop) unimplementable. Implement the flag set harness §4 already specifies:
   `--ci --pr --nightly --fresh --whitelabel --apply --mcp --mail --429 --upgrade --registry --all`,
   plus `--only <scenario-id>[,<id>…]` for the tester's re-runs and `--list` to print the phases a
   tier would run without running them.
   **`--only` is also the flake-retry path** (harness §9), so the runner — not the caller — owns two
   rules: (i) **one retry per scenario per run**, tracked in `state/` so a human re-running by hand
   cannot loop past it, exit 2 with the prior result on a second attempt; (ii) **refuse a solo run of
   a state-coupled scenario** — parse every `docs/roadmap/testing/R*-tests.md` `## State coupling`
   section (harness §12, lines `<id> <- <ids>`) and, when the requested id appears on the left, exit 2
   naming the ids it depends on rather than running it and producing a misleading failure.
   `--only` writes `attempts` into the report row (harness §11). Rules: unknown flag → exit 2 naming it; no flag → today's
   behaviour (reset → seed → probe → widget) so nothing existing breaks; every phase is skippable and
   independently runnable; the phase order and the "429 phase last" rule from harness §8 are enforced
   by the script, not by the caller's memory; `E2E_REUSE=1` skips reset and reuses `state/`.
0b. **`e2e/scripts/lib/report.mjs`** — the evidence writer harness §11 specifies. `record({id, tier,
   layer, role, result, ms, detail})` appends to `e2e/state/report.md`; `phase({name, result,
   duration, notes})` for the phase table; `result` accepts `PASS|FAIL|SKIP|FLAKE|FLAKE-SUSPECTED`
   and `attempts` is its own column (harness §11) — never folded into `detail`; a header with the UTC timestamp, git sha, flags, and the
   stack line (`/api/meta` fields + the mailpit message count). **Every phase writes through it, not
   just the AI phase** — today `scripts/audit.mjs:13` is the only writer and it runs solely under
   `--with-ai` (`run-e2e.sh:31-35`), so a zero-AI run produces no artifact at all, `H-05` fails, and
   the tester agent's hygiene preflight has nothing to read. Also emit `state/junit.xml`. Enforce
   harness §11's rule that `detail` carries no timings, counters or boundary indices (they go to the
   CI log) so `H-02`'s determinism diff is possible.
1. Confirm no existing unit-test workflow (`ls .github/workflows`). Create `.github/workflows/e2e.yml` with two jobs: `unit` (dotnet test) and `e2e` (needs: unit).
2. `run-e2e.sh --ci`; Playwright config CI tweaks.
3. Run the workflow via `workflow_dispatch` on the feature branch; fix flakiness (waits on `/swagger`, fixture server readiness) until two consecutive green runs.
4. README badge + `e2e/README.md` CI section.
5. Add `mailpit` to `docker-compose.yaml` (the compose file this workflow boots) per harness §2 — `axllent/mailpit:latest`, HTTP 8025 / SMTP 1025 container-internal, `Email__*` SMTP env on `api` — so the CI stack matches the harness.
6. Add `verdaccio` to `docker-compose.yaml` (`verdaccio/verdaccio:6`, port 4873, named storage volume) plus the committed `e2e/verdaccio/config.yaml`; wire `run-e2e.sh --registry` to start it, run `e2e/cli/registry.spec.mjs` (R1-04-06) and stop it. Not started on PR runs. Cache the npm/npx download in the nightly job (`actions/setup-node` cache) so the phase stays under ~90 s.

## Dashboard tasks
None.

## Docs
**None — internal only.** CI scheduling and the harness runner are contributor concerns; they belong in `e2e/README.md`, which this doc already updates. Nothing here changes what a user does.

## Tests
This doc *is* test infrastructure. Verification = two green scheduled/dispatched runs.

## Acceptance criteria
- [ ] `bash run-e2e.sh --list --nightly` prints the phase list without running anything; an unknown
      flag exits 2 naming it; a bare `bash run-e2e.sh` behaves exactly as before this change.
- [ ] `bash run-e2e.sh --only R1-05-03` runs that scenario and nothing else (the `e2e-tester-agent`'s
      round-2 contract), and its report row carries `attempts: 1`.
- [ ] A second `--only` of the same scenario in the same run exits 2 with the prior result (the
      one-retry cap is enforced by the runner, not by the caller's discipline).
- [ ] `--only` on a scenario listed on the left of a `## State coupling` line exits 2 naming the ids
      it depends on, and never runs it.
- [ ] A zero-AI run leaves a non-empty `e2e/state/report.md` containing the header, a `## Phases`
      table with a row per phase (`SKIP` + reason for phases the tier skipped) and a `## Scenarios`
      row per executed id, plus `state/junit.xml`.
- [ ] Two consecutive `--pr` runs from a wiped DB produce reports identical after stripping the `ms`
      and `detail` columns (`H-02`).
- [ ] A PR touching `web-component/src/**` triggers the workflow and both jobs (`unit`, `e2e`) are green on the PR check run.
- [ ] After merge, the nightly `schedule` run on the default branch is green and visible in Actions with artifacts (`playwright-report`, traces on failure, `report.md`).
- [ ] Introducing a deliberate widget selector break in a test branch makes the `e2e` job fail with a Playwright trace artifact.
- [ ] Total runtime < 15 min for the PR run (the nightly run, which adds the `--registry` phase and the
      other nightly-only work, is budgeted separately at ≤ 45 min — harness §8).
- [ ] The nightly run starts `verdaccio`, R1-04-06 is green, and the PR run never starts it.

## Rollout / compatibility
CI only. Requires Docker on the runner (present on `ubuntu-latest`). If the repo becomes private/limited on Actions minutes, keep `schedule` and drop `pull_request` for `web-component` paths only.

## Report template
Workflow file · links to the two green runs · runtime · any flake fixes made.
