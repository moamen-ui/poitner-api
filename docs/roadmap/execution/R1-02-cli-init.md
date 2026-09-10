# R1-02 — `npx pointer-feedback init` (§1 + §6 doc fix + §21 usage events · Release 1 · 1–2 weeks)

## Goal
A developer with a Pointer account runs **one command** in their app repo and ends with: the widget
mounted (Vite or static HTML), the API key stored, the project selected or created, the AI skills
installed for their tool, and a green verification — **without running an AI agent**. Next.js and
monorepos get a clear hand-off to the `pointer-init` AI skill. The server records `installed` and
`first_comment` events so time-to-first-comment becomes measurable.

## Out of scope
- `doctor`, `/api/meta` (R1-04) — but `init` calls `doctor`'s checks at the end, so build the check
  functions in a shared module `cli/src/checks.ts` here and let R1-04 wrap them.
- Device-code login (§2b, held). Pre-filled `--key` on the dashboard (R1-03).
- Deterministic injection for Next.js / App Router, Angular, CRA/Webpack, monorepo workspaces → the
  CLI prints the hand-off message (Design §F) and installs the skill; the skill does the rest.
- `list/apply/mcp` (R2). Replacing `pointer.sh` (the CLI still downloads it for `skill.md`'s benefit).

## Prerequisites
- R1-01 (names frozen; `install.sh` keeps `config.json` committable).
- Facts: `00-API-INVENTORY.md` §1 (auth), §2 (projects, stack), §4 (branding), §8 (`pointer-init.md`
  detection + snippets + env names). Project list/create are plain `[Authorize]`
  (`API/Controllers/Admin/ProjectsController.cs:11-38`). `CreateProjectRequest.Key` must match
  `^[a-z0-9-]+$` (`Application/Validators/CreateProjectValidator.cs:15`). Projects do **not**
  self-register (`IProjectService.EnsureAsync`, `ProjectService`); `pointer-init.md:11-12` is wrong.

## Design

### A. Package layout (`cli/`)
```
cli/
  package.json          name pointer-feedback, bin { pointer: dist/cli.js }, engines node>=18, type module
  tsconfig.json
  build.mjs             esbuild: src/cli.ts → dist/cli.js (single file, platform node, format esm,
                        banner "#!/usr/bin/env node", define DEFAULT_SERVER from env POINTER_DEFAULT_SERVER
                        or "https://api.pointer.moamen.work")
  src/cli.ts            arg parsing (hand-rolled: `pointer <cmd> [--flag value] [--bool]`), dispatch, exit codes
  src/config.ts         DEFAULT_SERVER, read/write .pointer/config.json + credentials.env, gitignore helper
  src/api.ts            api<T>(server, path, {method, body, token}) → unwraps Result<T>, throws ApiError
  src/branding.ts       getBranding(server) → {productName, urls.app} with fallback {productName:"Pointer"}
  src/prompt.ts         ask(question, {default, validate, secret}) and select(question, items) via node:readline/promises
  src/detect.ts         detectStack(cwd) → {kind: 'vite'|'static'|'next'|'angular'|'cra'|'monorepo'|'unknown', evidence: string[], htmlPath?: string}
  src/inject/vite.ts    injectVite(cwd, cfg) ; src/inject/static.ts injectStatic(cwd, htmlPath, cfg)
  src/skills.ts         installSkills(server, aiTool, cwd)
  src/events.ts         postEvent(server, token, {type, projectKey, meta})
  src/checks.ts         the doctor checks (shared with R1-04)
  src/commands/init.ts
  test/                 node:test + tsx; fixtures/ with a mini Vite project, a static index.html, a Next project
  README.md
```
`npm scripts`: `build`, `typecheck` (`tsc --noEmit`), `test` (`node --import tsx --test test/**/*.test.ts`), `prepublishOnly` (typecheck + test + build).

### B. Command surface
```
pointer init [--server <url>] [--key <ptr_…>] [--project <key>] [--create <name>] [--environment local|staging|production]
             [--tool claude-code|cursor|windsurf|opencode|antigravity|other] [--skills-dir <path>]
             [--html <path>] [--no-inject] [--no-skills] [--yes] [--json]
```
- `--yes`: non-interactive; requires `--key` and (`--project` or `--create`); missing → exit 2 with the
  exact missing flag names. Defaults: `--environment local`, `--tool` from env detection (same table as
  `pointer.sh:72-84`), `--server` from `.pointer/config.json` → `POINTER_SERVER` env → `DEFAULT_SERVER`.
- Idempotent: re-running updates `config.json`, replaces the injected block, never duplicates.

### C. Interactive flow (exact texts; `{product}` = `branding.productName`)
```
1  Server URL [https://api.pointer.moamen.work]:                    ← skipped if --server / config.json
   → GET /api/branding (2 s timeout). On failure: "Could not reach {url} — check the URL." exit 1.
   → GET /api/meta if available (R1-04); if data.minCliVersion > own version → exit 5 with upgrade hint.
2  API key (from {product} → profile → API key; input hidden):
   → POST /api/auth/login-with-key {apiKey}. Non-"ok" status or error → "Invalid API key." retry ×3 → exit 3.
   → GET /api/auth/me → greet: "✔ Signed in as {displayName} ({roleName})".
   → write .pointer/credentials.env (mode 0600) + credentials.env.example; update .gitignore (frozen lines).
3  Project:
   → GET /api/admin/projects → select list "Which project is this app?" with entries "{name}  ({key})"
     + last entry "＋ Create a new project…". Empty list → go straight to create.
   Create: "Project name:" → key auto-derived: lowercase, spaces/underscores → '-', strip [^a-z0-9-],
     collapse dashes, trim; shown as "Project key [my-app]:" (editable, validated ^[a-z0-9-]+$).
     → POST /api/admin/projects {key, name}. 409/conflict → "Key already exists, choose another."
4  Environment [local]:   select local | staging | production
5  AI tool [detected: claude-code]:  select claude-code | cursor | windsurf | opencode | antigravity | other
6  Detecting your stack… → prints kind + evidence (e.g. "Vite (vite.config.ts, index.html)")
   vite | static  → inject (D/E); prints the file changed.
   next | angular | cra | monorepo | unknown → hand-off (F); no file changes.
7  Installing AI skills → files written (G).
8  Registering stack → POST /api/projects/{key}/stack {frontend, backend, aiTool} (from detection; see H).
9  Verifying… (checks from cli/src/checks.ts, see R1-04 list) → each "✔"/"✘" line.
10 POST /api/events {type:"installed", projectKey, meta:{stack, aiTool, injected:boolean, cliVersion}}.
11 Summary:
   ✔ {product} is set up for project "{name}" ({key})
     • Widget: injected into index.html (vite)            | • Widget: run the pointer-init skill in {tool} (next)
     • Key: .pointer/credentials.env (gitignored)
     • Skills: .claude/skills/pointer-feedback, .claude/skills/pointer-init
   Next: start your dev server, open the app, click the {product} button and sign in.
         Dashboard: {branding.urls.app}
```
`--json` prints one JSON object with the same facts instead.

### D. Vite injection (`src/inject/vite.ts`)
1. Find `index.html` at `cwd` (or `--html`). Abort with hint if absent.
2. Upsert env lines in `.env` at `cwd` (create if missing; replace existing `VITE_POINTER_*` lines):
   `VITE_POINTER_ENABLED=true`, `VITE_POINTER_SERVER={server}`, `VITE_POINTER_PROJECT={key}`,
   `VITE_POINTER_ENV={environment}`. **Decision:** `.env`, not `.env.local` — values are not secrets and
   teammates need them.
3. Insert before `</body>` (case-insensitive; if missing, append) the block from `pointer-init.md:71-91`
   verbatim, wrapped in `<!-- pointer-feedback:start -->` … `<!-- pointer-feedback:end -->`. If the
   markers already exist, replace the block.
4. If `.env.example`/`.env.sample` exists, upsert the same keys there with `VITE_POINTER_ENABLED=false`
   and empty values (documentation for teammates).

### E. Static injection (`src/inject/static.ts`)
HTML file = `--html` or the single `index.html` in `cwd` (if several candidates → list them and exit 2
asking for `--html`). Insert before `</body>`, marker-wrapped:
```html
<!-- pointer-feedback:start -->
<script src="{server}/pointer.js" defer></script>
<pointer-feedback project="{key}" server="{server}" environment="{environment}" source-attr="data-component-source"></pointer-feedback>
<!-- pointer-feedback:end -->
```

### F. Hand-off message (next | angular | cra | monorepo | unknown)
```
ℹ {kind} detected — automatic injection isn't supported for this stack yet.
  The pointer-init skill was installed for {tool}. Run it and it will mount the widget for you:
    {tool-specific hint, e.g. "claude → /pointer-init" | "cursor → @pointer-init" | "opencode → 'run the pointer-init skill'"}
  Config is already saved in .pointer/config.json, so the skill won't ask for the key or project again.
```
**Decision:** monorepo = `pnpm-workspace.yaml`, `lerna.json`, `nx.json`, `turbo.json`, or `package.json`
`workspaces` present at `cwd` and no `index.html` at `cwd`.

### G. Skills install (`src/skills.ts`)
| tool | destination |
|---|---|
| claude-code | `.claude/skills/pointer-init/SKILL.md`, `.claude/skills/pointer-feedback/SKILL.md` (+ `.agents/` symlinks as `install.sh:29-34`) |
| cursor | `.cursor/rules/pointer-feedback.md`, `.cursor/rules/pointer-init.md` (+ `.agents/`) |
| windsurf | `.windsurf/rules/pointer-feedback.md`, `.windsurf/rules/pointer-init.md` (+ `.agents/`) |
| opencode / antigravity / other | `.agents/pointer-init/SKILL.md`, `.agents/pointer-feedback/SKILL.md` (real files, no symlink) |
Sources: `GET {server}/pointer-init.md`, `GET {server}/skill.md` (already server-filled). Also download
`GET {server}/pointer.sh` → `.pointer/pointer.sh` (chmod 755) — `skill.md` still depends on it until R2.
`--skills-dir` overrides the primary directory.

### H. Stack detection (`src/detect.ts`) — port of `pointer-init.md:32-41`
| kind | evidence (any) |
|---|---|
| vite | `vite.config.{js,ts,mjs,mts}` |
| next | `next.config.{js,mjs,ts}` or `app/` + `package.json` dep `next` |
| angular | `angular.json` |
| cra | `package.json` dep `react-scripts` or `webpack.config.*` |
| monorepo | see F |
| static | `index.html` present and none of the above and no `package.json` bundler dep |
| unknown | otherwise |
Frontend tokens for `/stack`: from `package.json` deps: `react`, `vue`, `svelte`, `solid-js`→`solid`,
`@angular/core`→`angular`, `next`, `tailwindcss`→`tailwind`, `vite`; backend tokens: `*.csproj`→`dotnet`,
`requirements.txt|pyproject.toml`→`python`, `go.mod`→`go`, `package.json` dep `express|fastify|nest`→`node`.

### I. Usage events (server) — §21
- Entity `Domain/Entity/UsageEvent.cs`: `Id`, `OwnerId` (Guid?, strict-own), `ProjectId` (int?),
  `UserId` (Guid?), `Type` (string, max 40), `Source` (string: `cli`|`widget`|`api`), `Meta` (jsonb string,
  max 2000), `CreatedAt`. Index `(OwnerId, ProjectId, Type, CreatedAt)`.
- `POST /api/events` `[Authorize]` — `RecordEventRequest { Type: string, ProjectKey?: string, Meta?: object }`
  → 204. Validator: `Type` ∈ {`installed`, `doctor_run`, `first_apply`, `apply_failed`} for client-sent
  events (others rejected 400; `first_comment` is server-emitted only). Rate-limit policy `events`
  60/min per user (see R1-05 for the per-user partition helper).
- Server emission: in `CommentService.CreateAsync` after save, if the project's non-deleted comment count
  == 1 → `UsageEvent{Type:"first_comment", Source:"api"}`.
- `GET /api/admin/events/summary?projectId=` `[Authorize(Policy="Admin")]` →
  `EventsSummaryResponse { Counts: Dictionary<string,int>, FirstAt: Dictionary<string,DateTime> }`.
- Controller `API/Controllers/EventsController.cs`, service `IUsageEventService` / `UsageEventService`,
  mapping `Infrastructure/Mappings/UsageEventMapping.cs`, migration `AddUsageEvents`.

### J. `/check` page (served by API)
`GET /check?project=<key>&environment=<env>` in `API/Program.cs` next to `/embed.js`: minimal HTML
(`<title>{productName} check</title>`) that loads `/embed.js?project=…&environment=…` and shows the text
"If you can see the {productName} button in the corner, the widget is served correctly. Sign in to test a
comment." `Safe()` sanitising as for `/embed.js`. The CLI prints this URL in the summary; verification
itself is API-level (checks module).

### K. Doc fixes (§6)
- `pointer-init.md:11-12`: replace "Projects **self-register**…" with "Projects are created in the
  dashboard or by `npx pointer-feedback init`; the widget does not create them."
- `pointer-init.md` top: add "**Prefer `npx pointer-feedback init`** — this skill is the fallback for
  stacks the CLI can't inject into (Next.js, Angular, CRA, monorepos)." and a "Step 0 — if
  `.pointer/config.json` exists, read server/project/environment from it and don't ask."
- `install.sh:16`: first echo line "Tip: `npx pointer-feedback init` does all of this interactively."
- `AGENTS.md` / `CLAUDE.md`: add `cli/` to the directory structure and the build commands.

## Tasks
1. Scaffold `cli/` (A); `npm run build` produces a runnable `dist/cli.js` printing help.
2. `src/api.ts` + `src/branding.ts` + `src/config.ts` with unit tests (envelope unwrap, error mapping, config round-trip, gitignore upsert idempotency).
3. `src/prompt.ts` (`ask`, `select`, `secret` input via `readline` with `output` muted).
4. `src/detect.ts` + fixtures (vite, static, next, angular, monorepo) + tests.
5. `src/inject/vite.ts`, `src/inject/static.ts` + tests (fresh insert, re-run replaces block, `.env` upsert, missing `</body>` appends).
6. `src/skills.ts` (+ symlink creation, Windows fallback = copy) + tests with a mocked server.
7. `src/checks.ts` — implement the R1-04 check list (server reachable, meta/version, key valid, project exists & active for env, widget tag present, skills present, gitignore correct); tests.
8. `src/commands/init.ts` — orchestrate C; `--yes`; `--json`; exit codes.
9. Server: I (entity, migration, service, controller, validator, rate-limit policy, server emission) + tests.
10. Server: J `/check` page.
11. Docs: K.
12. `cli/README.md`: install, flags, exit codes, hand-off matrix, contract link.
13. Manual run against `just up` (local API): fresh Vite app → init → open app → widget visible → first comment → `GET /api/admin/events/summary` shows `installed` and `first_comment`.

## Dashboard tasks
- Regenerate services (new `EventsController` endpoints: `RecordEventRequest`, `EventsSummaryResponse`).
- Follow-up tile (not R1): per-project "installed → first comment" durations from `/api/admin/events/summary`.

## Tests
- CLI unit (`cli/test/`): `api.test.ts`, `config.test.ts`, `detect.test.ts`, `inject-vite.test.ts`, `inject-static.test.ts`, `skills.test.ts`, `checks.test.ts`, `init-yes.test.ts` (end-to-end against a stub HTTP server implementing branding/login-with-key/me/projects/stack/events).
- API unit: `Tests/UsageEventServiceTests.cs` (record, tenant isolation, type whitelist, first_comment emitted once), `Tests/CheckPageTests.cs` (sanitising, 200, contains embed.js URL).
- E2E scenario names (implemented in R2-00): `init-vite-no-ai`, `init-static-no-ai`, `init-next-handoff`, `init-yes-ci`.

## Acceptance criteria
- [ ] On a fresh `npm create vite@latest` app, `npx pointer-feedback init` (interactive) completes with no AI tool, the widget renders in the dev server, and a comment can be posted — under 5 minutes wall clock.
- [ ] Same on a folder with a single `index.html`.
- [ ] On a Next.js app: no files changed except `.pointer/`, skills, `.gitignore`; hand-off message printed.
- [ ] `init --yes --key … --create "My App"` in CI creates the project and exits 0; missing `--key` exits 2 naming the flag.
- [ ] Wrong key → exit 3 after 3 attempts (interactive) / immediately (`--yes`).
- [ ] Re-running `init` changes nothing except `cliVersion` in `config.json` (idempotent; `git diff` empty otherwise).
- [ ] Running against a second server whose `/api/branding` returns `productName: "Acme Feedback"` — CLI output contains "Acme Feedback" and no "Pointer" (except in file paths/names from the frozen contract).
- [ ] `first_comment` event exists once for the project after two comments.
- [ ] `just test` and `cli` `npm test` green.

## Rollout / compatibility
- Existing installs keep working: `pointer.sh` and skills unchanged in behaviour; `config.json` is new and optional (`pointer.sh` ignores it).
- `install.sh` remains valid; it now points to the CLI.
- Migration `AddUsageEvents` is additive.

## Report template
Branch · files changed (grouped cli/api/docs) · `npm test` + `just test` summaries · the three manual runs (vite/static/next) with pasted terminal output · dashboard follow-ups.
