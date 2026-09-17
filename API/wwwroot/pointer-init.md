---
name: pointer-init
description: Use when the user wants to add, install, init, or integrate the <POINTER_PRODUCT> feedback widget (<pointer-feedback>) into an app — e.g. "add <POINTER_PRODUCT> to this app", "set up <POINTER_PRODUCT> feedback", "integrate the feedback widget". Asks the user for the variables (project key, <POINTER_PRODUCT> server URL), detects the host stack (Vite/Angular/Next/static), injects the loader, wires the env, and verifies. No build step required.
---
<!-- pointer-skill-version: <POINTER_SKILL_VERSION> -->

# Add <POINTER_PRODUCT> to this app

<POINTER_PRODUCT> is an element-level feedback widget delivered as a single Web Component,
`<pointer-feedback>`, loaded from a <POINTER_PRODUCT> server's `/pointer.js`. It renders entirely inside a
Shadow DOM (no CSS collisions), shows a small toolbar, and lets authenticated stakeholders click any
element and leave a comment. Projects **self-register**: the first time an app loads/comments with a
given project key, it appears in the <POINTER_PRODUCT> dashboard.

This skill wires the widget into the **current** app. Take the variables from `.pointer/config.json` when it exists (Step 0); ask the user only for what is missing, and never guess.

> **Server URL** = the deployed <POINTER_PRODUCT> origin (the one you fetched this skill from). When this skill
> is served by a running <POINTER_PRODUCT> server, the examples below are **auto-filled** with that URL; if you
> see a literal `<POINTER_SERVER>` placeholder, replace it with your deployed <POINTER_PRODUCT> URL.
> `http://localhost:8090` is only the local-dev default — **never ship `localhost` to production.**

## Step 0 — Read `.pointer/config.json` FIRST

If `.pointer/config.json` exists, the CLI has already run and every variable below is settled.
**Use those values and do not ask the user again.** Being asked to re-enter a project key you just
typed into `pointer init` makes the tool look broken, and a second answer that disagrees with the
config silently splits the install in two.

```json
{
  "server": "https://pointer.example.com",
  "project": "my-app",
  "htmlPath": "apps/web/src/index.html",
  "delivery": "embed"
}
```

| Field | Use it for |
|---|---|
| `server` | the Pointer origin — never prompt for this when it is set |
| `project` | the project key — never prompt for this when it is set (single-project repos only — see `projects` below) |
| `environment` / `environments` | **legacy — no longer written by `pointer init`.** Environments and their per-environment activation live in the dashboard now, next to the project's URLs; the widget resolves its environment from the page's own origin at runtime (and a signed-in reviewer can switch it from the toolbar), so **never ask which environment(s) this app runs in.** If one of these fields is present anyway (a config from an older install), ignore it for asking — it does not change what you inject either way; see Step 3's origin-mapping note |
| `htmlPath` | where a previous run mounted the widget — edit that same file rather than choosing a new one |
| `delivery` | how reviewers open the widget. `embed` (or absent, from an older install) ⇒ proceed with Step 3 as below. `extension` ⇒ the reviewer's browser extension injects the widget — do **NOT** inject anything; **skip Step 3** entirely and go straight to Step 4 |
| `projects` | **a monorepo with more than one Pointer project.** When present, there is no single `project`/`htmlPath` — each key is one app's own `{ path, htmlPath, delivery }` (an older install's entry may also carry the legacy `environment`/`environments` above). See **Monorepo (Nx) install** below; every rule above still applies, just per app instead of once for the whole repo |

Ask the user **only** for a field that is genuinely missing. If there is no config file at all, fall
back to Step 1.

**Join, not first install.** When `.pointer/config.json` already has BOTH `server` and `project`
set, someone already wired this app up — this run is a **join**, not a first install (this is
exactly what `npx pointer-feedback init` itself does in the same situation: see its `--yes`/`--json`
`mode: 'join'` output). Concretely:

- Ask the user for **nothing except the API key** (Step 4). Server, project, AI tool and delivery
  are all read from the config above; environments are a dashboard concern and were never part of
  it to begin with.
- **Skip Step 3 (inject the loader) entirely.** The `<pointer-feedback>` snippet (or, for
  `delivery: "extension"`, nothing at all) is already in the app's **committed** source — that is
  the whole point of `config.json` being committed. Re-injecting would at best duplicate the
  snippet and at worst stamp a different project key over the one the team already committed.
- **Do still make sure the skills and `.pointer/pointer.sh` are installed on THIS machine.** Unlike
  `config.json`/`stack.json`, the skill files and `pointer.sh` are gitignored (see the table in
  Step 4) — a fresh clone has neither, even though `config.json` is right there in git. Run
  `npx pointer-feedback update` (installs them if missing, refreshes them if stale), or re-run
  `install.sh`, or fetch `pointer-init.md`/`skill.md`/`pointer.sh` yourself the way Step 4 describes.
- If `aiTool` is missing from the config (an older install, from before that field existed), fall
  back to detecting/asking for it as in a first install — everything else above still applies.

Only fall back to the full Step 1 → Step 3 flow below when `config.json` is missing entirely, or is
missing `server` or `project`.

## Step 1 — Ask the user for the variables (only those Step 0 did not answer)

Ask this question first, before the table below, unless Step 0 already answered it from
`.pointer/config.json`'s `delivery` field:

> **How will reviewers open the feedback widget?**
> 1. Embed it in this app (recommended — works for every reviewer, no install) — **default**
> 2. Chrome extension only (no code changes; each reviewer installs the extension)

If the user picks option 2, treat `delivery` as `extension` for the rest of this run: **skip Step 3
entirely** (do not inject anything into any file) and go straight to Step 4. Everything else —
credentials scaffold, stack detection/registration, verification — proceeds exactly as for `embed`.

| Variable | Required | Meaning / guidance |
|---|---|---|
| **Project key** | ✅ | URL-safe slug — lowercase letters, digits and dashes only, `^[a-z0-9-]+$` (e.g. `my-app`). Identifies this app's feedback. The project must already exist in the dashboard; the widget does not self-register it. |
| **<POINTER_PRODUCT> server URL** | ✅ | The **deployed** <POINTER_PRODUCT> origin your team gave you (e.g. `https://pointer.example.com`). No trailing slash. `http://localhost:8090` only for local dev. |
| **Enabled?** | optional | Whether to mount the widget now. Default `true` for dev; usually `false` in production builds unless feedback is wanted in prod. |
| **Screenshots?** | optional | The widget captures an element screenshot per comment by default. Pass `screenshot="false"` to disable. |

**Do not ask about environments.** Environments and their per-project activation are managed in the
dashboard, next to the project's URLs — never ask "which environment(s) does this app run in?". By
default, do not set an `environment` attribute on `<pointer-feedback>` at all: the server resolves it
per request from the page's own origin, matched against the URLs registered for the project, and a
signed-in stakeholder can switch it from the toolbar (a separate, role-based server setting decides
whether they're allowed to). Only if the user explicitly wants **this specific deployment** pinned to
one environment — mirroring `pointer init --environment <name>` — set `environment="local|staging|
production"` by hand, and only then consider `fixed-environment="true"` too (rare — it also disables
the toolbar switcher, regardless of role).

## Scope rules — read before editing anything

This task is small on purpose. Mounting a widget is a script tag and an element; treat anything
beyond that as out of scope.

1. **The loader goes in an HTML file. Always. Only.**

   | App type | The one file to edit |
   |---|---|
   | SPA (Angular, React, Vue, Svelte, Vite, CRA) | the app's `index.html` |
   | Server-rendered (ASP.NET MVC/Razor, Rails, Laravel, Django, Express+templates) | the **master layout** — `_Layout.cshtml`, `application.html.erb`, `layouts/app.blade.php`, `base.html`, etc. |
   | Next.js / Nuxt (no plain index.html) | the single root document — `app/layout.tsx`, `pages/_document.tsx`, `app.vue` |

   Never bootstrap code (`main.ts`, `index.tsx`), never a component, never a service, never a
   route, and never more than one file. A master layout or `index.html` is the one place that
   renders on every page, which is exactly what a feedback widget needs — putting it anywhere else
   means it mounts on some pages and not others, and the next person cannot find it.

2. **Touch the fewest files possible** — the HTML above, plus at most one env/config file for the
   values. If you are about to create a *new* file, stop: unless the host stack genuinely has
   nowhere to put a value, you are building an abstraction nobody asked for. No dedicated
   config/constants file, no service, no provider, no wrapper component, no barrel export.

   **That env file must be development-scoped** — `.env.development`, `.env.local`, or whatever
   your stack loads for its dev configuration *only*. **Never the shared `.env`.** A plain `.env`
   is loaded for every configuration, production included, so putting the values there enables the
   widget in production builds without ever touching a file with "prod" in its name. Verified in
   an Nx + webpack monorepo: values in `apps/<app>/.env` were interpolated into the **production**
   build's `index.html`; moved to `.env.development`, the production build left the placeholders
   unsubstituted and the guard correctly evaluated false.

   Check, don't assume: many repos track `.env` in git, so those values also ship to every
   developer and every CI run.
3. **Never modify a production config.** `environment.prod.ts`, `.env.production`,
   `appsettings.Production.json` and their equivalents stay exactly as they are. Enabling feedback
   in production is a decision for the user, made deliberately, later.
4. **Gate on configuration presence, not on a build flag.** The widget mounts when its project key
   and server are set and non-empty, and stays dormant otherwise. That way an environment that
   never mentions Pointer is already correct, with nothing to remove.
5. **Do not reformat, reorganise, or "improve" the files you touch.** Add your lines and leave.
6. **Do not read the whole repository.** Look at the files the detection step names, and stop.
   Reading a large monorepo start-to-finish is why this takes minutes instead of seconds.
7. **In a monorepo, change one app per Pointer project** — the one the user named, or the one being
   set up right now under **Monorepo (Nx) install** below. Never a shared library, and never an app
   nobody asked about — a monorepo with several apps gets several independent Pointer projects
   (`.pointer/config.json`'s `projects` map, one entry per app), not one project covering all of
   them.

## Step 2 — Detect the host stack

- **Vite (React/Vue/Svelte)** — `vite.config.*` + an `index.html` using `%VITE_*%` placeholders → Step 3a.
- **Plain static HTML** — a hand-written `index.html`, no bundler → Step 3b.
- **Angular** — `angular.json` + `src/index.html` → Step 3c.
- **Next.js** — `next.config.*`, `app/` or `pages/` → Step 3d.
- **Create React App / Webpack** — `react-scripts` in `package.json`, or a `webpack.config.*` with
  `DefinePlugin`/`EnvironmentPlugin`; uses the `REACT_APP_` prefix (CRA) or a config-defined name → Step 3f.
- **Server-rendered / MVC** — a master layout rather than an `index.html`: ASP.NET MVC or Razor
  Pages (`Views/Shared/_Layout.cshtml`, `Pages/Shared/_Layout.cshtml`), Rails
  (`app/views/layouts/application.html.erb`), Laravel (`resources/views/layouts/app.blade.php`),
  Django (`templates/base.html`), Express with a template engine → Step 3g.
- **API Swagger / OpenAPI docs page** (Swashbuckle/.NET, Scalar, Redoc, swagger-ui) — embed it so
  consumers can comment directly on endpoints → Step 3e.

Match the env-var prefix to whichever you detect (see the naming table in Step 3).

## Step 3 — Inject the loader

> **Skip this step entirely when delivery = extension** — no file in this app is touched; the
> reviewer's Chrome extension injects the widget instead. Go straight to Step 4.

The loader loads `<POINTER_SERVER>/pointer.js`, then appends a `<pointer-feedback>` element.

Do NOT write a `source-attr` attribute: `data-component-source` is already the widget's default, and
the build plugin stamps that same frozen name, so stating it changes nothing. Set it only for an app
whose stamper uses a different attribute. What DOES matter is that the app actually stamps the
attribute — usually a build plugin behind a dev flag such as `VITE_DEBUG`; see the Source mapping
note in Step 4. Without the stamp, applies still work but can't jump straight to the file.

> **Env-var naming is stack-specific — use the prefix the detected stack exposes to the browser, not a
> fixed `VITE_` one.** Browsers can't read raw env vars, so each bundler only exposes vars carrying its
> own prefix. Map the two logical keys (`*_POINTER_SERVER`, `*_POINTER_PROJECT`) onto the host's
> convention:
>
> | Detected stack | Prefix to use | Read in code as |
> |---|---|---|
> | Vite | `VITE_` | `import.meta.env.VITE_*` / `%VITE_*%` in `index.html` |
> | Next.js | `NEXT_PUBLIC_` | `process.env.NEXT_PUBLIC_*` |
> | Create React App / Webpack (`react-scripts`) | `REACT_APP_` | `process.env.REACT_APP_*` |
> | Webpack with custom `DefinePlugin` | whatever the config defines (often unprefixed `POINTER_*`) | `process.env.POINTER_*` |
> | Angular | — (no runtime env) | a field in `src/environments/environment.ts` — the **development** file only, never `environment.prod.ts` |
> | Plain HTML / static | — (no env) | hardcode attributes, or use `embed.js` (3e) |
>
> Whichever you pick, **mirror it in `.env.example`** so the names match what the code reads.
>
> **There is no environment key and no `environment` attribute.** The server resolves the environment
> of every comment from the page origin, matched against the URLs registered for the project (the
> dashboard's Projects → App URLs). One build therefore reports the right environment on local, staging
> and production without carrying any of them. Only `pointer init --environment <name>` pins an
> install to one environment, and then the CLI writes the attribute (and `VITE_POINTER_ENV`) itself.
> Do not add `*_POINTER_ENV`; if you find one from an older install, remove it.

### 3a. Vite

Add to `index.html` before `</body>`:

```html
<script>
  if (
    '%VITE_POINTER_SERVER%'.indexOf('http') === 0 &&
    '%VITE_POINTER_PROJECT%' !== ''
  ) {
    var s = document.createElement('script');
    s.src = '%VITE_POINTER_SERVER%/pointer.js';
    s.defer = true;
    document.head.appendChild(s);
    // document.body is null while the parser is still inside <head>, so the mount waits for the
    // document rather than assuming the snippet sits just above </body>.
    var mount = function () {
      if (document.querySelector('pointer-feedback')) return;
      var el = document.createElement('pointer-feedback');
      el.setAttribute('project', '%VITE_POINTER_PROJECT%');
      el.setAttribute('server', '%VITE_POINTER_SERVER%');
      document.body.appendChild(el);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
    else mount();
  }
</script>
```

Add the env keys to `.env` (and document them in `.env.example`):

```
VITE_POINTER_SERVER=<POINTER_SERVER>          # deployed <POINTER_PRODUCT> URL; http://localhost:8090 only for local dev
VITE_POINTER_PROJECT=<project-key>
```

Vite substitutes `%VITE_*%` in `index.html`. There is no separate on/off flag: the guard mounts the
widget only when `VITE_POINTER_SERVER` is a URL, so a build that must ship without <POINTER_PRODUCT>
simply leaves it empty and carries zero <POINTER_PRODUCT> code paths.

### 3b. Plain static HTML

Inline literal values before `</body>`:

```html
<script src="<POINTER_SERVER>/pointer.js" defer></script>
<pointer-feedback
  project="<project-key>"
  server="<POINTER_SERVER>"></pointer-feedback>
```

### 3c. Angular

**One file changes: `src/index.html`.** Not `main.ts`, not `app.config.ts`, not a service, not a
new config file — see the injection-target rule in Scope rules. Angular has no `%ENV%` substitution
in `index.html`, so the values are written literally, inside the same marker block every other
stack uses:

```html
<!-- pointer-feedback:start -->
<script src="<POINTER_SERVER>/pointer.js" defer></script>
<script>
  (function () {
    function mount() {
      if (document.querySelector("pointer-feedback")) return;
      var el = document.createElement("pointer-feedback");
      el.setAttribute("project", "<project-key>");
      el.setAttribute("server", "<POINTER_SERVER>");
      document.body.appendChild(el);
    }
    // document.body is null while the parser is still inside <head>. Without this guard the
    // snippet throws "Cannot read properties of null (reading 'appendChild')" and mounts nothing
    // whenever it is placed anywhere but just above </body> — which is exactly what happened the
    // first time an agent followed this page.
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", mount);
    else mount();
  })();
</script>
<!-- pointer-feedback:end -->
```

**Do not set an `environment` attribute, and do not resolve one in the page.** The server works it
out per request, by matching the page's origin against the URLs registered for the project in the
dashboard — so one committed file reports `staging` on staging and `production` on production, and
changing a URL needs no rebuild anywhere.

An `environment` attribute, if present, **overrides** that resolution. That is deliberate, for an
embed that must be pinned to one environment on purpose — but it means writing one by default
silently disables the mechanism and re-creates the problem it solves. A hand-written origin→
environment map in the page is the same mistake in a longer form: a second copy of something the
dashboard already owns, going stale the first time a URL changes.

If an origin matches nothing registered, the comment is still captured and tagged `unknown`, and the
dashboard shows the origin so the owner can register it. Nothing is lost, and nothing is guessed.

Place it immediately before `</body>`. Keep the `pointer-feedback:start/end` comments — `doctor`
looks for them to tell an install from a hand-rolled snippet.

**Keeping it out of production builds.** Angular ships one `index.html` per build, so this block is
in every configuration by default. If the user wants it dev-only, the Angular-native way is a
per-configuration index in `angular.json` — and that is *their* decision to make, not something to
do unprompted:

```jsonc
"configurations": { "production": { "index": "src/index.prod.html" } }
```

Mention it; do not do it unless asked. Never edit `environment.prod.ts` or any other production
config to achieve the same thing.

**Nx / monorepo:** the path is per-application — `apps/<app>/src/index.html`, for the one app the
user named. Never a shared lib, never a second app because it looked similar.

### 3d. Next.js

Use a client component (e.g. in the root `app/layout.tsx` via a `'use client'` effect, or a
`<Script>` for pointer.js + an effect that creates `<pointer-feedback>`), reading values from
`NEXT_PUBLIC_POINTER_*` env vars. Guard on an `enabled` flag so prod can opt out.

### 3e. API Swagger / OpenAPI docs page

A Swagger UI is just an HTML page — embed <POINTER_PRODUCT> so consumers can leave element-level comments on
endpoints. The <POINTER_PRODUCT> server hosts a one-line loader at **`<POINTER_SERVER>/embed.js?project=<key>`**
that injects `pointer.js` and mounts a configured `<pointer-feedback>` (server pre-filled). The page
owner just (1) references that loader and (2) — if the page sends a CSP — allowlists the <POINTER_PRODUCT> origin.

**ASP.NET / Swashbuckle (recommended: config-driven).** Put every <POINTER_PRODUCT> setting in a `<POINTER_PRODUCT>`
section of `appsettings.json` so it's toggled/tuned per environment (override in
`appsettings.{Environment}.json` or `Pointer__*` env vars):

```json
"<POINTER_PRODUCT>": {
  "Enabled": true,
  "Server": "<POINTER_SERVER>",
  "Project": "",
  "Environment": "staging"
}
```

Read it once after `builder.Build()` and drive **both** the embed and the CSP from it:

```csharp
var p = app.Configuration.GetSection("<POINTER_PRODUCT>");
var pEnabled = p.GetValue("Enabled", false);
var pServer  = (p["Server"] ?? "<POINTER_SERVER>").TrimEnd('/');
var pProject = string.IsNullOrWhiteSpace(p["Project"]) ? app.Environment.ApplicationName : p["Project"]!;
var pEnv     = string.IsNullOrWhiteSpace(p["Environment"]) ? "staging" : p["Environment"]!;
var pAllow   = pEnabled ? $" {pServer}" : "";   // origins to add to the Swagger CSP

app.UseSwaggerUI(c =>
{
    if (pEnabled)
        c.InjectJavascript($"{pServer}/embed.js?project={Uri.EscapeDataString(pProject)}&environment={Uri.EscapeDataString(pEnv)}");
});
```

`Enabled` turns the whole thing on/off (per environment); `Project` blank → this app's own name;
`Server` is the <POINTER_PRODUCT> URL; `Environment` tags the comments.

**⚠️ CSP — the common gotcha.** If the docs page sends a `Content-Security-Policy` (many API
templates do, scoped to `/swagger`), the cross-origin widget is blocked until you allowlist the
<POINTER_PRODUCT> origin. Build the `/swagger` CSP with `pAllow` so it follows the same config (and the hole
disappears when disabled):

```csharp
$"default-src 'self'; script-src 'self' 'unsafe-inline'{pAllow}; connect-src 'self'{pAllow}; " +
$"style-src 'self' 'unsafe-inline'{pAllow}; img-src 'self' data:{pAllow}; frame-ancestors 'none'"
```

(`script-src` loads embed.js/pointer.js; `connect-src` the comments/login/upload API; `style-src`
the shadow-DOM `pointer.css` `<link>`; `img-src` screenshot thumbnails.)

**Other renderers** (Scalar, Redoc, standalone swagger-ui, static docs): just add one script tag
wherever that renderer allows custom JS — and, if the page has a CSP, allowlist `<POINTER_SERVER>`
in the same directives above:

```html
<script src="<POINTER_SERVER>/embed.js?project=<your-api>"></script>
```

### 3f. Create React App / Webpack

CRA interpolates `%REACT_APP_*%` into `public/index.html` at build time, exactly as Vite does with
`%VITE_*%`, so this stays an HTML-only change (Scope rule 1) — **not** a mount added to
`src/index.tsx`. Nx with webpack behaves the same way under the `NX_PUBLIC_` prefix.

Use the Step 3a block in `public/index.html`, with the prefix this stack exposes substituted
throughout: `REACT_APP_` for CRA/`react-scripts`, `NX_PUBLIC_` for Nx, or whatever name a custom
webpack `DefinePlugin`/`EnvironmentPlugin` defines.

```html
<!-- public/index.html — CRA shown; swap the prefix for your stack -->
<!-- pointer-feedback:start -->
<script>
  if (
    '%REACT_APP_POINTER_SERVER%'.indexOf('http') === 0 &&
    '%REACT_APP_POINTER_PROJECT%' !== ''
  ) {
    var s = document.createElement('script');
    s.src = '%REACT_APP_POINTER_SERVER%/pointer.js';
    s.defer = true;
    document.head.appendChild(s);
    var mount = function () {
      if (document.querySelector('pointer-feedback')) return;
      var el = document.createElement('pointer-feedback');
      el.setAttribute('project', '%REACT_APP_POINTER_PROJECT%');
      el.setAttribute('server', '%REACT_APP_POINTER_SERVER%');
      document.body.appendChild(el);
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount);
    else mount();
  }
</script>
<!-- pointer-feedback:end -->
```

If a stack genuinely has no HTML substitution at all, say so to the user and ask before mounting
from JavaScript — do not do it silently, and never as the default.

```
# .env  (CRA shown — for custom Webpack, use the names your DefinePlugin injects)
REACT_APP_POINTER_SERVER=<POINTER_SERVER>     # http://localhost:8090 only for local dev
REACT_APP_POINTER_PROJECT=<project-key>
```

### 3g. Server-rendered / MVC

**The master layout, and nothing else.** One layout renders every page, which is exactly the reach
a feedback widget needs. Never a per-view partial, never a controller, never a `_ViewImports`, and
never a second layout "for consistency" — if the app genuinely has two layouts serving different
areas, ask the user which one they want rather than editing both.

| Framework | The file |
|---|---|
| ASP.NET MVC / Razor Pages | `Views/Shared/_Layout.cshtml` or `Pages/Shared/_Layout.cshtml` |
| Rails | `app/views/layouts/application.html.erb` |
| Laravel | `resources/views/layouts/app.blade.php` |
| Django | `templates/base.html` |
| Express + Handlebars/Pug/EJS | `views/layout.*` |

Put the block immediately before `</body>`, and read the values from the framework's own
configuration so the gate is server-side — a page that is never rendered with a project key never
ships the script at all. Razor, as the most common case:

```cshtml
@* appsettings.json:  "Pointer": { "Enabled": true, "Server": "...", "Project": "...", "Environment": "staging" } *@
@inject IConfiguration Config
@if (Config.GetValue<bool>("Pointer:Enabled") && !string.IsNullOrWhiteSpace(Config["Pointer:Project"]))
{
    <!-- pointer-feedback:start -->
    <script src="@Config["Pointer:Server"]/pointer.js" defer></script>
    <pointer-feedback
        project="@Config["Pointer:Project"]"
        server="@Config["Pointer:Server"]"
        environment="@(Config["Pointer:Environment"] ?? "staging")"></pointer-feedback>
    <!-- pointer-feedback:end -->
}
```

Add the `Pointer` section to `appsettings.Development.json` only. Production is enabled by the user
later, deliberately, via `appsettings.Production.json` or `Pointer__*` environment variables — see
Scope rule 3.

## Step 4 — Authenticate the AI apply tool  ⚠️ do not skip

<POINTER_PRODUCT>'s whole point is that an AI agent later **pulls and applies** the feedback queue — and
**every API endpoint requires auth**. The apply skill (`<POINTER_SERVER>/skill.md`) authenticates
with a **long-lived personal API key** (not email/password), never a raw email/password pair. So
**always make sure a key is set up now**, even though the value itself may still need filling in —
don't leave it as a silent TODO.

**Prefer authenticating once per machine, not once per repo.** If `npx`/Node is available:

```bash
npx pointer-feedback login --server <POINTER_SERVER>
```

With no `--key` on a real terminal this opens a browser to sign in (prints a link + a short code,
waits for approval, mirrors `gh auth login`); pass `--key <key>` instead to skip the browser and
validate a pasted key (from the **profile page**, or the dashboard's **quick-start guide**, which
shows it pre-filled for copy-paste). Either way the result is saved to a **global, per-machine
credential store** (`~/.config/pointer/credentials.json`, mode `0600` — never printed, never
committed, never inside this or any other repo). Every command in every repo on this machine — this
apply skill included — then resolves it automatically: `POINTER_API_KEY` env var → this repo's
`.pointer/credentials.env` → the global store, in that order. Run `npx pointer-feedback whoami` to
confirm who is signed in and which of those three answered; `npx pointer-feedback logout` removes it.

**What's committed vs. gitignored in `.pointer/`** — only two files are meant to be shared via git;
everything else is derived or per-machine and every clone/developer gets (or refreshes) their own
copy instead:

| File | Committed? | Why |
|---|---|---|
| `config.json` | ✅ committed | team config — server, project, delivery |
| `stack.json` | ✅ committed | detected frontend/backend/design tokens — not a secret |
| `credentials.env` | ❌ gitignored | only written when the key is kept repo-local instead of in the global store (`--local-credentials`, or answering "no" to `init`'s save prompt) — holds the real `POINTER_API_KEY` |
| `pointer.sh` | ❌ gitignored | the no-Node CLI fallback; `npx pointer-feedback update` (or re-running `install.sh`) refreshes it in every clone |
| `manifest.json` | ❌ gitignored | build-time source map |

**No Node/npx at all** (the true no-Node fallback — see `pointer.sh`, installed by `install.sh` or
`npx pointer-feedback update`): create the repo-local scaffold by hand instead of running `login`:

```bash
mkdir -p .pointer
[ -f .pointer/credentials.env ] || printf 'POINTER_API_KEY=\n' > .pointer/credentials.env
touch .gitignore
grep -qxF '.pointer/*' .gitignore || echo '.pointer/*' >> .gitignore
grep -qxF '!.pointer/config.json' .gitignore || echo '!.pointer/config.json' >> .gitignore
grep -qxF '!.pointer/stack.json' .gitignore || echo '!.pointer/stack.json' >> .gitignore
grep -qxF '!.pointer/projects/' .gitignore || echo '!.pointer/projects/' >> .gitignore
```

Note the directory form: `.pointer/*` (contents), never bare `.pointer/` — git does not descend into
an excluded directory, so the bare form would make the `!` re-includes above inert and silently
ignore `config.json`/`stack.json`/`projects/` too. The last line only matters for a monorepo (see
**Monorepo (Nx) install** below) — it re-includes `.pointer/projects/`, where each app's own
`projects/<key>.stack.json` lives, committed exactly like `stack.json`.

**Then explicitly tell the user** (this is the critical step they must action):

> Run `npx pointer-feedback login` once — it opens your browser to sign in, or pass
> `--key <your <POINTER_PRODUCT> API key>` to skip the browser (copy the key from your **profile
> page**, generating one first if you don't have one yet, or from the dashboard's **quick-start
> guide**, which shows it pre-filled for copy-paste) — and every repo on this machine, this one
> included, is authenticated from then on. If Node/npx is unavailable, fill in `POINTER_API_KEY` in
> `.pointer/credentials.env` instead (gitignored — never commit it). Until one of these is done,
> pulling or applying the feedback queue will fail with a login error. Any <POINTER_PRODUCT>
> account's key works (any role can fetch/apply); using a dedicated `Developer`-role account is
> conventional but not required.

The apply workflow itself is the separate <POINTER_PRODUCT> skill served at `<POINTER_SERVER>/skill.md`
— a small entry file plus three sibling sub-files (`apply.md`, `translate.md`, `advanced.md`) fetched
alongside it into the same skill folder (or concatenated into one file for Cursor/Windsurf); all four
install and refresh together, so there is nothing extra to do here.

> **⚠️ Install the skills into YOUR AI tool's own directory — not blindly into `.claude/`.**
> The installer defaults to `.claude/skills/` (Claude Code). **If you are not Claude Code, clone the
> skills into the directory your tool actually reads**, by passing it to the installer:
>
> ```bash
> curl -fsSL <POINTER_SERVER>/install.sh | sh -s -- <your-tool-dir>
> ```
>
> | AI tool | Skills/rules directory to use |
> |---|---|
> | Claude Code | `.claude/skills/` (default) |
> | Cursor | `.cursor/rules/` |
> | Windsurf | `.windsurf/rules/` |
> | Antigravity, Codex, or any tool reading the standard Agent Skills layout | `.agents/skills/` |
> | GitHub Copilot | `.github/` (e.g. a `copilot-instructions.md` / rules location) |
> | Cline / other | that tool's rules/skills directory (the CLI's own `other` fallback is also `.agents/skills/`) |
> | none of these | just hand `skill.md` (and this file) to your agent directly |
>
> Identify which tool you are and pick the matching directory; if unsure, ask the user. The
> `.pointer/credentials.env` scaffold (above) is tool-independent — it always lives at the repo root.

## Step 5 — Detect and register the tech stack  (once — never repeated)

The apply skill (`skill.md`) needs to know this app's stack to decide *how* to apply a styling fix
(edit `className` on a Tailwind app vs. the winning CSS rule elsewhere) and whether a failing API
call in a bug report is something to chase into this repo's own backend or an external service.
Detect it now so `skill.md` never has to guess later — this is a one-time registration per project,
not something either skill repeats on every run.

1. **Detect `frontend`** — you likely already know this from Step 2's stack detection; reuse it
   rather than re-deriving it. Add framework + styling tokens from a fixed, lowercase vocabulary:
   `react`, `angular`, `vue`, `next`, `nuxt`, `svelte`, `tailwind`, `scss`, `css-modules` (add more
   as genuinely needed, but keep tokens lowercase and from this style).
2. **Detect `backend`**, if this repo contains one (skip — leave `null` — if the backend is clearly
   a separate repo or an external API this app just calls):
   - Runtime tokens: `.csproj`/`.sln` → `dotnet`; `go.mod` → `go`;
     `pyproject.toml`/`requirements.txt` → `python`; `Gemfile` → `ruby`; `package.json` with
     `express`/`@nestjs/core`/`fastify` → `node`.
   - **MVC framework tokens too, not just the runtime** — `skill.md`'s route-to-view convention
     mapping needs to know *which* MVC framework, not just the language: Rails (`config/routes.rb`)
     → also add `rails`; ASP.NET MVC (`Controllers/*Controller.cs` + `Views/`, not just any
     `.csproj`) → also add `aspnetmvc`; Laravel (`routes/web.php`) → also add `laravel`; Django
     (`urls.py`) → also add `django`; Spring MVC (`@RequestMapping`/`@GetMapping` annotations) →
     also add `spring`. So `backend` for a Rails app is `["ruby", "rails"]`, not just `["ruby"]`.
   - Datastore hints from `docker-compose.*`/connection strings/ORM config, if present: `postgres`,
     `mysql`, `mongodb`, `redis`.
3. **Self-identify which AI tool you are** (`aiTool`) from the same vocabulary `skill.md` uses:
   `claude-code`, `opencode-glm`, `cursor`, `antigravity`, `windsurf`, `other`.
4. **Register once:**
   ```bash
   curl -s "${AUTH[@]}" -X POST "$SERVER/api/projects/$PROJECT/stack" \
     -H 'Content-Type: application/json' \
     -d '{"frontend":["react","tailwind"],"backend":["dotnet","postgres"],"aiTool":"claude-code"}'
   ```
   (`$SERVER`/`$PROJECT`/`${AUTH[@]}` — same login flow as `skill.md` Steps 1-2; use the same
   automation credentials from Step 4 above.) The response's `data` is the authoritative merged
   state — write it verbatim to a **committed** (not gitignored — this isn't a secret) repo-local
   file:
   ```bash
   mkdir -p .pointer
   echo '<response data object>' > .pointer/stack.json
   ```
5. **Tell the user**: `.pointer/stack.json` was created and committed — `skill.md`'s apply step
   reads it directly, with no further server round trip for `frontend`/`backend`. Note that when
   using the CLI (`npx pointer-feedback init`), a `design` block is automatically detected and
   written to `.pointer/stack.json` so the AI knows existing tokens (Tailwind, CSS variables, SCSS).
   The `design` block is local-only and must never be sent to the server in `POST /api/projects/$PROJECT/stack`.
   If a teammate's AI tool later applies comments on this same project, its own first run will add itself to
   `aiTools` the same way — that's expected, not a bug.

## Step 6 — Verify

**If delivery = embed** (the default):

1. Start the app and ensure `<POINTER_SERVER>` is reachable.
2. Load a page — a <POINTER_PRODUCT> toolbar appears (no login popup on load; it's deferred).
3. Click **+ Comment** → sign in or **Create account** → click an element → leave a comment.
4. Confirm the project appears in the <POINTER_PRODUCT> dashboard (`<POINTER_SERVER>/admin/`) with the comment.
5. Confirm `.pointer/stack.json` exists and is staged for commit (not gitignored).

**If delivery = extension:** there is nothing to find in this app's source — the widget only
appears once a reviewer installs the Chrome extension and activates it on this app's tab, so do not
look for a `<pointer-feedback>` element or a toolbar here. Instead:

1. `GET <POINTER_SERVER>/api/branding` and read `data.extension.storeUrl`.
2. If it is set, tell the user to install the extension from that URL, open its Options page, set
   the server to `<POINTER_SERVER>`, sign in, then open the app, click the extension icon, pick
   this project, and **Activate** — the toolbar then appears on that tab.
3. If `storeUrl` is empty, tell the user: "Your admin has not set the Chrome Web Store URL yet
   (Settings → Extension) — ask them to set it before reviewers can install the extension."
4. Still confirm `.pointer/stack.json` exists and is staged for commit — stack registration is
   unaffected by delivery mode.

## Monorepo (Nx) install

A monorepo with several independently-deployed apps gets several independent Pointer projects, one
per app — never one project that supposedly covers all of them (Scope rule 7). This mirrors exactly
what `npx pointer-feedback init` itself does for a monorepo; follow the same flow by hand when
driving it through this skill instead.

1. **Detect it.** An Nx workspace has `nx.json` at the repo root. Discover candidate apps under
   `apps/*`:
   - a directory with its own `project.json` whose `projectType` is `"application"` (its Pointer
     project's suggested key: the `project.json` `name`, or the directory name) — a `"library"`
     `project.json` is never a candidate;
   - OR, lacking a `project.json` at all, a directory that still has its own `src/index.html` or
     `index.html` — include it, but tell the user it has no `project.json` so they can confirm it
     really is meant to be one.

   A non-Nx monorepo has no `nx.json` to detect from — ask the user which app directory (or
   directories) they mean instead, and treat each the same way from step 2 on.

2. **One Pointer project per app the user wants covered.** For each: pick or create its project (Step
   1's question, scoped to that app — the project must already exist in the dashboard, same rule as
   ever) and its delivery (the repo's own default — see Step 1 — is the suggested starting point,
   override only if this app genuinely differs). Do not ask about environments here either — same
   rule as Step 1, per app instead of once.

3. **Inject per app**, in that app's own directory — `apps/<app>/index.html`,
   `apps/<app>/src/index.html`, or wherever Step 2's detection finds it for THAT app. Never the repo
   root's `index.html`; a repo like this usually has none, or a placeholder that renders nothing.

4. **Register the stack per app**, writing to `.pointer/projects/<key>.stack.json` — **not**
   `.pointer/stack.json` — mirroring Step 5, once per app:
   ```bash
   mkdir -p .pointer/projects
   echo '<response data object>' > .pointer/projects/<key>.stack.json
   ```
   Add `!.pointer/projects/` to `.gitignore` (Step 4's scaffold, above) so every app's stack file
   stays committed exactly like a single-project repo's `.pointer/stack.json`.

5. **Write `.pointer/config.json`'s `projects` map** — one entry per app, keyed by its Pointer
   project key:
   ```json
   {
     "server": "<POINTER_SERVER>",
     "aiTool": "claude-code",
     "delivery": "embed",
     "projects": {
       "<key>": {
         "path": "apps/<app>",
         "htmlPath": "apps/<app>/src/index.html",
         "delivery": "embed"
       }
     }
   }
   ```
   There is **no top-level `project`/`htmlPath`** once `projects` exists — every app
   is a keyed entry, including one that was already set up as the (formerly) single project: if
   `config.json` currently has a plain `project` and you are adding a second app, move the existing
   one into `projects` first (best-guess `path` from its recorded `htmlPath`, or `.` if it has none
   — say so, so the user can confirm) rather than leaving it stranded outside the map.

6. **Credentials, skills and `pointer.sh` stay repo-level** — Step 4 runs once for the whole repo,
   not once per app.

7. **Verify per app**, same as Step 6, run once for each: for `embed` delivery, start that app's dev
   server and confirm the widget mounts; for `extension`, confirm its project is selectable in the
   extension popup once activated. `npx pointer-feedback list`/`apply` cover every configured
   project unless the user passes `--project <key>` or is working from inside that app's directory
   — see `skill.md`'s Monorepos note.

## Notes & gotchas

- **`project` is required**; the component disables itself without it.
- **`server`** defaults to the script's origin if omitted — set it explicitly when the app and the
  <POINTER_PRODUCT> server are different origins (the usual case).
- **Cross-origin is fine:** `pointer.js` (script), `pointer.css` (link), and uploaded images (`<img>`)
  aren't CORS-restricted; API calls use the server's permissive CORS policy.
- **Auth:** stakeholders need a <POINTER_PRODUCT> account; self-signup (an admin-approved request) is built into
  the widget. The token is stored in `localStorage` (`pointer_token`).
- **Source mapping (enables precise applies — offer this):** <POINTER_PRODUCT> records the
  `data-component-source` attribute of the clicked element, so the apply step opens the **exact
  file** instead of searching by snapshot text. Nothing stamps that attribute by default.

  **Do not tell the user to write a build plugin — one ships with the CLI.** For a Vite app
  (React, Vue, Svelte):

  ```bash
  npx -y pointer-feedback init --source-map
  ```

  which adds the plugin to `vite.config.*` and sets `VITE_POINTER_SOURCE=true` in the
  development env file. The manual equivalent, if you would rather edit the config yourself:

  ```ts
  import pointerSource from 'pointer-feedback/vite';

  export default defineConfig({
    plugins: [react(), pointerSource({ enabled: process.env.VITE_POINTER_SOURCE === 'true' })],
  });
  ```

  What it does: stamps each component's root element with an **8-character hash** of its
  repo-relative path plus export name, and writes `.pointer/manifest.json` mapping hash → file.
  The hash rather than the path is deliberate — production HTML then reveals nothing about your
  source layout. The manifest is gitignored and regenerated on every build; `pointer get --json`
  resolves a hash back to a file, and `pointer map --from-source` rebuilds it after a rename.

  **Non-Vite stacks (Angular, Next, CRA, server-rendered):** there is no plugin yet. Say so plainly
  rather than inventing one — the widget still works, and applies fall back to matching on the
  element snapshot, which is slower and less exact.
- Keep the `enabled` guard so production builds can ship without the widget when desired.
- **Privacy & self-hosting:** For the full engineering breakdown of what the widget captures, what is never captured, retention and deletion semantics, and self-hosting boundaries, see `<POINTER_SERVER>/data.html`.
