# Admin Preferences — i18n (AR/EN) + Dark Theme + Persistence — Design

**Status:** approved · **Date:** 2026-06-24

Add per-user **language (ar/en)** and **theme (light/dark)** to the Angular admin SPA, persisted in
the database via new API endpoints. Builds on the existing pointer-api backend + `admin-web/` SPA.

## 1. Goal

- Runtime **AR/EN** language switch (no reload) with **RTL** for Arabic.
- A **dark/light theme** toggle in the header.
- Each user's **(language, theme)** is **saved in the DB** and restored on next login/device.
- Resolution order for what's shown: **saved preference → browser language + system theme →
  hard fallback `en` / `dark`.**

## 2. Backend (.NET)

### Schema
- Add two **nullable** columns to the `User` entity: `Language` (`string?`, `"ar"`|`"en"`) and
  `Theme` (`string?`, `"light"`|`"dark"`). Null = never set (so the SPA falls back to browser/system).
- `UserMapping`: `language` (varchar, max 8) + `theme` (varchar, max 8), both nullable, snake_case.
- One EF migration `AddUserPreferences`. (Applied to the dev DB via the normal startup auto-migrate.)

### DTO
- Extend `MeResponse` with `string? Language` and `string? Theme` — so `/api/auth/login` and
  `/api/auth/me` already return the prefs (no extra startup call). `AuthService.MapToMeResponse`
  populates them; the login user query already loads the `User`.

### Endpoint
- `MeController` at `/api/me`, `[Authorize]` (any signed-in user; acts on the caller only):
  - `PATCH /api/me/preferences` — body `UpdatePreferencesRequest { string? Language; string? Theme; }`.
    Partial update (only non-null fields change). Returns the updated `MeResponse`.
- `IPreferencesService.UpdateAsync(Guid currentUserPublicId, UpdatePreferencesRequest)` →
  loads the user by `PublicId` (from `ICurrentUser.Id`), sets provided fields, saves, returns `MeResponse`.
- **Validation** (`UpdatePreferencesValidator`): if `Language` present it must be `ar`|`en`; if `Theme`
  present it must be `light`|`dark`. Both optional.

## 3. Frontend (Angular) — i18n with Transloco

- Add **Transloco** (`@jsverse/transloco`). Dictionaries `src/assets/i18n/en.json` + `ar.json` covering
  ALL UI strings: nav (Overview/Roles/Users/Projects), header (brand, sign out, user role label),
  Overview (card labels, table headers, Refresh, Projects Breakdown), Roles/Users/Projects (column
  headers, add forms, button labels, status Active/Disabled, system chip), login, and snackbar messages.
- Components reference keys via the Transloco pipe (`{{ 'nav.overview' | transloco }}`) / structural
  directive. Keys grouped by feature (`nav.*`, `header.*`, `overview.*`, `roles.*`, `users.*`,
  `projects.*`, `login.*`, `common.*`).
- **RTL:** switching to `ar` sets `document.documentElement.dir = 'rtl'` and `lang = 'ar'` (and `ltr`/`en`
  for English). Angular Material mirrors layout from `dir` automatically.

## 4. Frontend — dark theme

- `styles.scss`: keep the light CSS-variable palette as the default; add a `html.dark { … }` block that
  overrides the same variables for dark (`--app-bg`, `--header-bg`, `--sidebar-bg`, `--panel-bg`,
  `--border`, `--ink`, `--muted`; keep `--brand`). Set `color-scheme` per mode so Material's own
  surfaces follow. Dark palette (approx): app `#0f141b`, header `#131a23`, sidebar `#0c1116`,
  panel `#161d27`, border `#243042`, ink `#e6edf5`, muted `#94a3b8`.
- Toggling adds/removes the `dark` class on `<html>`.

## 5. Frontend — `PreferencesService` (the orchestrator)

- Signals `language: 'ar'|'en'` and `theme: 'light'|'dark'`.
- `apply()` — sets Transloco active lang, sets `dir`/`lang` on `<html>`, toggles the `dark` class.
- `init(saved?: { language?: string|null; theme?: string|null })` — resolves in order:
  **(1)** saved (from `MeResponse`), **(2)** browser (`navigator.language` starts with `ar` → `ar`,
  else `en`) + system (`matchMedia('(prefers-color-scheme: dark)')` → `dark`, else `light`),
  **(3)** fallback `en`/`dark`. Then `apply()`.
- `setLanguage(l)` / `setTheme(t)` — update signal + `apply()` immediately (optimistic) **and**
  `PATCH /api/me/preferences` to persist (best-effort; snackbar on failure but keep the local change).
- Wiring: `AuthService.login()` and the app bootstrap call `prefs.init(user)` once the `MeResponse` is
  available. A guest on `/login` gets browser/system-resolved prefs (toggles work, stored only locally
  until login; the server value wins after login).

## 6. Header controls

- Two icon buttons in the shell toolbar:
  - **Language**: a button showing the *other* language (e.g. shows "العربية" in EN mode), toggles ar⇄en.
  - **Theme**: a sun/moon `mat-icon` button toggling light⇄dark.
- Both call `PreferencesService`.

## 7. Out of scope

- No per-locale date/number formatting beyond Transloco/Angular defaults.
- Theme is light/dark only; "system" is used solely for first-visit detection, not a third stored value.
- No translation of backend `message` strings (kept English); the SPA shows them as-is.

## 8. Verification

- **Backend:** migration applies; `PATCH /api/me/preferences` round-trips and validates; login/me return
  `language`/`theme`.
- **Browser e2e:** toggle Arabic → UI strings translate + layout flips RTL; toggle dark → palette
  switches (header/sidebar/content all dark); reload and re-login → the saved prefs are restored from the
  DB; switching back works; 0 console errors.
- Existing unit tests still pass; add a backend test for the preferences validator (ar/en, light/dark).
