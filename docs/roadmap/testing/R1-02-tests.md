# R1-02-tests — `npx pointer-feedback init` (CLI init · usage events · `/check`)

Harness: [`00-HARNESS.md`](00-HARNESS.md). Execution doc: [`../execution/R1-02-cli-init.md`](../execution/R1-02-cli-init.md).

## Covers

- AC-1 (fresh Vite app → init → widget renders → comment posted, ≤ 5 min) → **R1-02-01** · AC-2
  (single-`index.html` folder) → **R1-02-02** · AC-3 (Next.js: hand-off message, no edits outside
  `.pointer/`/skills/`.gitignore`) → **R1-02-03** · AC-4 (`--yes` creates project exit 0; missing
  `--key` exit 2) + AC-5 (wrong key exit 3 under `--yes`) + AC-6 (idempotent re-run, `git diff`
  clean except `cliVersion`) → **R1-02-04** · AC-7 (`installed` + `first_comment` events, exactly
  once after two comments) → **R1-02-05** · `/check` page (Design §J) → **R1-02-06**.
- AC-8 (`just test` + `cli npm test` green) is unit-level. The "Acme Feedback" branding AC is
  R2-00's white-label job — see Not covered here.

## Preconditions

- Seed complete (`state/credentials.json`, `state/keys.json`); personas `developer` (DEV),
  `tester` (QA), `wsAdmin` (WA), `tenantBOwner` (TB). `e2e-alpha` active for Local.
- CLI built: `cd cli && npm run build`; scenarios invoke it via `spawnCli` (`lib/cli.mjs`,
  `CLI_ENTRY` per harness §6) — never `npx`, never `npm link`.
- Fresh-app driver `e2e/fresh-app/run.mjs` + `e2e/fresh-app/fresh.spec.ts` +
  `e2e/fresh-app/templates/static/index.html` + `e2e/scripts/serve-dir.mjs` (R2-00 tasks 1–3b).
  Generators pinned to a minor: `npm create vite@6.0 -- --template vanilla-ts`,
  `npx create-next-app@15.0 --ts --app --no-eslint --use-npm --yes`.
- Fixture sources for R1-02-04: `cli/test/fixtures/{vite,static,next}` (R1-02 task 4) — copied into
  a fresh temp repo (`lib/git.mjs tempRepo()`: `git init`, `git config user.email e2e@example.com`).

## Scenarios

| id | intent | tier | layer | role | steps | expected | evidence |
|---|---|---|---|---|---|---|---|
| R1-02-01 | `init-vite-no-ai` | PR (paths `cli/**`) — **the single slowest PR scenario** (≈2–4 min: `npm create` + install + build). It is the funnel proof, so it stays in PR; if the PR tier ever exceeds the 15-min budget (harness §8) this is the **first** scenario to move to nightly, before anything else is cut | cli + widget | developer | 1. `node e2e/fresh-app/run.mjs --stack vite`: wipe `e2e/state/fresh/vite/`, scaffold with the pinned generator. 2. Read `state/keys.json → developer.apiKey` (K). 3. `spawnCli({ cwd: <app>, args: ['init','--server','http://localhost:8090','--key',K,'--create','Fresh vite <runId>','--environment','local','--tool','other','--yes','--json'] })`. 4. Assert files in `<app>`: `index.html` contains exactly **one** `<!-- pointer-feedback:start -->`…`<!-- pointer-feedback:end -->` pair wrapping the env-guarded snippet (`'%VITE_POINTER_ENABLED%' === 'true'`, `document.createElement('pointer-feedback')`, `source-attr`/`data-component-source` — `pointer-init.md:73-91`); `.env` has exactly one of each `VITE_POINTER_ENABLED=true`, `VITE_POINTER_SERVER=http://localhost:8090`, `VITE_POINTER_PROJECT=<key>`, `VITE_POINTER_ENV=local`; `.pointer/credentials.env` exists with mode `0600` and `POINTER_API_KEY=<K>`; `.pointer/pointer.sh` mode `755`; `.agents/pointer-init/SKILL.md` + `.agents/pointer-feedback/SKILL.md` exist (tool `other`); `.gitignore` has the frozen block incl. `!.pointer/config.json`, `!.pointer/stack.json`; `.pointer/stack.json` exists (stack registered). 5. `npm install && npm run build` in `<app>`, then `npx vite preview --port 4174 --strictPort`. 6. `FRESH_URL=http://localhost:4174 npx playwright test e2e/fresh-app/fresh.spec.ts`: `page.locator('pointer-feedback')` attached with a shadow root; unauthenticated comment flow opens the login modal — fill `#pf-email`, `#pf-password` (`dev@example.com` / `DevPass1!`), click `#pf-login-submit` (`templates.ts:26-33`); **Decision:** if the modal appears at widget boot instead of on submit, fill it there — the selectors are the same either way; then `#pf-add` → click `<h1>` (pick) → fill `#pf-comment-text` `E2E fresh comment` → `#pf-submit`. 7. API as developer: `GET /api/projects/<key>/comments?view=summary` → exactly 1 item, `body === 'E2E fresh comment'`. 8. Budget: wall clock step 3 → step 7 ≤ 300 s; one automatic whole-stack retry on budget failure only (R2-00 §7). | 3 → exit 0; JSON `ok === true`, `project.created === true`, `project.key` matches `/^fresh-vite-/`, `injected === true`, `routedToSkill === false`, `files` ⊇ {`.env`, `index.html`, `.pointer/config.json`, `.pointer/credentials.env`, `.gitignore`}, `product === 'Pointer'`, `cliVersion` non-empty. 5–7 as listed; exit code 0 overall. | report row; per-attempt seconds in `detail`; `<app>` zipped as artifact on failure |
| R1-02-02 | `init-static-no-ai` | PR (paths `cli/**`) | cli + widget | developer | 1. `run.mjs --stack static`: wipe `e2e/state/fresh/static/`, copy `e2e/fresh-app/templates/static/index.html` in. 2. Step 2–3 of R1-02-01 with `--create 'Fresh static <runId>'`. 3. Assert `index.html` has exactly one marker pair wrapping exactly (`R1-02 §E`): `<script src="http://localhost:8090/pointer.js" defer></script>` and `<pointer-feedback project="<key>" server="http://localhost:8090" environment="local" source-attr="data-component-source"></pointer-feedback>`; **Decision:** assert no `.env` file was created (static injection writes none). 4. `node e2e/scripts/serve-dir.mjs e2e/state/fresh/static 4174` + `fresh.spec.ts` (same login→comment flow as R1-02-01 step 6). 5. API assert as R1-02-01 step 7 (`/^fresh-static-/`). 6. Budget ≤ 300 s, one retry. | 2 → exit 0; JSON as R1-02-01 with `stack.kind === 'static'`; steps 3–5 as listed. | report row; per-attempt seconds |
| R1-02-03 | `init-next-handoff` | nightly | cli | developer | 1. `run.mjs --stack next`: scaffold `create-next-app@15.0` into `e2e/state/fresh/next/fresh-next`; **Decision:** create-next-app has no `--skip-git` — after scaffold `rm -rf .git`, then `git init && git config user.email e2e@example.com && git add -A && git commit -m base` (harness §6) so the no-edits assertion is exact. 2. `spawnCli init … --create 'Fresh next <runId>' --environment local --tool other --yes --json` (server/key as R1-02-01). 3. `git -C <app> status --porcelain` — every line starts with `.pointer/`, `.agents/`, or is exactly `.gitignore`; nothing under `app/`, `package.json`, `next.config.*`. 4. `spawnCli init …` again without `--json` (same flags) → stdout contains `ℹ next detected — automatic injection isn't supported for this stack yet.` and `pointer-init`. | 2 → exit 0; `ok === true`, `injected === false`, `routedToSkill === true`, `stack.kind === 'next'`. 3 → no other paths. 4 → exit 0; message as listed (AC-3). | report row; `git status --porcelain` output in `detail` |
| R1-02-04 | `init-yes-ci` | PR | cli | developer | In `e2e/cli/init.spec.mjs`, each sub-case a fresh `tempRepo()` copy of `cli/test/fixtures/vite` (**Decision:** copy the fixture — detection is file-based, no npm scaffold needed): 1. `spawnCli init --server http://localhost:8090 --key <K> --create 'My App' --environment local --tool other --yes --json` → exit 0; `project.key === 'my-app'` (derived), `project.created === true`. 2. Missing key: `spawnCli init --yes --create 'My App'` → exit 2; stderr names the missing flag exactly (`--key`). 3. Bad key: `… --key ptr_<40 zeros> --project e2e-alpha --yes` → exit 3 immediately, stderr matches `/Invalid API key/` (no retry loop under `--yes`). 4. Idempotent: re-run step 1 in the same repo → exit 0; `git diff --stat` lists only `.pointer/config.json`; `git diff --unified=0 -- .pointer/config.json` changed lines all match `/cliVersion/`. 5. Conflict under `--yes`: run step 1 again → **Decision:** 409 cannot re-prompt non-interactively → exit 3, stderr contains `Key already exists`. 6. Super-admin negative (only if `keys.json.superAdmin.apiKey` is non-null, else record sub-SKIP): `init --key <saKey> --create 'SA Proj' --yes` → exit 3, stderr contains `This account cannot create projects.` | exit codes 0/2/3/3/3 as listed; step 4 `git diff` otherwise empty (AC-6). | report row per sub-case (single scenario id) |
| R1-02-05 | `installed` + `first_comment` events exactly once | PR | api | wsAdmin (reads) · tester (writes) | 1. WA: `POST /api/auth/login {email:'e2e-owner@example.com',…}` → `POST /api/admin/projects {key:'e2e-events', name:'E2E Events'}` → 200, note `id`. 2. QA: `POST /api/events {type:'installed', projectKey:'e2e-events', meta:{stack:{kind:'static'}, aiTool:'other', injected:true, cliVersion:'0.1.0'}}` → **204**. 3. QA: two comments `POST /api/projects/e2e-events/comments {body:'events c<i>', environment:1, element:{selector:'#x', route:'/'}}` → 200, 200. 4. WA: `GET /api/admin/events/summary?projectId=<id>` → `counts.installed === 1`, `counts.first_comment === 1`, `firstAt.first_comment` is a parseable date ≥ step 3's first comment. 5. QA: a **third** comment → 200; WA re-reads summary → `counts.first_comment === 1` still (partial-unique-index race safety). 6. Negatives (QA): `POST /api/events {type:'first_comment', projectKey:'e2e-events'}` → 400 (client whitelist); `{type:'installed', projectKey:'nope'}` → 404; `{type:'installed', projectKey:'e2e-events', meta:{blob:'x'.repeat(2001)}}` → 400. 7. Cross-tenant: TB: `GET /api/admin/events/summary?projectId=<id>` → **Decision:** 200 with every count 0 / no `first_comment` key (strict-own filter yields zero rows for a foreign tenant's project). | statuses as listed; AC-7 (`first_comment` exists **once** after two comments) proven by steps 4–5. | report row; summary JSON in `detail` |
| R1-02-06 | `/check` page renders productName | PR | api | — | 1. `GET /check?project=e2e-alpha&environment=local` (anonymous). 2. `GET /check?project=%3Cscript%3Ealert(1)%3C/script%3E`. | 1 → 200 `text/html`; body contains `<title>Pointer check</title>` (productName from branding) and the sentence `If you can see the Pointer button in the corner, the widget is served correctly. Sign in to test a comment.`; references `/embed.js` with `project=e2e-alpha` and `environment=local`. 2 → 200; body does **not** contain the raw `<script>alert(1)` (Safe() sanitised). | report row |

## Spec files

- `e2e/fresh-app/run.mjs` + `e2e/fresh-app/fresh.spec.ts` — R1-02-01/02/03 (R2-00 tasks 1–3 create
  the driver; this suite pins the R1-02 assertions above into it).
- `e2e/cli/init.spec.mjs` — R1-02-04 (needs `lib/cli.mjs` `spawnCli`, `lib/git.mjs` `tempRepo`).
- `e2e/api/events.spec.mjs` — R1-02-05 (`lib/api.mjs`, `lib/report.mjs`).
- `e2e/api/check-page.spec.mjs` — R1-02-06 (uses the new `getRaw` helper from R1-01-tests).

## Not covered here

- Interactive 3-attempt key retry, prompt defaults, `--json` failure envelope, exit-2 flag listing —
  `cli/test/init-yes.test.ts` against its stub HTTP server (a TTY prompt loop is unit territory).
- Injection edge cases (missing `</body>` appends, `.env.example` upsert, marker replacement) and the
  detection matrix — `cli/test/inject-*.test.ts`, `cli/test/detect.test.ts`.
- Server unit: event whitelist, tenant isolation, `ProjectKey`→`ProjectId` 404, and the concurrent
  `first_comment` uniqueness proof — `Tests/UsageEventServiceTests.cs` /
  `Tests/UsageEventFirstCommentTests.cs` (SQLite, per R1-02 Tests).
- `/check` sanitising internals — `Tests/CheckPageTests.cs`.
- The "Acme Feedback" white-label AC (second branded server, no `Pointer` leak) — R2-00's
  `whitelabel` job (`R2-00-05`/`R2-00-06`); needs a branding mutation this doc must not do.
- `npm pack` packaging (`bin`, `exports`, shebang) — nightly packaging job (harness §6).

## Flake notes

- Fresh-app scenarios run in CI only on the `cli/**` path filter (npm scaffolding is slow); PR tier
  otherwise stays ≤ 15 min. Pinned **minor** generator versions; one whole-stack retry only on the
  300 s budget; every attempt's seconds printed into `detail`.
- `run.mjs` kills the preview/`serve-dir` server via the `run-e2e.sh` trap pattern; `--strictPort`
  on 4174.
- R1-02-04 creates project `my-app` on the shared stack — safe because every orchestrated run starts
  from reset+seed; a re-run against a reused stack hits the step-5 conflict path, which is itself
  asserted.
- R1-02-05 posts ≤ 3 comments as tester — far under the 30/min/user `comments` policy (R1-05); events
  calls ≤ 8, under 60/min/user.
- `installed` is posted by **every** `init` run (no uniqueness on it — only `first_comment`/
  `first_apply` are unique); never assert `counts.installed === 1` after a re-run.
