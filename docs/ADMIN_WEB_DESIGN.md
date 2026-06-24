# Admin Web (Angular) — Design

**Status:** approved · **Date:** 2026-06-24

A standalone **Angular 22** SPA that replaces the build-free `wwwroot/admin` dashboard, consuming the
Pointer APIs. Lives in `pointer-api/admin-web/`. The existing static `/admin/` stays as a fallback.

## 1. Goal

Move the admin dashboard out of the .NET app into a proper Angular SPA:
- Decoupled from the API (own build/deploy), so the dashboard can grow independently.
- Feature parity with the current dashboard: **Overview/stats, Roles, Users, Projects**, login.
- Built on the latest Angular (22) with **standalone components, signals, new control flow, functional
  guards/interceptors**, and **Angular Material** components.

Non-goals (now): deployment wiring, SSR, removing the static dashboard (kept as a fallback).

## 2. Architecture

- **Angular 22** via Angular CLI in `pointer-api/admin-web/` — own `package.json`; `ng serve` on
  **:4200**, `ng build` → static bundle.
- **Angular Material** (+ CDK) for tables, cards, forms, dialogs, snackbars, nav.
- Calls the API at `environment.apiBase` (default `http://localhost:8090`). CORS is already open
  (`AllowAnyOrigin`) — no proxy required.
- State via **signals**; HTTP via functional `HttpInterceptor`; route protection via functional guard.

```
admin-web/src/app/
  core/
    auth/{auth.service.ts, auth.guard.ts, auth.interceptor.ts}
    api/{models.ts, api.ts, stats.service.ts, roles.service.ts, users.service.ts, projects.service.ts}
  features/
    login/login.component.ts
    shell/shell.component.ts            # toolbar + side-nav + <router-outlet>
    overview/overview.component.ts
    roles/roles.component.ts
    users/users.component.ts
    projects/projects.component.ts
  app.routes.ts · app.config.ts
  environments/environment.ts           # { apiBase }
```

## 3. Auth

- **`AuthService`** — `login(email,password)` → `POST /api/auth/login`; stores JWT + user in
  `localStorage`; exposes `user` and `isAdmin` as signals; `logout()` clears + routes to `/login`.
- **`authInterceptor`** (functional) — attaches `Authorization: Bearer <token>`; on **401** clears auth
  and redirects to `/login`.
- **`adminGuard`** (functional `CanActivateFn`) — allows only when authenticated **and** `user.isAdmin`;
  otherwise redirects to `/login`.
- **Routes:** `/login` (public); a protected **shell** wrapping `/overview` (default), `/roles`,
  `/users`, `/projects`, guarded by `adminGuard`.

## 4. API integration

- Typed **models** mirror the API DTOs: `LoginResponse`, `MeResponse`, `RoleResponse`,
  `UserResponse`, `ProjectResponse`, `StatsResponse` (Totals + ProjectStats), and the request shapes.
- **`api.ts`** wraps `HttpClient` and unwraps the `{ isSuccess, message, data }` envelope: returns
  `data` on success, throws an error carrying `message` otherwise.
- Feature services (`stats/roles/users/projects.service.ts`) expose typed methods over the endpoints:
  - `GET /api/admin/stats`
  - `GET/POST /api/admin/roles`, `PATCH /api/admin/roles/{id}`
  - `GET/POST /api/admin/users`, `PATCH /api/admin/users/{id}`
  - `GET/POST /api/admin/projects`, `PATCH /api/admin/projects/{id}`
  - `POST /api/auth/login`, `GET /api/auth/me`
- Errors surface via `MatSnackBar`. Hand-written services (small API; no codegen).

## 5. Screens (parity + polish)

- **Login** — Material card + reactive form; error message on failure; rejects non-admin accounts.
- **Shell** — `mat-toolbar` (brand · whoami `displayName · roleName` · sign out) + `mat-sidenav`
  (Overview, Roles, Users, Projects) + content `router-outlet`.
- **Overview** — 6 Material stat cards (Projects, Users, Comments, Open, Pending, Completed,
  colour-accented) + a sortable `mat-table` per-project (key, comments, open, pending, completed,
  status). Refresh button.
- **Roles** — `mat-table` (name + `system` chip, grants-admin `mat-slide-toggle`, status, actions
  rename/disable); add-role form (name + grants-admin); the `Admin` system role is read-only.
- **Users** — `mat-table` (email, name, role `mat-select` from the catalog, status, disable);
  add-user form (email, name, password, role).
- **Projects** — `mat-table` (key, name, status, disable); add-project form (key, name).
- Loading + empty states; confirm dialog before disabling; snackbar toasts.

## 6. Config & run

- `environment.ts` → `{ apiBase: 'http://localhost:8090' }` (prod env can point elsewhere).
- Run: `cd admin-web && npm install && npm start` → http://localhost:4200.
- README section documents this + the default admin login.

## 7. Verification

- `ng build` succeeds (no errors).
- Browser e2e (playwright): login → Overview renders stats → Roles add/rename → Users add + role
  change → Projects disable/enable → sign out. 0 console errors.
- Focused unit tests: `authInterceptor` attaches the token; `api.ts` unwraps the envelope / throws on
  `isSuccess:false`.

## 8. Out of scope

Deployment/hosting wiring, SSR, retiring the static `/admin/`, charts/time-series (could come later).
