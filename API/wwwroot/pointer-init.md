---
name: pointer-init
description: Use when the user wants to add, install, init, or integrate the Pointer feedback widget (<pointer-feedback>) into an app — e.g. "add Pointer to this app", "set up Pointer feedback", "integrate the feedback widget". Asks the user for the variables (project key, Pointer server URL, environment), detects the host stack (Vite/Angular/Next/static), injects the loader, wires the env, and verifies. No build step required.
---

# Add Pointer to this app

Pointer is an element-level feedback widget delivered as a single Web Component,
`<pointer-feedback>`, loaded from a Pointer server's `/pointer.js`. It renders entirely inside a
Shadow DOM (no CSS collisions), shows a small toolbar, and lets authenticated stakeholders click any
element and leave a comment. Projects **self-register**: the first time an app loads/comments with a
given project key, it appears in the Pointer dashboard.

This skill wires the widget into the **current** app. Do not guess the variables — **ask the user**.

> **Server URL** = the deployed Pointer origin (the one you fetched this skill from). When this skill
> is served by a running Pointer server, the examples below are **auto-filled** with that URL; if you
> see a literal `<POINTER_SERVER>` placeholder, replace it with your deployed Pointer URL.
> `http://localhost:8090` is only the local-dev default — **never ship `localhost` to production.**

## Step 1 — Ask the user for the variables

| Variable | Required | Meaning / guidance |
|---|---|---|
| **Project key** | ✅ | URL-safe slug, `^[A-Za-z0-9._-]+$` (e.g. `my-app`). Identifies this app's feedback; self-registers in the dashboard. |
| **Pointer server URL** | ✅ | The **deployed** Pointer origin your team gave you (e.g. `https://pointer.example.com`). No trailing slash. `http://localhost:8090` only for local dev. |
| **Environment** | optional | `local` \| `staging` \| `production` — tags each comment. Default `staging`. |
| **Enabled?** | optional | Whether to mount the widget now. Default `true` for dev; usually `false` in production builds unless feedback is wanted in prod. |
| **Screenshots?** | optional | The widget captures an element screenshot per comment by default. Pass `screenshot="false"` to disable. |

## Step 2 — Detect the host stack

- **Vite (React/Vue/Svelte)** — `vite.config.*` + an `index.html` using `%VITE_*%` placeholders → Step 3a.
- **Plain static HTML** — a hand-written `index.html`, no bundler → Step 3b.
- **Angular** — `angular.json` + `src/index.html` → Step 3c.
- **Next.js** — `next.config.*`, `app/` or `pages/` → Step 3d.
- **Create React App / Webpack** — `react-scripts` in `package.json`, or a `webpack.config.*` with
  `DefinePlugin`/`EnvironmentPlugin`; uses the `REACT_APP_` prefix (CRA) or a config-defined name → Step 3f.
- **API Swagger / OpenAPI docs page** (Swashbuckle/.NET, Scalar, Redoc, swagger-ui) — embed it so
  consumers can comment directly on endpoints → Step 3e.

Match the env-var prefix to whichever you detect (see the naming table in Step 3).

## Step 3 — Inject the loader

The loader loads `<POINTER_SERVER>/pointer.js`, then appends a configured `<pointer-feedback>` element.
Always set `source-attr="data-component-source"` so the widget can capture the source path of clicked
elements — **and make sure the app actually stamps that attribute** (usually a build plugin behind a
dev flag such as `VITE_DEBUG`; see the Source mapping note in Step 4). Without it, applies still work
but can't jump straight to the file.

> **Env-var naming is stack-specific — use the prefix the detected stack exposes to the browser, not a
> fixed `VITE_` one.** Browsers can't read raw env vars, so each bundler only exposes vars carrying its
> own prefix. Map the four logical keys (`*_POINTER_ENABLED`, `*_POINTER_SERVER`, `*_POINTER_PROJECT`,
> `*_POINTER_ENV`) onto the host's convention:
>
> | Detected stack | Prefix to use | Read in code as |
> |---|---|---|
> | Vite | `VITE_` | `import.meta.env.VITE_*` / `%VITE_*%` in `index.html` |
> | Next.js | `NEXT_PUBLIC_` | `process.env.NEXT_PUBLIC_*` |
> | Create React App / Webpack (`react-scripts`) | `REACT_APP_` | `process.env.REACT_APP_*` |
> | Webpack with custom `DefinePlugin` | whatever the config defines (often unprefixed `POINTER_*`) | `process.env.POINTER_*` |
> | Angular | — (no runtime env) | a field in `src/environments/environment*.ts` |
> | Plain HTML / static | — (no env) | hardcode attributes, or use `embed.js` (3e) |
>
> Whichever you pick, **mirror it in `.env.example`** so the names match what the code reads.

### 3a. Vite

Add to `index.html` before `</body>`:

```html
<script>
  if (
    '%VITE_POINTER_ENABLED%' === 'true' &&
    '%VITE_POINTER_SERVER%'.indexOf('http') === 0
  ) {
    var s = document.createElement('script');
    s.src = '%VITE_POINTER_SERVER%/pointer.js';
    s.defer = true;
    document.head.appendChild(s);
    var el = document.createElement('pointer-feedback');
    el.setAttribute('project', '%VITE_POINTER_PROJECT%');
    el.setAttribute('server', '%VITE_POINTER_SERVER%');
    el.setAttribute('environment', '%VITE_POINTER_ENV%');
    el.setAttribute('source-attr', 'data-component-source');
    document.body.appendChild(el);
  }
</script>
```

Add the env keys to `.env` (and document them in `.env.example`):

```
VITE_POINTER_ENABLED=true
VITE_POINTER_SERVER=<POINTER_SERVER>          # deployed Pointer URL; http://localhost:8090 only for local dev
VITE_POINTER_PROJECT=<project-key>
VITE_POINTER_ENV=staging
```

Vite substitutes `%VITE_*%` in `index.html`; the `enabled` guard means a production build with
`VITE_POINTER_ENABLED=false` ships zero Pointer code paths.

### 3b. Plain static HTML

Inline literal values before `</body>`:

```html
<script src="<POINTER_SERVER>/pointer.js" defer></script>
<pointer-feedback
  project="<project-key>"
  server="<POINTER_SERVER>"
  environment="staging"
  source-attr="data-component-source"></pointer-feedback>
```

### 3c. Angular

Angular does not substitute `%ENV%` in `index.html`. Easiest: add a literal loader to
`src/index.html` before `</body>` (markup as in 3b). For env-switching, read from
`src/environments/environment*.ts` and append the element in `main.ts` after bootstrap.

### 3d. Next.js

Use a client component (e.g. in the root `app/layout.tsx` via a `'use client'` effect, or a
`<Script>` for pointer.js + an effect that creates `<pointer-feedback>`), reading values from
`NEXT_PUBLIC_POINTER_*` env vars. Guard on an `enabled` flag so prod can opt out.

### 3e. API Swagger / OpenAPI docs page

A Swagger UI is just an HTML page — embed Pointer so consumers can leave element-level comments on
endpoints. The Pointer server hosts a one-line loader at **`<POINTER_SERVER>/embed.js?project=<key>`**
that injects `pointer.js` and mounts a configured `<pointer-feedback>` (server pre-filled). The page
owner just (1) references that loader and (2) — if the page sends a CSP — allowlists the Pointer origin.

**ASP.NET / Swashbuckle (recommended: config-driven).** Put every Pointer setting in a `Pointer`
section of `appsettings.json` so it's toggled/tuned per environment (override in
`appsettings.{Environment}.json` or `Pointer__*` env vars):

```json
"Pointer": {
  "Enabled": true,
  "Server": "<POINTER_SERVER>",
  "Project": "",
  "Environment": "staging"
}
```

Read it once after `builder.Build()` and drive **both** the embed and the CSP from it:

```csharp
var p = app.Configuration.GetSection("Pointer");
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
`Server` is the Pointer URL; `Environment` tags the comments.

**⚠️ CSP — the common gotcha.** If the docs page sends a `Content-Security-Policy` (many API
templates do, scoped to `/swagger`), the cross-origin widget is blocked until you allowlist the
Pointer origin. Build the `/swagger` CSP with `pAllow` so it follows the same config (and the hole
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

CRA and most Webpack setups have no HTML placeholder substitution, so mount from JS and read the
env vars under **the prefix this stack exposes** — `REACT_APP_` for CRA/`react-scripts`, or whatever
name a custom Webpack `DefinePlugin`/`EnvironmentPlugin` defines (often unprefixed `POINTER_*`). Add
the mount near the app root (e.g. `src/index.tsx`):

```js
// uses CRA's REACT_APP_ prefix — swap to your Webpack-defined names if different
if (process.env.REACT_APP_POINTER_ENABLED === 'true' &&
    (process.env.REACT_APP_POINTER_SERVER || '').indexOf('http') === 0) {
  const server = process.env.REACT_APP_POINTER_SERVER;
  const s = document.createElement('script');
  s.src = server + '/pointer.js';
  s.defer = true;
  document.head.appendChild(s);
  const el = document.createElement('pointer-feedback');
  el.setAttribute('project', process.env.REACT_APP_POINTER_PROJECT);
  el.setAttribute('server', server);
  el.setAttribute('environment', process.env.REACT_APP_POINTER_ENV || 'staging');
  el.setAttribute('source-attr', 'data-component-source');
  document.body.appendChild(el);
}
```

```
# .env  (CRA shown — for custom Webpack, use the names your DefinePlugin injects)
REACT_APP_POINTER_ENABLED=true
REACT_APP_POINTER_SERVER=<POINTER_SERVER>     # http://localhost:8090 only for local dev
REACT_APP_POINTER_PROJECT=<project-key>
REACT_APP_POINTER_ENV=staging
```

## Step 4 — Create the AI apply-tool credentials  ⚠️ do not skip

Pointer's whole point is that an AI agent later **pulls and applies** the feedback queue — and
**every API endpoint requires auth**. The apply skill (`<POINTER_SERVER>/skill.md`) reads a
gitignored **`.pointer/credentials.env`** and fails to log in if it's missing. So **always create it
now**, even though the values are filled in later — don't leave it as a silent TODO.

**Run this** (creates the file and gitignores it):

```bash
mkdir -p .pointer
printf 'POINTER_EMAIL=\nPOINTER_PASSWORD=\n' > .pointer/credentials.env
grep -qxF '.pointer/' .gitignore 2>/dev/null || echo '.pointer/' >> .gitignore
```

**Then explicitly tell the user** (this is the critical step they must action):

> Created `.pointer/credentials.env` and gitignored `.pointer/`. **Fill in `POINTER_EMAIL` +
> `POINTER_PASSWORD`** — your Pointer account, or a dedicated automation user an admin created in the
> dashboard (any role can fetch/apply; a `Developer`-role user is conventional). Until these are set,
> pulling or applying the feedback queue will fail with a login error. Never commit this file.

The apply workflow itself is the separate Pointer skill served at `<POINTER_SERVER>/skill.md` —
install it wherever your AI tool reads skills/rules (e.g. Claude Code
`.claude/skills/pointer-feedback/SKILL.md`, Cursor `.cursor/rules/`, or just hand the file to your agent).

## Step 5 — Verify

1. Start the app and ensure `<POINTER_SERVER>` is reachable.
2. Load a page — a Pointer toolbar appears (no login popup on load; it's deferred).
3. Click **+ Comment** → sign in or **Create account** → click an element → leave a comment.
4. Confirm the project appears in the Pointer dashboard (`<POINTER_SERVER>/admin/`) with the comment.

## Notes & gotchas

- **`project` is required**; the component disables itself without it.
- **`server`** defaults to the script's origin if omitted — set it explicitly when the app and the
  Pointer server are different origins (the usual case).
- **Cross-origin is fine:** `pointer.js` (script), `pointer.css` (link), and uploaded images (`<img>`)
  aren't CORS-restricted; API calls use the server's permissive CORS policy.
- **Auth:** stakeholders need a Pointer account; self-signup (an admin-approved request) is built into
  the widget. The token is stored in `localStorage` (`pointer_token`).
- **Source mapping (enables precise applies — check this):** Pointer records the `source-attr`
  (default `data-component-source`) of the clicked element, e.g. `path/to/Component:line`, so the
  apply step opens the **exact file**. Most apps don't emit this by default — it's produced by a
  **build plugin gated behind a dev/preview flag** (e.g. `VITE_DEBUG=true` driving a Babel/SWC plugin
  that stamps `data-component-source`). **Enable that flag in the environments where stakeholders give
  feedback** (local/staging/preview). Without it the widget still works, but applies fall back to
  searching by element snapshot/classes — slower and less exact. Confirm the attribute is present
  (inspect an element) as part of verification.
- Keep the `enabled` guard so production builds can ship without the widget when desired.
