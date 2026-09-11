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
- **R1-09** (project URLs belong to an enabled workspace environment) — it changes `CreateProjectRequest`'s shape, which this doc's step 3 and step 4b both use. Land R1-09 first, or land them together; if R1-09 slips, step 4b still works by calling the per-environment URL endpoint right after create.
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
  src/branding.ts       getBranding(server) → {productName, urls.app}; NO literal fallback name — an unreachable /api/branding is a hard exit 1 (white-label rule, 01-OVERVIEW)
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
             [--app-url <url>] [--no-app-url] [--html <path>] [--no-inject] [--no-skills] [--yes] [--json]
```
- `--yes`: non-interactive; requires `--key` and (`--project` or `--create`); missing → exit 2 with the
  exact missing flag names. Defaults: `--environment local`, `--tool` from env detection (table below),
  `--server` from `.pointer/config.json` → `POINTER_SERVER` env → `DEFAULT_SERVER`.
- **AI-tool vocabulary** (CLI `--tool` value = the `aiTool` id registered via `POST /stack` = the
  `config.json.aiTool` value): `claude-code | cursor | windsurf | opencode | antigravity | other`.
  Env detection (port of `pointer.sh:72-84`, which has no opencode rule): `CLAUDECODE`/`CLAUDE_CODE_ENTRYPOINT`
  → `claude-code`; `ANTIGRAVITY_AGENT`/`GEMINI_CLI` → `antigravity`; `TERM_PROGRAM` contains `Cursor` →
  `cursor`; `WINDSURF` → `windsurf`; `OPENCODE` (if such a variable is set by the tool; otherwise no rule) →
  `opencode`; nothing matched → prompt (interactive) or `other` (`--yes`). Today's documented vocabulary is
  `opencode-glm` (`pointer-init.md:311`, `skill.md:216`, `SetProjectStackRequest.cs:13-15` — free text, no
  validator): **Decision:** the CLI registers `opencode`; `skill.md`/`pointer-init.md` vocabulary lists gain
  `opencode`, keep `opencode-glm` as an accepted legacy value, and the dashboard's display-name map shows
  both as "opencode". No server validation is added (field stays honor-system).
- **All printed/documented commands use `npx -y pointer-feedback …`** (the `-y` skips npx's first-run
  "Ok to proceed?" prompt, which would otherwise break copy-paste and CI). Applies to R1-03/R1-04 too.
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
     → POST /api/admin/projects {key, name}. Error handling (`ProjectService.CreateAsync`, `ProjectService.cs:45-71`):
       409/`isConflict` → "Key already exists, choose another." (re-prompt); 403/`isForbidden` (super-admin
       `:45-46`, quick-access `:51-52`) → "This account cannot create projects." exit 3; 400 with
       `isLimitReached` (`:62-71`, plan cap) → print the server `message` + limit, exit 1.
4  Environment [local]:   select local | staging | production
4b App URL for this environment — detected, always confirmable (see §H2):
   "Where does this app run in {environment}? [http://localhost:5173]:"
   → Enter accepts the detected value; anything typed replaces it; an empty line skips
     (the project is created with no URL for that environment and `doctor` reports ⚠).
   → Sent with the project create/update per R1-09's shape (URL + the environment it belongs to).
   → Non-interactive: `--app-url <url>` overrides detection; `--no-app-url` skips.
5  AI tool [detected: claude-code]:  select claude-code | cursor | windsurf | opencode | antigravity | other
6  Detecting your stack… → prints kind + evidence (e.g. "Vite (vite.config.ts, index.html)")
   vite | static  → inject (D/E); prints the file changed.
   next | angular | cra | monorepo | unknown → hand-off (F); no file changes.
7  Installing AI skills → files written (G).
8  Registering stack → POST /api/projects/{key}/stack {frontend, backend, aiTool} (from detection; see H).
   Non-2xx → print "⚠ Stack not registered ({status})" and continue (never fatal). On 2xx write the response
   `data` object verbatim to `.pointer/stack.json` (committable; same file `pointer.sh:99` writes) — R1-04's
   `stack` check reads it.
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
`--json` prints exactly one JSON object and nothing else on stdout (prompts are disabled — implies `--yes`):
```json
{ "ok": true, "product": "<productName>", "server": "...", "project": { "key": "...", "name": "...", "created": false },
  "environment": "local", "appUrl": "http://localhost:5173", "appUrlSource": "vite.config.ts:server.port", "aiTool": "claude-code", "stack": { "kind": "vite", "evidence": ["vite.config.ts"] },
  "injected": true, "routedToSkill": false, "files": [".env", "index.html", ".pointer/config.json", "..."],
  "checks": [ { "id": "server", "status": "ok", "message": "..." } ], "cliVersion": "0.1.0" }
```
On failure: `{ "ok": false, "error": { "code": <exit code>, "message": "..." } }` and the matching exit code.

### D. Vite injection (`src/inject/vite.ts`)
1. Find `index.html` at `cwd` (or `--html`). Abort with hint if absent.
2. Upsert env lines in `.env` at `cwd` (create if missing; replace existing `VITE_POINTER_*` lines):
   `VITE_POINTER_ENABLED=true`, `VITE_POINTER_SERVER={server}`, `VITE_POINTER_PROJECT={key}`,
   `VITE_POINTER_ENV={environment}`. **Decision:** `.env`, not `.env.local` — values are not secrets and
   teammates need them (Vite loads both; `.env` is the shared layer, `.env.local` the per-developer,
   conventionally gitignored override). If the host repo gitignores `.env`, values must be copied per
   developer — the `.env.example` upsert (step 4) documents the keys either way; print a one-line ⚠ when
   `.env` is gitignored.
3. Insert before `</body>` (case-insensitive; if missing, append) the block from `pointer-init.md:73-91` (line 71-72 is prose)
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
`--skills-dir` overrides the primary directory and is recorded as `config.json.skillsDir` so `doctor`/`update` (R2-03) find the files.

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

### H2. Dev-server URL detection (`src/detect.ts` → `detectAppUrl(cwd, kind)`)

Returns `{ url: string | null, source: string }`. **Never guessed silently** — the value is always
shown for confirmation (step 4b), so a wrong guess costs one keystroke, and `source` is printed in
`--json` so a mis-detection is diagnosable.

| kind | port read from (first hit wins) | default |
|---|---|---|
| vite | `server.port` in `vite.config.{js,ts,mjs,mts}` (regex on the source — do **not** execute the config); `--port <n>` in the `dev` script | 5173 |
| next | `-p <n>` / `--port <n>` in the `dev` script; `PORT=` in `.env*` | 3000 |
| angular | `projects.*.architect.serve.options.port` in `angular.json`; `--port <n>` in the `start` script | 4200 |
| cra | `PORT=` in the `start` script or `.env*` | 3000 |
| static | — | `null` (no dev server; prompt starts empty) |
| monorepo / unknown | — | `null` |

Rules: **https only when the config says so** (Vite `server.https`, Angular `serve.options.ssl`),
else `http`. Host is always `localhost` (never `0.0.0.0`/`127.0.0.1` — `0.0.0.0` is not a valid
browser origin and would not match the widget's `Origin` header for R1-05's allow-list). If the
environment chosen in step 4 is **not** `local`, the prompt is still shown but pre-filled empty —
a staging/production URL cannot be detected from the repo, and inventing one would be worse than
asking. Detection is pure file reading: no network, no child processes, no config evaluation.

**Decision:** detection failure is never fatal — `{ url: null }` simply means the prompt starts
empty. A project with no URL for its environment is valid (R1-09); it only costs the extension's
origin lookup and the quick-access invite's landing URL, both of which `doctor` warns about.

### I. Usage events (server) — §21
- Entity `Domain/Entity/UsageEvent.cs`: `Id`, `OwnerId` (Guid?, strict-own), `ProjectId` (int?),
  `UserId` (Guid?), `Type` (string, max 40), `Source` (string: `cli`|`widget`|`api`), `Meta` (jsonb string,
  max 2000), `CreatedAt`. Index `(OwnerId, ProjectId, Type, CreatedAt)`.
- `POST /api/events` `[Authorize]` — `RecordEventRequest { Type: string, ProjectKey?: string, Meta?: object }`
  → 204. The controller resolves `ProjectKey` → `ProjectId` through `IProjectService.EnsureAsync(key)`
  (tenant-scoped; unknown key → 404 `Result.NotFound`). Validator: `Type` ∈ {`installed`, `doctor_run`,
  `apply_started`, `apply_failed`} for client-sent events (others rejected 400; `first_comment` and
  `first_apply` are server-emitted only). `Meta` serialised with `System.Text.Json.JsonSerializer.Serialize`
  (default options) and rejected with 400 when the serialised length > 2000 chars. Rate-limit policy
  `events` 60/min per user — **owned and added by this doc** in `RateLimitingExtensions.cs` (R1-05 owns
  `comments`/`login`, R1-04 owns `meta`; partition helper `UserOrIp` — whichever doc lands first adds it,
  the other reuses it; expect a trivial merge conflict in that file).
- Server emission — **race-safe by constraint, not by count**: partial unique index
  `UX_usage_events_first_per_project ON usage_events (ProjectId, Type) WHERE Type IN ('first_comment','first_apply')`.
  In `CommentService.CreateAsync`, **after the comment's own `SaveChangesAsync`** (`CommentService.cs:177`)
  has completed, attempt to insert `UsageEvent{Type:"first_comment", Source:"api", ProjectId,
  OwnerId = project.OwnerId}` with **its own separate `SaveChangesAsync`**, so a swallowed failure can never
  take the comment down with it. Swallow rule (mandatory, exact): catch `DbUpdateException` **only when**
  `ex.InnerException is PostgresException { SqlState: "23505" }` (unique violation on that index → someone
  else was first) → then `context.Entry(ev).State = EntityState.Detached` (the UnitOfWork shares one
  DbContext; a failed `Added` entry would replay on the next `SaveChangesAsync`, e.g. `CommentService.cs:522`);
  **rethrow everything else** — a genuine DB failure must not be eaten. Same pattern for `first_apply`
  when a comment transitions to `Applied` (`CommentService.UpdateStatusAsync`). No `COUNT(*)` anywhere.
  Null-owner projects (`ProjectService.cs:605-613`) legitimately yield null-owner events, invisible to a
  tenant admin's summary under the strict-own filter — same as their comments today; **do not** "fix" this
  with `IgnoreQueryFilters`.
- `OwnerId` on a `UsageEvent` is stamped from the **project's** `OwnerId` (as `CommentService.CreateAsync`
  does for comments, `CommentService.cs:54-61`), not from the caller — super-admins acting cross-tenant
  must not produce null-owner rows.
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
  dashboard or by `npx -y pointer-feedback init`; the widget does not create them."
- `pointer-init.md` top: add "**Prefer `npx -y pointer-feedback init`** — this skill is the fallback for
  stacks the CLI can't inject into (Next.js, Angular, CRA, monorepos)." and a "Step 0 — if
  `.pointer/config.json` exists, read server/project/environment from it and don't ask."
- `install.sh:16`: first echo line "Tip: `npx -y pointer-feedback init` does all of this interactively."
- `AGENTS.md` / `CLAUDE.md`: add `cli/` to the directory structure and the build commands.

## Tasks
1. Scaffold `cli/` (A); `npm run build` produces a runnable `dist/cli.js` printing help.
2. `src/api.ts` + `src/branding.ts` + `src/config.ts` with unit tests (envelope unwrap, error mapping, config round-trip, gitignore upsert idempotency).
3. `src/prompt.ts` (`ask`, `select`, `secret` input via `readline` with `output` muted).
4. `src/detect.ts` + fixtures (vite, static, next, angular, monorepo) + tests — including `detectAppUrl` (§H2): custom `server.port`, `--port` in the dev script, Angular `angular.json` port, https flag, and the null cases.
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
- API unit: `Tests/UsageEventServiceTests.cs` (record, tenant isolation, type whitelist, `ProjectKey` → `ProjectId` resolution, unknown key → 404) and `Tests/UsageEventFirstCommentTests.cs` — **must use the SQLite provider** (`Microsoft.EntityFrameworkCore.Sqlite`, `Pointer.Tests.csproj:15`), not InMemory: InMemory enforces neither unique nor partial indexes and never throws `DbUpdateException`, so "first_comment emitted exactly once under two concurrent creates" is only provable on SQLite (create the partial index in the test schema). `Tests/CheckPageTests.cs` (sanitising, 200, contains embed.js URL).
- E2E scenarios `init-vite-no-ai`, `init-static-no-ai`, `init-next-handoff`, `init-yes-ci` — defined in `docs/roadmap/testing/R1-02-tests.md` (R2-00 remains the driver/CI-matrix host only).

## Acceptance criteria
- [ ] On a fresh `npm create vite@latest` app, `npx -y pointer-feedback init` (interactive) completes with no AI tool, the widget renders in the dev server, and a comment can be posted — under 5 minutes wall clock.
- [ ] Same on a folder with a single `index.html`.
- [ ] On a Next.js app: no files changed except `.pointer/`, skills, `.gitignore`; hand-off message printed.
- [ ] `init --yes --key … --create "My App"` in CI creates the project and exits 0; missing `--key` exits 2 naming the flag.
- [ ] Wrong key → exit 3 after 3 attempts (interactive) / immediately (`--yes`).
- [ ] On a Vite app with `server.port: 4000` in `vite.config.ts`, step 4b offers `http://localhost:4000`; pressing Enter stores it as the project's URL for the chosen environment, and `--app-url https://x.test` overrides it; `--no-app-url` creates the project with none and `doctor` reports ⚠.
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
