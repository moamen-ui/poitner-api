# Rebranding the browser extension

Most of the product brand is **runtime-driven** from the super-admin Branding page
(`GET /api/branding`) and needs **no extension change**:

- The **popup** fetches `/api/branding` and shows the current product name in its heading + copy.
- The **injected widget** (`pointer.js`, loaded live from the server) already reflects the branded
  name in its UI.

The following are **static** parts of a Chrome/Chromium MV3 extension — they are baked into the
package and/or the Web Store listing, so a rebrand here requires editing files + a **rebuild and
re-publish** (there is no runtime API for them):

## 1. Manifest (`extension/manifest.json`)
Edit and rebuild (`npm run build`):
- `name` — the extension name shown in `chrome://extensions`, the toolbar tooltip, and the Web Store.
- `description` — Web Store + extensions page.
- `action.default_title` — the toolbar icon hover tooltip.

## 2. Static popup/options chrome
- `extension/popup.html` `<h1>` — a fallback; the popup overwrites it at runtime from `/api/branding`,
  but update the literal so there's no flash of the old name before the fetch resolves.
- `extension/options.html` — `<title>`, `<h1>`, and the "Default … server" labels are static; edit them.

## 3. Icons (toolbar + Web Store)
MV3 icons are static files referenced from the manifest. Add PNGs and reference them:
```jsonc
// manifest.json
"action": { "default_popup": "popup.html", "default_title": "<Name> Feedback",
            "default_icon": { "16": "icons/16.png", "32": "icons/32.png" } },
"icons": { "16": "icons/16.png", "48": "icons/48.png", "128": "icons/128.png" }
```
Place the PNGs under `extension/icons/` and ensure `build.mjs` copies them into `dist/` (add to its
static-copy list if not already). Required sizes: **16, 32, 48, 128**. The Web Store listing also
needs a **128×128** store icon + screenshots (uploaded in the Developer Dashboard, not in the repo).
Tip: reuse the icons uploaded to the Branding page (`pwa192`/`iconSquare`) downscaled to these sizes.

## 4. Rebuild + reload / re-publish
```bash
cd extension && npm run build          # → dist/
# Local test: chrome://extensions → reload the unpacked extension (⟳)
# Publish: zip dist/ and upload a new version in the Chrome Web Store Developer Dashboard
#          (the store name/icon/screenshots are edited in the listing, separately from the zip).
```

## Summary
| Brand element | Where | How to change |
|---|---|---|
| Popup heading + copy | runtime | Branding page → auto |
| Injected widget UI | runtime | Branding page → auto |
| Extension name / description / toolbar tooltip | `manifest.json` | edit + rebuild + re-publish |
| Toolbar + package icons | `manifest.json` + `icons/*.png` | add files + rebuild + re-publish |
| Web Store name / icon / screenshots | Web Store listing | edit in Developer Dashboard |
