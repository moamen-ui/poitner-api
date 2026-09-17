# Pointer Feedback — Browser Extension (Chrome/Chromium, MV3)

An **alternative delivery channel** for [Pointer](../README.md): inject the Pointer
feedback widget into **any** web page — including sites you don't control and can't add the
`widget.js` loader to — and **log in once** instead of per-site. The embedded `<pointer-feedback>`
widget remains the base product; this extension is a way to *carry it anywhere*.

## How it relates to the widget

The extension does **not** reimplement the UI. On activation it loads the **live `widget.js`
from your Pointer server**, so any widget update you deploy shows up in the extension automatically
— **no extension re-release needed**. Only changes to the extension *shell* (popup, background,
manifest) require rebuilding/re-publishing.

The base widget exposes a small injection seam (added in `web-component/src`):
- `window.__POINTER_CONFIG__` — `{server, project, environment, token, user}`; the extension sets
  this so the widget starts already authenticated and targeting the chosen project.
- `window.__POINTER_FETCH__` — a transport the widget routes all API calls through; the extension
  points it at its background service worker.

## Architecture

```
popup (login + per-domain project picker + activate)
      │  chrome.runtime
      ▼
background service worker
  • holds the JWT in chrome.storage.session  (never enters the page)
  • API proxy: every widget request is re-issued here with the real token —
    bypasses the page's connect-src CSP
  • activation: adds a per-tab declarativeNetRequest rule that strips the page's
    CSP header (so the remote widget.js/css can load), reloads the tab, then
    injects the content bridge (ISOLATED) + config/loader (MAIN world)
      ▲  window.postMessage
      │
content-bridge.js (ISOLATED)  ⇄  inject-main (MAIN world: __POINTER_CONFIG__ +
                                   __POINTER_FETCH__ + loads widget.js)
```

Security note: the real JWT stays in the background worker — the page only ever sees a placeholder
token (`__pointer_via_proxy__`); the background swaps in the real token on each request. The CSP
strip is scoped to the single tab you explicitly activate and is removed on deactivate / tab close.

## Install (reviewers)

The extension is published on the **Chrome Web Store**. The listing URL is a server setting — the
super admin sets it in the dashboard under **Settings → Extension → Chrome Web Store URL** — and every
surface reads it live from `GET /api/branding` (`extension.storeUrl`), so a new listing URL never
requires a code change or a CLI release:

- the landing page's "Add to Chrome" button;
- the dashboard **Install guide → Chrome extension** method;
- `npx pointer-feedback init` when the developer picks **"Chrome extension only"** (`--delivery extension`)
  — the CLI then skips code injection and prints the install steps.

Reviewer steps: install from the store link → extension **Options** → set the server URL → sign in
with your Pointer account → open the app, click the toolbar icon, pick the project, **Activate**.

## Develop

```bash
cd extension
npm install              # Node >= 22 (repo uses node@26)
npm run build            # → extension/dist
# Chrome: chrome://extensions → Developer mode → Load unpacked → select extension/dist
npm run watch            # rebuild on change (reload the extension after shell changes)
```

Set the Pointer server in the extension **Options** (default `https://api.pointer.moamen.work`).

## Manual verification (E2E)

1. **Auth once** — click the toolbar icon → sign in with a Pointer account. The popup should show
   your name; the token lives in `chrome.storage.session` (never in any page).
2. **Plain site** — open e.g. `https://example.com`, set a project key, **Activate**. The tab
   reloads once and the Pointer launcher appears, already authenticated (no login prompt). Leave a
   comment → it appears in the dashboard under that project (self-registers).
3. **Strict-CSP site** — repeat on a site that sends a `Content-Security-Policy` (e.g. GitHub). The
   widget still loads (CSP stripped for the tab) and the comment POST is carried by the **background
   worker** (visible as a request from the service worker, not the page).
4. **Per-domain memory** — reopen the popup on the same domain → the project is pre-filled.
5. **Widget auto-update** — deploy a widget change to the server, reload the activated tab → the
   change appears with no extension rebuild.
6. **Deactivate** — Deactivate on the tab → it reloads with its original CSP restored.

## Publish an update to the Chrome Web Store

Only shell changes (popup, background, options, manifest, icons) need a release — widget changes
ship through the server (see above).

```bash
cd extension
# 1. bump "version" in manifest.json — the store only accepts a version higher than the published one
npm ci && npm run typecheck && npm run build          # → extension/dist (gitignored)
V=$(node -p "require('./manifest.json').version")
(cd dist && zip -qr "../pointer-ext-v$V.zip" . -x '.DS_Store')   # → extension/pointer-ext-v<V>.zip (gitignored)
```

Test the exact bundle first: `chrome://extensions` → Developer mode → **Load unpacked** →
`extension/dist` (or drag the zip). Then upload `pointer-ext-v<V>.zip` in the Chrome Web Store
Developer Dashboard. If the listing URL changes, update **Settings → Extension → Chrome Web Store
URL** in the dashboard; nothing else needs to change.

Listing notes kept from the first review: justify the `declarativeNetRequest` CSP-header removal
(widget must load on CSP'd pages, only on user-activated tabs) and the remote `widget.js` load. If a
review ever rejects remote code, the fallback is to **bundle** `widget.js` and inject it via
`executeScript({world:'MAIN', files:[...]})` at the cost of pinning the widget to extension releases.

## Not in this MVP

Firefox packaging (planned via `webextension-polyfill`), `data-component-source` mapping on
third-party pages (applies fall back to snapshot/selector), and popup-driven re-auth on 401 (the
widget currently falls back to its own login modal).
