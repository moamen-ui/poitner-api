You are authoring Playwright E2E specs for the Pointer product. Work ONLY inside /Users/momen/Desktop/REPOS/pointer-spec-r3-01 (a git worktree of pointer-api). Do not touch any other directory.

READ FIRST, in full, in this order:
1. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/docs/roadmap/execution/IMPLEMENTER-BRIEF.md — binding conventions.
2. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/docs/roadmap/testing/00-HARNESS.md — the harness contract: personas, seeded projects, ports, report helpers.
3. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/docs/roadmap/testing/R3-01-tests.md — YOUR CONTRACT. Its "## Scenarios" table is the spec: every row is one test, with exact steps, exact expected statuses, and exact assertions. Its "## Spec files" section names exactly which files to create.
4. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e/scripts/lib/api.mjs — the HTTP helper you MUST use. Exports: BASE_URL, ApiError, get, post, patch, put, del, login(email,password), and raw(method, path, {token, body, headers}) which returns {status, ok, headers, body, isSuccess, data} WITHOUT throwing.
5. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e/widget/lib/pre-auth.ts — preAuthWidget / waitForWidgetReady / pickElement, for any widget-layer scenario.
6. Two or three existing specs nearest your layer, for style and comment density — `ls e2e/*/*.spec.*` (39 of them now).
   For an API scenario read e2e/api/secrets-flag.spec.mjs; for a CLI one e2e/cli/skill-stamp.spec.mjs; for a widget one e2e/widget/widget.spec.ts.
7. /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e/scripts/lib/state.mjs — read seeded state through THESE helpers, never by reading e2e/state/*.json directly.
   credentials(), keys() and expected() are lazy on purpose: a module-scope readFileSync throws during Playwright's DISCOVERY pass, which turns an
   unseeded workspace into a silently EMPTY suite instead of an error. Use loginClient(api) for the quick-access client — it is PASSWORDLESS, so
   password login refuses it outright.

YOUR TASK: create exactly the files listed in R3-01-tests.md's "## Spec files" section, implementing every scenario id in its "## Scenarios" table. One Playwright test per scenario row. Each test's title MUST begin with the scenario id exactly as written (e.g. "R1-05-01 — origin-enforced-blocks-foreign-origin"), because run-e2e.sh dispatches single scenarios with 'npx playwright test -g <id>'.

RULES:
- Use raw() for every assertion about a status code. The origin/permission/rate-limit matrices are status matrices: 'expect(res.status).toBe(403)'. Never wrap an expected failure in try/catch — a catch block that swallows the wrong error passes silently, which is the exact failure this whole suite exists to prevent.
- Assert HTTP status and structured fields ONLY. Never assert on message text: MessageKeys are English literals, not localisation keys.
- Any state a scenario creates it must restore in a finally block. The suite runs phases in sequence against one shared database; a leftover toggle breaks every later scenario.
- Respect the tier column: scenarios marked 'nightly' go in the file the doc assigns them to and must not run in the PR tier.
- If the contract names a fixture, seed addition, or helper that does not exist yet, CREATE it as the doc specifies — but only within e2e/.
- Never modify anything under docs/roadmap/** — the contract is not yours to edit.
- Never run 'git push', 'git merge', or 'git rebase'. You may commit on the current branch.
- SOME FILES YOUR CONTRACT NAMES ALREADY EXIST AND ARE IN USE BY PASSING SCENARIOS:
    e2e/fixture-app/vite-react/
    e2e/widget/lib/auth.ts
    e2e/widget/widget.spec.ts
  EXTEND them — add your tests, import the helpers that are already there. Do NOT rewrite
  or truncate one. Another release's green scenarios live in these files, and replacing a
  file wholesale deletes them; the suite would still go green because the deleted tests
  simply stop being collected. Run `git diff --stat` before you finish: a file you were
  meant to extend must show insertions, and should show no deletions.


BUILT AND WORKING ALREADY — you are writing TESTS, not features:
- The Vite source-stamp plugin and local manifest ship in cli/src/vite/ (commit 0b15f1b).
- The fixture app e2e/fixture-app/vite-react/ exists and is committed.
- Helpers you will need already exist: e2e/scripts/lib/init-args.mjs (initArgs/INIT_ENV — note
  --json is OPT-IN via `extra`, because the JSON envelope replaces init's human output),
  e2e/scripts/lib/vite-fixture.mjs (copyFixture), e2e/scripts/lib/cli.mjs (spawnCli),
  e2e/scripts/lib/git.mjs (tempRepo). READ e2e/cli/design-tokens.spec.mjs first — it is the
  closest existing spec to what you are writing and shows the exact house style.

TWO RULES THAT HAVE BITTEN EVERY PREVIOUS RUN OF THIS TASK:
1. Give any test that restarts the API or scaffolds an app an explicit `test.setTimeout(...)`.
   Playwright's default is 30s. A test without one that needs longer does not report "too slow";
   it reports a bare "Test timeout of 30000ms exceeded" that looks like a product hang. Two
   scenarios in this repo were unpassable for exactly this reason.
2. Never assert only that something is absent from stdout, or wrap an expected failure in
   try/catch. Assert the positive fact. A test that cannot fail is worse than no test.

CRITICAL — DO NOT RUN THE TEST SUITE. The API and PostgreSQL on :8090 are a single shared instance and other jobs are using them concurrently. Running the suite, the seed script, or reset.sh would corrupt their state. Verify your work WITHOUT executing tests:
- 'cd /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e && node --check <file>' for every .mjs file you write.
- 'cd /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e && npx tsc --noEmit' if you write .ts files.
- 'cd /Users/momen/Desktop/REPOS/pointer-spec-r3-01/e2e && npx playwright test <yourfile> --list' — this PARSES and lists tests without running them. Every scenario id must appear in the listing.
A reviewer will run them for real against a clean database.

WHEN THE SPEC AND THE CODE DISAGREE: stop and report it as 'SPEC-CONFLICT: <evidence>'. Do not silently guess at intent. The contract was written against the code, so a disagreement is real information.

FINAL REPORT (your last message), exactly these sections:
1. Files created — one line each
2. Scenario ids implemented — the full list, and any you could not
3. Verification output — paste the actual output of node --check / tsc / playwright --list
4. Decisions — every place the contract was silent and you chose
5. SPEC-CONFLICT — contract vs code disagreements, with file:line evidence
6. NEEDS_REVIEW — anything you are unsure about
