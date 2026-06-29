# Pointer

**Element-level feedback for any web app.** Stakeholders (client / PM / tester / developer) click
any element on your running app and leave a short comment; you — or your AI tool — pull the queue and
apply the changes to the real source files. Comments are partitioned by project and tied to a real
account (never anonymous).

**Hosted instance:**

| | URL |
|---|---|
| API + widget (`pointer.js`) | `https://api.pointer.moamen.work` |
| Dashboard (review comments) | `https://app.pointer.moamen.work` |

> Want to run your own instance, build the component, or deploy? See
> **[Self-hosting & development](docs/SELF_HOSTING.md)**.

## How it works

1. **Add the widget** to your app (one `<script>` + one tag — below).
2. **Stakeholders comment** on elements. They sign in or self-sign-up on first use (an admin
   approves new accounts); the author + role come from their token.
3. **Apply the feedback** — tell your AI tool *"apply pending pointer comments"* (the `pointer-feedback`
   skill edits the real source), or mark items done in the dashboard.

---

## 1. Add the widget

> Adding Pointer to an **API's Swagger / OpenAPI docs** instead of an app page? Jump to
> [section 3](#3-embed-in-an-apis-swagger-page-optional).

### Easiest — let your AI do it (`pointer-init` skill) ⭐

Install the skill, then tell your AI tool to run it — it asks for the project key / server /
environment, detects your stack (Vite / Angular / Next / static), injects the loader, wires env, and
verifies. No manual edits.

```bash
curl -fsSL https://api.pointer.moamen.work/install.sh | sh   # installs the Pointer skills
```

Then run **`/pointer-init`** in your AI tool. (Skill details + applying feedback → [section 2](#2-get-the-ai-skills).)

### Manual — two lines (any stack / static HTML)

Prefer to wire it by hand? Drop these before `</body>`:

```html
<script src="https://api.pointer.moamen.work/pointer.js" defer></script>
<pointer-feedback project="my-app" environment="staging"></pointer-feedback>
```

That's the whole install — `server` defaults to the script's own origin, so loading `pointer.js`
from the Pointer server is enough.

### Manual — env-gated (any bundler, ship it only where you want)

Gate the widget on env vars so it never appears in builds that shouldn't have it. Pointer has **no
fixed env-var prefix** — only your bundler does. Browsers can't read raw env vars, so every bundler
requires a prefix to expose one to client code, and that prefix is **stack-specific**. Use your
stack's convention for the four keys `*_POINTER_ENABLED`, `*_POINTER_SERVER`, `*_POINTER_PROJECT`,
`*_POINTER_ENV`:

| Stack | Prefix | Example | Read in code as |
|---|---|---|---|
| Vite (React/Vue/Svelte) | `VITE_` | `VITE_POINTER_SERVER` | `import.meta.env.VITE_POINTER_SERVER` (or `%VITE_*%` in `index.html`) |
| Next.js | `NEXT_PUBLIC_` | `NEXT_PUBLIC_POINTER_SERVER` | `process.env.NEXT_PUBLIC_POINTER_SERVER` |
| Create React App / Webpack | `REACT_APP_` | `REACT_APP_POINTER_SERVER` | `process.env.REACT_APP_POINTER_SERVER` |
| Webpack (custom `DefinePlugin`) | your choice | `POINTER_SERVER` | whatever key you define |
| Angular | — (no runtime env) | n/a | a field in `src/environments/environment.ts` |
| Plain HTML / static | — (no env) | n/a | hardcode the attributes, or use [`embed.js`](#3-embed-in-an-apis-swagger-page-optional) |

Example for a **Vite** app — add to the app's `.env` and use a `%VITE_*%`-substituted loader in
`index.html`. **For another stack, swap the `VITE_` prefix for the row above and read the values the
way that stack exposes them** (`process.env.NEXT_PUBLIC_*`, `environment.ts`, etc.):

```bash
# apps/<app>/.env   (Vite shown — use your stack's prefix)
VITE_POINTER_ENABLED=true
VITE_POINTER_SERVER=https://api.pointer.moamen.work   # http://localhost:8090 for local
VITE_POINTER_PROJECT=my-app
```

```html
<script>
  if ('%VITE_POINTER_ENABLED%' === 'true' && '%VITE_POINTER_SERVER%'.indexOf('http') === 0) {
    var s = document.createElement('script');
    s.src = '%VITE_POINTER_SERVER%/pointer.js'; s.defer = true;
    document.head.appendChild(s);
    var el = document.createElement('pointer-feedback');
    el.setAttribute('project', '%VITE_POINTER_PROJECT%');
    el.setAttribute('server', '%VITE_POINTER_SERVER%');
    el.setAttribute('source-attr', 'data-component-source');
    document.body.appendChild(el);
  }
</script>
```

The `<pointer-feedback>` element only reads HTML attributes, so it's stack-agnostic — env vars
matter only for *how your build feeds those attributes*. On a stack with no client env vars (static
HTML, server-rendered docs), skip env wiring entirely and use the [`embed.js` loader](#3-embed-in-an-apis-swagger-page-optional).

### `<pointer-feedback>` attributes

| Attribute | Required | Default | Description |
|---|---|---|---|
| `project` | ✅ | — | Project key the feedback is partitioned under (e.g. `my-app`). |
| `server` |  | the `pointer.js` script's own origin | Pointer API base URL (e.g. `https://api.pointer.moamen.work`). |
| `environment` |  | `staging` | Tag stored on each comment: `local` \| `staging` \| `production`. |
| `source-attr` |  | `data-component-source` | DOM attribute that carries an element's source path (`file.tsx:line`) so applies jump straight to the code. |
| `launcher-position` |  | `bottom-end` | Corner of the collapsed launcher button: `top-start` \| `top-end` \| `bottom-start` \| `bottom-end`. |
| `screenshot` |  | enabled | Set `screenshot="false"` to remove the "Attach screenshot" option entirely. |

The widget starts as a small launcher button, captures an optional element screenshot per comment,
and supports **private** comments (visible only to their author) and **archived** status.

### Source mapping (so applies jump to the exact file)

Pointer records the **`source-attr`** of the clicked element (default `data-component-source`,
e.g. `src/components/Header.tsx:42`) so the apply step edits the **right file** instead of guessing.
Apps don't emit that attribute by default — it's produced by a **build plugin gated behind a dev/preview
flag**. For example, a Vite/React app stamps `data-component-source` when `VITE_DEBUG=true`:

```bash
# apps/<app>/.env  — enable in the envs where stakeholders give feedback (local/staging/preview)
VITE_DEBUG=true
```

**Enable that flag wherever feedback is collected.** Without it the widget still works, but applies
fall back to searching by the element's snapshot/classes — slower and less precise. (To verify:
inspect an element and confirm it carries `data-component-source`.)

### Theming (per project)

The widget's styles are CSS custom properties, so you can re-theme it from your **own** app's CSS —
the tokens pierce the Shadow DOM:

```css
pointer-feedback {
  --pf-primary: #0aa36e;
  --pf-radius-lg: 16px;
}
```

---

## 2. Get the AI skills

Pointer ships two **tool-agnostic, self-configuring** skills, served by the API. Fetched from your
Pointer server they arrive **pre-filled with that server's URL** — nothing to edit.

- **`pointer-init`** — adds the `<pointer-feedback>` widget to an app.
- **`pointer-feedback`** — lists / **applies** pending comments by editing the real source.

### Install (one command)

```bash
curl -fsSL https://api.pointer.moamen.work/install.sh | sh
```

Installs both skills into `.claude/skills/`. Then run **`/pointer-init`** in your AI tool to add the
widget. (Different layout? Pass a directory: `… | sh -s -- .cursor/rules`.)

<details>
<summary><b>Manual alternative</b> (no script — just two downloads)</summary>

```bash
POINTER=https://api.pointer.moamen.work    # http://localhost:8090 for local

curl -s --create-dirs "$POINTER/pointer-init.md" -o .claude/skills/pointer-init/SKILL.md       # add the widget
curl -s --create-dirs "$POINTER/skill.md"        -o .claude/skills/pointer-feedback/SKILL.md   # apply feedback
```

Only adding the widget? You just need the first line. Using another tool, save the same markdown
where it looks for skills/rules — e.g. **Cursor** `.cursor/rules/pointer-init.mdc`, **Windsurf**
`.windsurf/rules/`, **opencode** `.opencode/`.
</details>

### Applying feedback

Set the automation account credentials in your shell (an admin creates this account — any role works;
a `Developer` "automation" user is conventional). Keep them out of any committed/Vite-exposed file:

```bash
export POINTER_EMAIL="automation@pointer.local"
export POINTER_PASSWORD="…"
```

Then tell your AI tool **"apply pending pointer comments"** — it reads the `*POINTER_SERVER` /
`*POINTER_PROJECT` keys from the app's `.env` (matching whatever prefix the stack uses — `VITE_`,
`NEXT_PUBLIC_`, `REACT_APP_`, or none), logs in, fetches the `ReadyToApply` queue, applies each
item by `element.sourcePath`, and `PATCH`es it to `Applied` with an `appliedByLabel` for traceability.

---

## 3. Embed in an API's Swagger page (optional)

A Swagger UI is just an HTML page, so any API can show the feedback widget on its own docs — let
consumers comment directly on endpoints. The Pointer server hosts a self-configuring loader at
**`<pointer-server>/embed.js?project=<key>`** that injects `pointer.js` and mounts a configured
`<pointer-feedback>`. The API owner does two things.

**1. Config (`appsettings.json`)** — all settings live here, so they're per-environment overridable
(`appsettings.{Environment}.json` or `Pointer__*` env vars):

```json
"Pointer": {
  "Enabled": true,
  "Server": "http://localhost:8090",   // your deployed Pointer URL in staging/prod
  "Project": "",                        // blank → this app's name
  "Environment": "staging"
}
```

**2. Wire it (Swashbuckle, `Program.cs`)** — read the section once and drive **both** the embed and
the `/swagger` CSP from it:

```csharp
var p = app.Configuration.GetSection("Pointer");
var pEnabled = p.GetValue("Enabled", false);
var pServer  = (p["Server"] ?? "http://localhost:8090").TrimEnd('/');
var pProject = string.IsNullOrWhiteSpace(p["Project"]) ? app.Environment.ApplicationName : p["Project"]!;
var pEnv     = string.IsNullOrWhiteSpace(p["Environment"]) ? "staging" : p["Environment"]!;
var pAllow   = pEnabled ? $" {pServer}" : "";   // origins added to the Swagger CSP

app.UseSwaggerUI(c =>
{
    if (pEnabled)
        c.InjectJavascript($"{pServer}/embed.js?project={Uri.EscapeDataString(pProject)}&environment={Uri.EscapeDataString(pEnv)}");
});
```

**⚠️ CSP.** If the docs page sends a `Content-Security-Policy` (common, scoped to `/swagger`), the
cross-origin widget is blocked until the Pointer origin is allowlisted. Build that CSP with `pAllow`
so it follows the same config (and the hole closes when disabled):

```csharp
$"default-src 'self'; script-src 'self' 'unsafe-inline'{pAllow}; connect-src 'self'{pAllow}; " +
$"style-src 'self' 'unsafe-inline'{pAllow}; img-src 'self' data:{pAllow}; frame-ancestors 'none'"
```

Non-.NET docs (Scalar, Redoc, swagger-ui, static): add `<script src="<pointer-server>/embed.js?project=<key>"></script>`
and allowlist the Pointer origin in the page's CSP if it has one. The `pointer-init` skill's
**"API Swagger / OpenAPI docs page"** section automates all of this.

---

## Self-hosting & development

This repo is the Pointer backend (**.NET 8 + PostgreSQL**) plus the web-component source. To run your
own instance, build the `<pointer-feedback>` component, or deploy:

- **[docs/SELF_HOSTING.md](docs/SELF_HOSTING.md)** — architecture, local setup, admin/accounts,
  building the web component, and the design docs.
- **[DEPLOY.md](DEPLOY.md)** — production deploy (Docker Compose + Caddy) and how to ship changes.
- **[AGENTS.md](AGENTS.md)** / **[CLAUDE.md](CLAUDE.md)** — guidance for AI agents working in this repo.
