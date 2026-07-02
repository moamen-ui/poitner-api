# Centralized Branding — Phase 1 spec (contract for all surfaces)

Goal: super-admin edits product **name / tagline / color / logos+icons / reference URLs** in one
place; landing, dashboards, and widget reflect it at runtime. No rebrand-by-code.

## Decisions (locked)
- Phase 1 surfaces: **dashboards (×3), landing, widget**. Emails + extension in-UI text = phase 2.
- Branding admin page in **all 3 dashboards** (super-admin only).
- "Domain" = **editable reference/link URLs** (app/demo/docs/landing). Actual serving domains stay
  infra (DNS/Caddy/build env) — NOT a runtime toggle.

## Data model
Text/config → existing settings store (`ISettingsService`, `AppSetting`), keys:
- `brand_product_name`   (default `Pointer`)
- `brand_tagline`        (default `Point at the UI. Ship it with AI.`)
- `brand_primary_color`  (default `#2563eb`)
- `brand_url_app`        (default `https://app.pointer.moamen.work`)
- `brand_url_demo`       (default `https://demo.pointer.moamen.work`)
- `brand_url_docs`       (default `https://github.com/moamen-ui/poitner-api#readme`)
- `brand_url_landing`    (default `https://pointer.moamen.work`)
- `brand_assets_version` (int, bumped on any asset upload/delete — cache-buster)

Assets → uploaded files in the **persistent uploads volume** under `uploads/branding/<kind>.<ext>`,
served **public** (not signed). Kinds + required/expected sizes:
| kind | purpose | expected |
|---|---|---|
| `logo` | wordmark (nav/header) | SVG or PNG, ~transparent, height ~40px |
| `iconSquare` | square mark / avatar | PNG 512×512 |
| `favicon` | browser tab | PNG 32×32 (also used 16) |
| `appleTouch` | iOS home screen | PNG 180×180 |
| `pwa192` | PWA icon | PNG 192×192 |
| `pwa512` | PWA icon | PNG 512×512 |

Uploads validated: content-type in {png,svg,webp,jpeg}, ≤ 1 MB, (dimension check best-effort).

## API
- `GET /api/branding` — **public**, `Cache-Control: no-cache` (see Caddy). Returns:
  ```json
  { "productName":"Pointer","tagline":"…","primaryColor":"#2563eb",
    "urls":{"app":"…","demo":"…","docs":"…","landing":"…"},
    "assets":{"logo":"https://api…/api/branding/asset/logo?v=3","iconSquare":null,
              "favicon":"…","appleTouch":null,"pwa192":null,"pwa512":null},
    "version":3 }
  ```
  Asset URL present only if uploaded; `null` → consumer uses its own bundled default.
- `GET /api/branding/asset/{kind}` — **public**, serves the file (or 404), long cache keyed by `?v`.
- `GET /api/admin/branding` — super-admin; same shape (for the editor).
- `PUT /api/admin/branding` — super-admin; body = { productName, tagline, primaryColor, urls{…} }.
- `POST /api/admin/branding/asset/{kind}` — super-admin, multipart `file`; stores, bumps version.
- `DELETE /api/admin/branding/asset/{kind}` — super-admin; removes upload (revert to default), bumps version.

## Consumers (Phase 1)
- **Dashboards (×3):** on app load, fetch `GET /api/branding` into a branding store → (a) set
  `document.title = "<productName> Admin"`, (b) swap favicon `<link rel=icon>` to `assets.favicon`
  (fallback bundled), (c) render `productName` + `logo` in the shell header, (d) use `primaryColor`
  where the theme reads a CSS var (optional). PLUS the **Branding admin page** (super-admin) with the
  text/url form + per-kind upload widgets (show expected dimensions) + live preview + save.
- **Landing:** client-side fetch (like the pricing section) → set `<title>`, favicon, nav/footer
  brand name + logo, and replace product-name text nodes (mark them `data-brand="name"`).
- **Widget (`pointer.js`):** fetch `GET /api/branding` during boot (alongside status catalog) → use
  `productName` in the login modal / toolbar title / any "Pointer" string; use `logo` if shown.

## Defaults / seeding
Settings default to the current values above (so nothing changes visually until edited). No assets
seeded — consumers keep their bundled favicons/logos until the super-admin uploads replacements.

## Out of scope (documented limits)
Serving-domain switch (infra), extension manifest name + toolbar icon + Web Store listing (static,
needs re-publish), emails (phase 2). Initial static `index.html` favicon/title flashes the bundled
brand for a moment before branding loads (acceptable).
