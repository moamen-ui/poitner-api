# R2-01-tests — Apply core library + `pointer apply` / `--plan`

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R2-01-apply-core-cli.md`](../execution/R2-01-apply-core-cli.md).

## Covers

AC-1 (`--plan` prints prompt with verbatim SECURITY + both items, no changes) → R2-01-01 · AC-2 (`--mark` matches `commitStyle`; empty index → exit 1, no PATCH; `commitUrl` github form / null unknown host) → R2-01-02/03/05 · AC-3 (`no-push.test.ts` masking criterion) → unit, see below · AC-4 (`get --json` keys = `AiCommentView` exactly, flags absent) → R2-01-06 · AC-5 (`--tool claude` spawns stub) → unit · AC-6 (`skill.md` diff shape) → unit/review · AC-7 (bare-remote refs identical before/after, pasted) → R2-01-04 · AC-8 (`npm test`, `just test`) → CI, not a scenario.

## Preconditions

- Seed complete. Personas: WA (`TENANT_OWNER` — **its API key comes from `state/keys.json.wsAdmin`**, minted by seed per harness §3; the execution doc's "seeded tenant owner key" does not exist before the harness), QA (`USERS.tester` — comment author), DEV (`USERS.developer` — non-admin fallback, R2-01-07).
- Prerequisites merged: R1-02, R1-04, R2-01. `CLI_ENTRY=node <repo>/cli/dist/cli.js` after `npm run build` in `cli/`.
- **Apply fixture (shared, built in spec setup):**
  1. `tempRepo()` from `lib/git.mjs`: `git init`, `git config user.email e2e@example.com`, `git config user.name E2E`, initial commit (`README.md`).
  2. `bareRemote(repo)` — `git init --bare <tmp>/apply-fixture-bare.git`; `git remote add origin <bare path>` (a file path → unknown host → `commitUrl === null`; R2-01-03 re-points it to a github-shaped URL).
  3. WA creates a dedicated project (keeps `e2e-alpha`'s seeded queue untouched): `POST /api/admin/projects { key: 'e2e-apply-<runId>', name: 'E2E Apply' }` (on 409 → find via `GET /api/admin/projects`); `PATCH /api/admin/projects/{id}` `{ commitStyle: 2 }`.
  4. QA posts 2 comments: `POST /api/projects/{key}/comments { body: 'Make the CTA primary', environment: 1, element: { selector: '.cta', route: '/', pageUrl: 'http://localhost/x' } }` and `{ body: 'Bump footer year to 2026', … }` → WA `PATCH /api/comments/{id}` `{ status: 2 }` for each → `id1`, `id2`.
  5. `<repo>/.pointer/config.json` `{ "server": "http://localhost:8090", "project": "<key>" }`; `<repo>/.pointer/credentials.env` `POINTER_API_KEY=<keys.json.wsAdmin>`; `<repo>/.pointer/stack.json` `{ "frontend": ["react"], "backend": null }`.
  6. `snapBefore = refsSnapshot(bare)` — `git --git-dir=<bare> for-each-ref` output; asserted byte-identical at the end of every scenario.
- **Decision:** in `--json` output `body` and `replies[].body` are emitted as `{ value: <string>, untrusted: true }` (the whitelist key set is unchanged); in human output they sit inside the `UNTRUSTED DATA` fence.

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R2-01-01 | `apply: plan makes no edits` | PR | cli | WA | 1. `gitStatus0 = spawn('git',['status','--porcelain'])`. 2. `spawnCli({ cwd: repo, args: ['apply','--plan'] })`. 3. Re-run `git status --porcelain`; `sha256` of every tracked file unchanged. 4. `assertRefsUnchanged(bare, snapBefore)`. | exit 0; stdout contains `PLAN ONLY`, `#<id1>` and `#<id2>`, the `## ⚠️ SECURITY` heading with the rewritten commit-authority bullet (`git push` `is never permitted`), `AI RULES PRECEDENCE`, `UNTRUSTED DATA`; porcelain byte-identical; refs unchanged | report row; stdout saved to `state/apply-work/plan.txt` |
| R2-01-02 | `apply: separate commits` | PR | cli | WA | 1. WA `PATCH /api/admin/projects/{projectId}` `{ commitStyle: 2 }`. 2. In repo: `printf 'fix1\n' >> src/a.txt; git add -- src/a.txt; printf 'unrelated\n' >> src/b.txt` (b.txt left **unstaged**). 3. `head0 = git rev-parse HEAD`; `spawnCli({ args: ['apply','--mark', String(id1), '--reply', 'Applied CTA fix'] })`. 4. `GET /api/comments/{id1}` (WA token); `GET /api/comments/{id2}`. 5. `git log -1 --pretty=%s`; `git status --porcelain` still lists `src/b.txt` as unstaged-modified. 6. `assertRefsUnchanged`. | exit 0; exactly one new commit (`rev-list --count HEAD` +1) with subject `Apply Pointer comment #<id1> — Make the CTA primary` (product name `Pointer`, first 60 chars of body); id1 `status === 3`, `appliedByLabel === 'e2e@example.com'`, `commitUrl === null` (origin is a local path — unknown host); id2 still `status === 2`; unstaged `src/b.txt` untouched by the commit | report row; `git log -1` + comment JSON |
| R2-01-03 | `apply: single commit` | PR | cli | WA | 1. WA `PATCH /api/admin/projects/{projectId}` `{ commitStyle: 1 }`. 2. `git remote set-url origin https://github.com/e2e/apply-fixture.git`. 3. `printf 'f1\n' >> src/a.txt; printf 'f2\n' >> src/b.txt; git add -- src/a.txt src/b.txt`; `head0 = rev-parse HEAD`. 4. `spawnCli({ args: ['apply','--mark','all','--reply','Applied both'] })`. 5. `GET /api/comments/{id1}` and `{id2}` (WA). 6. `assertRefsUnchanged`. | exit 0; exactly **one** new commit, subject `Apply 2 pending Pointer comments`; both ids `status === 3` and `commitUrl === 'https://github.com/e2e/apply-fixture/commit/' + <new HEAD sha>` (ends with the sha of the commit the CLI made, not `head0`); same URL object-equal across both | report row; both `commitUrl`s pasted |
| R2-01-04 | `apply: never pushes` | PR | cli | WA | 1. Runs **after** R2-01-01…03 and 05–06 in the same repo: `assertRefsUnchanged(bare, snapBefore)`. 2. Also `git --git-dir=<bare> rev-parse --all` → empty output (bare never received any object). 3. Paste both snapshots into the report. | `for-each-ref` output byte-identical before/after the whole file (a local tracking ref like `origin/main` would prove nothing — that is why the **bare** is snapshotted); `rev-parse --all` empty | report row; before/after `for-each-ref` blocks pasted verbatim |
| R2-01-05 | `apply: empty index → exit 1, no PATCH` | PR | cli | WA | 1. `git reset` (empty index; worktree may stay dirty). 2. `spawnCli({ args: ['apply','--mark', String(id2), '--reply','x'] })`. 3. `GET /api/comments/{id2}`. 4. `spawnCli({ args: ['apply','--mark','all','--reply','x'] })`. 5. `assertRefsUnchanged`. | 2 → exit **1**, stderr contains `Nothing staged for #<id2>`, no PATCH (id2 still `status === 2`, `appliedAt` null); 4 → exit 1, `Nothing staged`; commit count unchanged | report row |
| R2-01-06 | `apply: get --json = exact AiCommentView key set` | PR | cli | WA | 1. `spawnCli({ cwd: repo, args: ['get', String(id1), '--json'] })`. 2. Parse stdout JSON; deep-compare sorted top-level keys. 3. Compare `element` keys, `replies[0]` keys, `pickedActions[0]` keys. 4. Deep scan every key at every depth for forbidden names. | exit 0; top-level keys exactly `appliedAt, appliedByLabel, authorName, body, commitUrl, createdAt, element, environment, id, isBugReport, pickedActions, replies, status`; `element` keys exactly `appliedCssRules, classes, deviceType, pageUrl, parentInfo, route, pageTitle, selector, snapshot, sourcePath, viewportHeight, viewportWidth`; `replies[]` `authorName, body, isAi`; `pickedActions[]` `text, prompt`; `body.value`/`body.untrusted === true` (Decision in Preconditions); zero occurrences of `hasPayloadFlag`, `payloadFlags`, `authorId`, `ownerId`, `editedBy` anywhere (R2-06 guarantee holds even though the server returns them to headerless callers as absent) | report row; parsed key list pasted |
| R2-01-07 | `apply: non-admin developer falls back to summary view` | nightly | cli | DEV | 1. Ground the trigger: `GET /api/admin/projects/{key}/apply-queue` with DEV token → 403. 2. Fresh `tempRepo()` with `.pointer/config.json` (same project) and `credentials.env` `POINTER_API_KEY=<keys.json.developer>`. 3. `spawnCli({ args: ['apply'] })` and `spawnCli({ args: ['apply','--plan'] })`. 4. `spawnCli({ args: ['list','--status','ready','--json'] })`. | 1 → 403; 3 → both exit 0; stdout contains the note `Note: predefined-action prompts need an admin key` **exactly once per run**; prompt includes both item bodies (`Make the CTA primary`, `Bump footer year…`) but **no** `Picked actions` prompt text and no `aiRules` prompts; 4 → exit 0, items have summary-view fields only (`id, status, environment, body, createdAt, route, sourcePath, authorName`) | report row; stdout of steps 3–4 |

## Spec files

- `e2e/apply/apply.spec.mjs` — all rows (node `node:test`); uses `lib/api.mjs`, `lib/cli.mjs` (`spawnCli`, `CLI_ENTRY`), `lib/git.mjs` (**new**: `tempRepo()`, `bareRemote(repo)`, `refsSnapshot(bare)`, `assertRefsUnchanged(bare, snap)`), `lib/report.mjs`.
- Setup/teardown: project fixture + comments + `snapBefore` in `before()`; `assertRefsUnchanged` re-asserted in `after()` (R2-01-04).
- No API or widget spec — every assertion rides CLI output + API reads.

## Not covered here

- Unit-level per R2-01 Tests: `queue.test.ts` (fallback, 403), `prompt.test.ts` (golden, fencing, rule ordering), `projection.test.ts` (exact keys incl. flags dropped), `git.test.ts` (`commitUrlFor` matrix — E2E covers only the github + unknown-host cells), `no-push.test.ts` (masked-bundle regex — AC-3's criterion is the masked grep, not E2E), `mark.test.ts` (PATCH bodies), `security-text.test.ts` (heading-scoped drift) — all in `cli/test/`.
- `--tool claude|opencode|cursor|clipboard` spawn behaviour (AC-5) — stub-on-PATH unit test; spawning real AI tools is manual (see R2-02-05).
- `skill.md` diff shape (AC-6) — enforced by the drift test + PR review, not E2E.
- §21 events (`apply_started`, `first_apply`, `apply_failed`) — no listable events endpoint is in R2-01 scope; assert when §32/§21 surfaces a read API (follow-up row).

## Flake notes

- Never run against `e2e-alpha`'s seeded queue (C1/C3/C4/C7 are shared ground truth for other specs) — the dedicated `e2e-apply-<runId>` project keeps commit counts deterministic.
- `appliedByLabel` comes from `git config user.email` (R2-01 design) — `tempRepo()` must set it or the label is `ai-agent`.
- R2-01-03 must run **after** R2-01-02 (same repo, sequential ids) and re-points `origin`; keep file order fixed inside the spec.
- The CLI caches its JWT (R1-02) — each `tempRepo()` has its own `.pointer/`, so no cross-scenario token bleed; never share one repo between the WA and DEV scenarios.
- `git reset` in R2-01-05 leaves the dirty worktree from R2-01-03 — intended (proves only the staged index is committed / empty index fails while dirty).
