# Orval Migration Plan: admin-web

> Generate type-safe Angular services and models from the .NET API Swagger spec using Orval with `httpResource` mode.

## Decisions

| Decision | Choice |
|---|---|
| Orval retrieval mode | `httpResource` (signal-first GETs, HttpClient mutations) |
| Envelope handling | HTTP response interceptor (no custom mutator) |
| Swagger source | Download from running API → `openapi.json` → Orval |
| Migration scope | Full (replace all hand-written services) |
| Path alias | `@api/*` → `src/app/core/api/generated/*` |
| Tree-shaking | Guaranteed via `tags-split` + `provideIn: 'root'` + standalone resource functions |

## Steps — ALL COMPLETE

- [x] **Step 1: Backend — Add `[ProducesResponseType]` + `[Produces("application/json")]` attributes**
  - Added inner-type `[ProducesResponseType]` to all 6 controllers (Auth, Me, Admin/Users, Roles, Projects, Stats)
  - Added global `[Produces("application/json")]` filter to eliminate text/plain + text/json content types
  - This ensures Orval generates clean typed methods without `accept` overloads

- [x] **Step 2: Install Orval**
  - `npm install -D orval` → v8.19.0

- [x] **Step 3: Create `orval.config.ts`**
  - `client: 'angular'`, `retrievalClient: 'httpResource'`, `provideIn: 'root'`
  - `mode: 'tags-split'`, tag filter: Auth, Me, Users, Stats, Projects, Roles
  - `formatter: 'prettier'`, `clean: true`

- [x] **Step 4: Create generate-services script**
  - `scripts/generate-services.mjs`: downloads spec, saves openapi.json, runs orval
  - `npm run generate-services` registered

- [x] **Step 5: Rewrite auth interceptor → `apiInterceptor`**
  - Unified interceptor: base URL prepending + Bearer token + envelope unwrap + 401 redirect
  - Works for both `httpResource` (GETs) and `HttpClient` (POSTs/PATCHs)
  - `app.config.ts` updated to reference `apiInterceptor`

- [x] **Step 6: Add `@api/*` path alias**
  - `paths` in `tsconfig.json` (no `baseUrl` — TS 6.0 compatible)

- [x] **Step 7: Run generation**
  - API started locally with updated annotations
  - Spec downloaded (36.8 KB → 31.8 KB after JSON-only filter)
  - All response types verified: UserResponse, RoleResponse, ProjectResponse, StatsResponse, LoginResponse, MeResponse

- [x] **Step 8: Migrate domain services**
  - `AuthService`: uses generated `AuthService` (aliased as `ApiAuthService`) for `postApiAuthLogin()`
  - `PreferencesService`: uses generated `MeService` for `patchApiMePreferences()`
  - Both keep their domain logic (signals, localStorage, etc.)

- [x] **Step 9: Migrate feature components**
  - **ProjectsComponent**: `getApiAdminProjectsResource()` + `ProjectsService` for mutations
  - **RolesComponent**: `getApiAdminRolesResource()` + `RolesService` for mutations
  - **OverviewComponent**: `getApiAdminStatsResource()` + `getApiAdminUsersResource()` + `getApiAdminRolesResource()` + `UsersService` for approve/reject
  - **UsersComponent**: `getApiAdminUsersResource()` (filter-based) + `UsersService` + `RolesService` for all mutations
  - All components use `extractMessage()` for error handling
  - All components use signal-based loading state from resources

- [x] **Step 10: Delete old hand-written API layer**
  - Removed: `api.ts`, `models.ts`, `api.spec.ts`, `users.service.ts`, `roles.service.ts`, `projects.service.ts`, `stats.service.ts`

- [x] **Step 11: Create extract-message utility + update tests**
  - `core/api/extract-message.ts` utility
  - Updated `auth.interceptor.spec.ts` → tests `apiInterceptor` with envelope unwrap tests

- [x] **Step 12: Verify build**
  - `ng build` succeeds with 0 errors
  - Bundle output: each component lazy-loaded in its own chunk (tree-shaking confirmed)
  - `openapi.json` + `generated/` ready to commit
