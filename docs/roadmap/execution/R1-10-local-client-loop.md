# R1-10 — Fully-local client loop (NEW-4d · Release 1 · 1 day)

## Goal
Change an API endpoint and see it working in the dashboard **without deploying anything**. Today the
only path from a new endpoint to a dashboard that can call it runs through
`.github/workflows/publish-clients.yml`, which generates from **production**
(`POINTER_SWAGGER_URL: https://api.pointer.moamen.work/...`) and publishes to GitHub Packages — so an
endpoint that is not yet deployed cannot be consumed, and the `dashboard-agent` (step 4 of the phase
lifecycle, `01-OVERVIEW.md`) is blocked on a deploy it should not need. This doc closes that loop:
generate against the local API (which already works), build, publish to the **local Verdaccio** the
harness already runs (NEW-4c), and install into the three dashboard apps from there — on a machine
with **no GitHub token at all**.

**There is no second Orval config.** `orval.config.ts` describes only input tag filters and output
shapes; both are identical local and remote. Generation has *always* been local by default —
`scripts/generate-clients.mjs:23-25` falls back to `http://localhost:8090/swagger/v1/swagger.json`.
The gap is **delivery**, and it is a three-line hard-coding, not a missing config (see §Design A).

## Out of scope
- Changing what is generated (tags, client shapes, the axios mutator) — `orval.config.ts` is untouched.
- The published pipeline: `publish-clients.yml` keeps generating from production and publishing to
  GitHub Packages. This adds a parallel local path; it does not replace the release path.
- Dashboard UI work — that is the `dashboard-agent`'s job per phase; this only changes how it *gets*
  the client packages.
- Verdaccio itself — introduced by NEW-4c (`R1-07` task 6, harness §2/§6). This doc consumes it.

## Prerequisites
- **NEW-4c** (Verdaccio in `docker-compose.yaml`, `verdaccio/verdaccio:6` on **4873**, committed
  `e2e/verdaccio/config.yaml`, scratch `.npmrc` with a dummy `_authToken`, `npm_config_userconfig` and
  `npm_config_cache` redirected to a scratch dir — harness §6.1).
- Facts verified in code:
  - `scripts/generate-clients.mjs:23-25` — `POINTER_SWAGGER_URL ?? 'http://localhost:8090/swagger/v1/swagger.json'`.
  - `scripts/generate-clients.mjs` — `CLIENTS_VERSION ?? '1.0.0'` already parameterises the version;
    it writes each `clients/<fw>/package.json` **and** a `clients/<fw>/.npmrc` containing
    `@moamen-ui:registry=https://npm.pkg.github.com`, with `publishConfig.registry` set to the same
    hard-coded host.
  - `scripts/build-clients.mjs:16` — `const REGISTRY = 'https://npm.pkg.github.com';` used at `:206`
    (react/vue `publishConfig`), `:212` (react/vue `.npmrc`), `:231` (angular dist `publishConfig`)
    and `:234` (angular dist `.npmrc`).
  - Publish roots differ per framework: `clients/react`, `clients/vue`, **`clients/angular/dist`**
    (`publish-clients.yml` loop; ng-packagr writes its own `dist/package.json`).
  - `.gitignore:14-15` — **`clients/` and `openapi.json` are gitignored**, so nothing this loop writes
    under `clients/` can ever dirty this repo. The hygiene risk lives entirely in the *dashboard* repo.
  - The three dashboard apps each depend on `@moamen-ui/pointer-{angular,react,vue}@^1.0.31`.

## Design

### A. The blocker: the registry is hard-coded, and `publishConfig` outranks `--registry`
`npm publish --registry http://localhost:4873` **does not work today**, and would fail in a way that
looks like a Verdaccio problem: `publishConfig.registry` in the package being published takes
precedence over the `--registry` flag, and the generated `.npmrc` pins the `@moamen-ui` scope to
GitHub Packages as well. Both are written by our own scripts, so the fix is ours.

**Decision: add a `CLIENTS_REGISTRY` env var**, defaulting to `https://npm.pkg.github.com`, mirroring
the existing `CLIENTS_VERSION` pattern. Every hard-coded occurrence reads it instead. No behaviour
changes when it is unset — `publish-clients.yml` keeps working untouched.

### B. Version strategy
**Decision: `0.0.0-local.<unix-seconds>`**, a unique prerelease per publish.
- Verdaccio refuses to overwrite an existing version, and republishing *different bytes* under the
  same version would poison npm's integrity cache on the consumer — so the version must be unique.
  `--force`/unpublish loops are the alternative and are worse: they leave the consumer's cache holding
  a tarball that no longer exists.
- The `0.0.0-` major guarantees it can **never** satisfy the dashboard's `^1.0.31` range. A local
  client is only ever used when someone asked for it explicitly, and a stray `npm install` in the
  dashboard silently falls back to the published package instead of picking up a local build.
- `clients/` is gitignored (Prerequisites), so the version written into `clients/*/package.json`
  cannot dirty this repo. Nothing needs restoring here.

### C. Consuming it without dirtying the dashboard
**Decision: install with `--no-save`.**

```
npm i @moamen-ui/pointer-angular@0.0.0-local.<ts> --registry http://localhost:4873 --no-save
```

`--no-save` writes `node_modules` and touches **neither `package.json` nor `package-lock.json`** — so
it is structurally impossible to commit a dashboard pinned to a local version. That is the whole
reason for choosing it over a per-app `.npmrc` or a saved dependency: those both leave a diff someone
has to remember to revert, and "remember to revert" is how a local version reaches `main`.

**Back to normal: `npm ci`** in the app directory — restores exactly what the lockfile says, i.e. the
published packages. Note honestly: `npm ci` re-fetches `@moamen-ui/*` from GitHub Packages and so
**does** need `NODE_AUTH_TOKEN`. A developer without a token can still work — they simply stay on the
local clients until they next need the published ones, or `rm -rf node_modules` and re-install when
they have a token. The local *loop* needs no token (§E); only returning to the published packages does.

### D. One command
**Decision: `npm run clients:local`** in this repo's root `package.json` →
`node scripts/publish-clients-local.mjs`, which:
1. computes `VERSION=0.0.0-local.$(date +%s)`;
2. runs `generate-clients` and `build-clients` with `CLIENTS_VERSION=$VERSION` and
   `CLIENTS_REGISTRY=http://localhost:4873`;
3. preflights the registry (`GET http://localhost:4873/-/ping`) and the API
   (`GET http://localhost:8090/swagger/v1/swagger.json`), failing with the exact `just up` /
   `docker compose up -d verdaccio` command rather than a stack trace;
4. publishes `clients/react`, `clients/vue`, `clients/angular/dist` with the scratch `.npmrc`
   (`npm_config_userconfig` redirected — harness §6.1 — so the developer's `~/.npmrc` is never
   written, which `npm publish` would otherwise do when it records auth);
5. prints the three paste-ready install commands and the one-line way back:

```
✅ Published @moamen-ui/pointer-{angular,react,vue}@0.0.0-local.1757164800 → http://localhost:4873

Use them:
  (cd ../pointer-dashboard/angular && npm i @moamen-ui/pointer-angular@0.0.0-local.1757164800 --registry http://localhost:4873 --no-save)
  (cd ../pointer-dashboard/react   && npm i @moamen-ui/pointer-react@0.0.0-local.1757164800   --registry http://localhost:4873 --no-save)
  (cd ../pointer-dashboard/vue     && npm i @moamen-ui/pointer-vue@0.0.0-local.1757164800     --registry http://localhost:4873 --no-save)

Back to published:  (cd ../pointer-dashboard/<app> && npm ci)   # needs NODE_AUTH_TOKEN
```

**Decision: the dashboard side stays script-free.** A printed command is auditable and leaves nothing
behind; a `use-local-clients` script in the dashboard repo would be one more thing to keep in sync
across three apps for no gain over a line you paste.

### E. No GitHub token anywhere in the local path
- Publishing targets Verdaccio, whose `e2e/verdaccio/config.yaml` grants publish rights to the
  `@moamen-ui/*` pattern against the scratch dummy token (NEW-4c).
- Installing passes `--registry http://localhost:4873` explicitly, so the `@moamen-ui` scope resolves
  there rather than at GitHub Packages.
- Transitive/peer deps (`@angular/core`, `axios`, `@tanstack/*`) resolve through Verdaccio's npmjs
  uplink (NEW-4c), so an offline-but-warm cache is enough.
- **Acceptance criterion AC-5 asserts the whole loop with `NODE_AUTH_TOKEN` unset** — that is the
  point, not a nicety.

## Tasks
1. `scripts/generate-clients.mjs` — `const REGISTRY = process.env.CLIENTS_REGISTRY ?? 'https://npm.pkg.github.com';`
   and use it for both `publishConfig.registry` and the written `clients/<fw>/.npmrc`. Log the
   resolved registry next to the resolved version so a mis-set env is visible in the output.
2. `scripts/build-clients.mjs` — same variable, replacing the hard-coded `:16` constant at `:206`,
   `:212`, `:231`, `:234`. Also make the README `INSTALL` block say "the registry this build targets"
   rather than asserting GitHub Packages unconditionally.
3. `scripts/publish-clients-local.mjs` — new, per §D. Preflight, generate, build, publish three dirs,
   print. Non-zero exit on any failure; never leaves a partially-published set silently.
4. Root `package.json` — add `"clients:local": "node scripts/publish-clients-local.mjs"`.
5. `docs/SELF_HOSTING.md` and the root `CLAUDE.md` dashboard blockquote — document the local loop
   alongside the published one (both already describe the corrected pipeline; this adds the local path).
6. `e2e/verdaccio/config.yaml` (owned by R1-07 task 6) — confirm the `@moamen-ui/*` package pattern
   allows publish/unpublish with the dummy token; if NEW-4c scoped it to `pointer-feedback*` only,
   widen it and say so in the report.
7. `.claude/agents/dashboard-agent.md` — replace the "generate from production" limitation with this
   loop (see §Dashboard tasks).

## Dashboard tasks
- **Agent instruction change, not repo work.** `dashboard-agent` now: `npm run clients:local` in
  `pointer-api` → install the printed `--no-save` commands in all three apps → wire the UI → build and
  test each app. It **keeps the existing reporting requirement**: its report must state that the apps
  are running on `0.0.0-local.*` clients, that `package.json`/`package-lock.json` are deliberately
  untouched, and that the phase must be **re-pointed to published packages once the API is deployed**
  (`npm ci` per app, then re-run the builds) before the dashboard work can merge.
- No change to any dashboard `package.json`, lockfile or `.npmrc` — by construction (§C).

## Tests
- Unit (this repo): none — the scripts are exercised end-to-end by the e2e scenarios; a unit test of
  "does npm publish work" would mock away the thing under test.
- E2E: `docs/roadmap/testing/R1-10-tests.md` — `R1-10-01` … `R1-10-05`.

## Acceptance criteria
- [ ] **AC-1** With the API running locally and an endpoint present that does **not** exist in
      production, `npm run clients:local` completes and the generated
      `clients/angular/src/index.ts` exports a symbol for that endpoint.
- [ ] **AC-2** All three packages appear in Verdaccio at `0.0.0-local.<ts>`, and
      `npm view @moamen-ui/pointer-angular versions --registry http://localhost:4873` lists it.
- [ ] **AC-3** After the printed install command, the dashboard app compiles against the new endpoint,
      and `git status --porcelain` in `pointer-dashboard` is **empty** (no `package.json`/lockfile diff).
- [ ] **AC-4** `npm ci` in that app restores the published `^1.0.31` client and the new symbol is gone.
- [ ] **AC-5** The whole loop (AC-1 → AC-3) succeeds with `NODE_AUTH_TOKEN` **unset**.
- [ ] **AC-6** `npm run generate-clients` with no `CLIENTS_REGISTRY` still writes
      `publishConfig.registry: https://npm.pkg.github.com` — the release path is byte-identical.
- [ ] **AC-7** After a local publish, `~/.npmrc` is unchanged (sha256 before/after) and
      `npm config get registry` still reports the developer's default.

## Rollout / compatibility
Additive and default-off: with `CLIENTS_REGISTRY` unset every script behaves exactly as today, so
`publish-clients.yml` is unaffected (AC-6 pins this). Nothing in `clients/` is tracked, so there is no
migration and no dirty-tree risk in this repo. The dashboard repo is never modified. A developer who
never runs `clients:local` sees no change at all.

**The one sharp edge:** an app left on `--no-save` local clients looks normal in `git status` — the
evidence lives only in `node_modules`. That is the deliberate trade (it cannot be committed), and the
mitigations are the agent's mandatory report line (§Dashboard tasks) and `npm ci` being the documented
default before merging.

## Report template
Files changed · the resolved local version · `npm view` output proving all three published · the three
install commands as printed · `git status --porcelain` from the dashboard repo (must be empty) ·
confirmation AC-5 ran with `NODE_AUTH_TOKEN` unset · the `~/.npmrc` sha256 before/after.
