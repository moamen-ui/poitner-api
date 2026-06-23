# Pointer API — Design

**Status:** proposed (awaiting review) · **Date:** 2026-06-23

This document is the source of truth for *what* we're building and *why*. The phased work
breakdown with live statuses lives in [`TASKS.md`](TASKS.md).

---

## 1. Goal

Replace Pointer's zero-dependency Node server (`../Pointer/comments-skill/server.js`) with a real
.NET backend that:

1. Stores comments in **PostgreSQL** instead of flat JSON files.
2. Requires a **real account** for every comment — author identity is taken from the auth token,
   never self-reported (kills the anonymous problem).
3. Keeps Pointer's existing capabilities: **multi-project**, rich element capture, the
   **AI fetch → apply → mark-applied** workflow, and serving `pointer.js` + `skill.md`.
4. Adds a **minimal admin UI** to provision accounts and assign roles.

Non-goal (for now): Keycloak/SSO integration. We use **local accounts**, but structure the auth
layer so SSO can be dropped in later without schema or endpoint churn.

---

## 2. Architecture

A standalone Clean-Architecture solution that mirrors `tuwaiq-clubs-api-dotnet-revamp`:

```
pointer-api/
├── API/              # Presentation: controllers, JWT middleware, static admin UI,
│                     #   serves /pointer.js and /skill.md
├── Application/      # Services (Result pattern, Scrutor auto-reg by "Service" suffix),
│                     #   DTOs, FluentValidation validators
├── Domain/           # Entities (BaseEntity audit fields), enums (Role, CommentStatus, Environment)
├── Infrastructure/   # EF Core + PostgreSQL, IEntityTypeConfiguration mappings (snake_case),
│                     #   Repository/UnitOfWork, BCrypt password hasher, JWT token service
├── Dockerfile
├── docker-compose.yaml   # Postgres + API
└── justfile              # up / down / build / migrate / db-update / fmt  (same DX as clubs API)
```

### Conventions inherited from the clubs API
- **`Result` / `Result<T>`** return type from every service method — no exceptions for flow control.
- **Soft deletes** — `DeletedAt`/`DeletedBy`; every query filters `DeletedAt == null`.
- **Audit fields** auto-populated in `SaveChangesAsync` from the current user's `sub` claim.
- **Scrutor** auto-registers classes ending in `Service`; all scoped lifetime.
- **FluentValidation** with localized message keys.
- **snake_case** DB columns; file-scoped namespaces; nullable enabled; CSharpier formatting.

### Deliberate difference: local auth
The clubs API validates a Keycloak JWT. This service **issues its own JWT** (HS256, symmetric
signing key from env). The `sub` claim is a Pointer `User.Id` (`Guid`, matching `BaseEntity`'s
audit field types). All identity reads go through one `ICurrentUser` abstraction and one
`ITokenService`, so a future swap to Keycloak is contained to those two seams.

---

## 3. Authentication & Authorization

### Accounts (local)
- Fields: `Email`, `PasswordHash` (**BCrypt**), `DisplayName`, `Role`, `IsActive`, + audit.
- Provisioned by an admin via the **minimal admin UI** (Section 8).
- Passwords are never stored in plaintext; the seed/admin path hashes on write.

### Roles (data-driven catalog — managed in the dashboard)
Roles are rows in a `roles` table, **not** a code enum, so admins can add/rename/disable them at
runtime. The only capability that matters for authorization is **`GrantsAdmin`**:

| Field | Meaning |
|---|---|
| `Name` | Display label (Developer / PM / Tester / Client / Designer / …) |
| `GrantsAdmin` | If true, holders can access admin endpoints + the dashboard |
| `IsSystem` | Seeded + protected (the `Admin` role) — cannot be renamed, disabled, or have `GrantsAdmin` changed |
| `IsActive` | Disabled roles can't be assigned to new users |

Seeded defaults: **Admin** (`GrantsAdmin`, `IsSystem`), Developer, PM, Tester, Client.

Authorization is **capability-based, not name-based**: admin endpoints use a policy
(`[Authorize(Policy = "Admin")]`) that requires the JWT `is_admin` claim — so renaming or adding
roles never weakens security. The JWT carries `role` (name), `role_id`, and `is_admin`.
`User.Role` is a FK (`role_id`) to the catalog. Roles CRUD lives at `/api/admin/roles`.

### Login flow
- `POST /api/auth/login` `{ email, password }` → validates against BCrypt hash + `IsActive`
  → returns a signed JWT (`sub`, `email`, `name`, `role`, `exp`).
- Clients send `Authorization: Bearer <jwt>` on all write/queue endpoints.
- Token lifetime: configurable (default 12h) — long enough that stakeholders aren't re-prompted
  mid-review; short enough to be safe. Refresh is out of scope for v1 (re-login on expiry).

### Designed-for-SSO-swap seam
- `ITokenService` (issue/validate) and `ICurrentUser` (read `sub`/role from `HttpContext`) are the
  only places that know auth is local. Swapping to Keycloak later = new `ITokenService` validator
  + JWT bearer config; entities, services, and endpoints are untouched.

---

## 4. Data Model

```
User      Guid Id, Email (unique), PasswordHash, DisplayName, Role, IsActive, + audit
Project   int Id, Key (unique, e.g. "tuwaiq-clubs"), Name, IsActive, + audit
Comment   int Id, ProjectId → Project, Environment,
          Status (Open | ReadyToApply | Applied),
          AuthorId → User,                 # verified identity — NOT self-reported
          Body (text),
          Element (jsonb):                  # the full capture, consumed whole by the AI
            { selector, snapshot, classes, computedStyles,
              appliedCssRules, sourcePath, parentInfo },
          AppliedAt?, AppliedBy? → User, AppliedByLabel?,   # who/when applied
          + audit + soft-delete
Reply     int Id, CommentId → Comment, AuthorId → User, Body (text), + audit
```

### Notes
- **Element capture is a single `jsonb` column.** The AI consumes the whole blob to resolve source
  and apply CSS changes — normalizing selectors/CSS rules into tables adds cost with no benefit.
- **`project` stays first-class** so the service remains multi-project (clubs today, other apps
  later). Projects **self-register**: the first time a developer's app (with `VITE_POINTER_PROJECT=<key>`)
  loads or comments, `ProjectService.EnsureAsync` lazily creates the project (active, name = key), so it
  appears in the dashboard automatically. An admin can rename or disable it; a disabled project blocks
  further comments. (Admins can also create projects explicitly via `/api/admin/projects`.)
- **`Status`** preserves the existing "Ready to Apply" gate. `Applied` records `AppliedAt` +
  `AppliedBy` (the automation account) + `AppliedByLabel` (optional free-text, e.g. the dev's
  `git config user.email`) for human traceability without requiring a human login.
- `AuthorId` replaces the old `{ author, stakeholder }` free-text fields — identity + role now
  come from the account.

---

## 5. API Endpoints

All under `/api`. Lowercase routes. `Bearer` required unless noted.

### Auth
- `POST /api/auth/login` — `{email, password}` → `{ token, user }`. (public)
- `GET  /api/auth/me` — current account from token.

### Comments (the web component uses these)
- `POST /api/projects/{key}/comments` — create a comment (body + element capture + environment).
  Author = token `sub`.
- `GET  /api/projects/{key}/comments?status=&environment=` — list (sidebar/pins).
- `PATCH /api/comments/{id}` — update status (e.g. → `ReadyToApply`), or apply (→ `Applied`).
- `POST /api/comments/{id}/replies` — add a reply.
- `DELETE /api/comments/{id}` — soft delete (author or Admin).

### AI apply-tool (Developer-role automation account)
- `GET  /api/projects/{key}/comments?status=readyToApply` — the pending, self-contained queue.
- `PATCH /api/comments/{id}` `{ status: "Applied", reply, appliedByLabel }` — mark applied + reply.

### Admin (roles that grant admin)
- `GET/POST /api/admin/roles`, `PATCH /api/admin/roles/{id}` (add / rename / disable / toggle GrantsAdmin).
- `GET/POST /api/admin/users`, `PATCH /api/admin/users/{id}` (set roleId / disable).
- `GET/POST /api/admin/projects`, `PATCH /api/admin/projects/{id}`.

### Static
- `GET /pointer.js` — the web component (served from this API, like the Node server did).
- `GET /skill.md` — the AI skill (updated for the login + new endpoints).

---

## 6. Web Component (`pointer.js`) changes

The current component shows a **name + role modal** and writes anonymous comments. Changes:

1. Replace the identity modal with an **email + password login modal** → `POST /api/auth/login`
   → store JWT in `localStorage` (`pointer_token`).
2. Send `Authorization: Bearer <token>` on every write.
3. **Author + role come from the token**, not user input — the role/stakeholder dropdown is removed.
4. On `401` (expired/invalid), clear the token and re-show the login modal.
5. Everything else (element capture: selector, snapshot, classes, computed styles, applied CSS
   rules, source path; pins; sidebar) is **unchanged** — only the identity/transport layer moves.

The host-app integration in `tuwaiq-clubs/index.html` stays the same shape (env-gated inline
loader); only `VITE_POINTER_SERVER` points at the new API.

---

## 7. AI fetch → apply workflow + `skill.md`

Today the skill `curl`s the server with no auth. Updated flow:

1. **Login once:** the skill reads `POINTER_EMAIL` / `POINTER_PASSWORD` (a `Developer`-role
   automation account you create) from env, calls `POST /api/auth/login`, caches the JWT.
2. **Fetch:** `GET /api/projects/{key}/comments?status=readyToApply` with the bearer token →
   the self-contained queue (each item carries its element `jsonb`, so no second lookup).
3. **Apply:** resolve source via `element.sourcePath` (else codebase search), edit the file.
4. **Mark applied:** `PATCH /api/comments/{id}` `{status:"Applied", reply, appliedByLabel}`.

`appliedByLabel` defaults to the dev's `git config user.email` so applies are human-traceable even
though the JWT identity is the automation account.

---

## 8. Admin UI (minimal)

A small, **build-free static page** served by the API (vanilla HTML/JS, no SPA toolchain — matches
Pointer's zero-build ethos). Pages:

- **Login** (admin account).
- **Roles:** table + "add role" (name, grants-admin) + rename/disable + grants-admin toggle (the `Admin`
  system role is protected). User role dropdowns are populated from this catalog.
- **Users:** table + "add user" (email, password, display name, role) + disable toggle + role change.
- **Projects:** list (incl. self-registered ones) + add (key, name) + disable.

It calls the same `/api/admin/*` endpoints with the admin's JWT. Kept intentionally tiny.

---

## 9. Host-app (clubs) integration delta

Minimal change in `tuwaiq-academy-mono-spa/apps/tuwaiq-clubs`:
- `.env`: `VITE_POINTER_SERVER` → new API URL.
- `index.html`: unchanged loader shape (the component now self-manages login).

The `data-component-source` stamping (the `VITE_DEBUG` Babel plugin) is untouched — the new API
stores `sourcePath` exactly as before.

---

## 10. Open items / resolutions

- **Repo/folder name** — still provisional `pointer-api`; rename once the product name is chosen
  (candidates: Talmeeh / Ishara / Daleel). _Open._
- **JWT lifetime** — **resolved:** 12h, no refresh token in v1 (re-login on expiry).
- **Admin UI** — **resolved:** build-free static page served by the API (`/admin/`). Implemented.
- **Password hashing** — **resolved:** BCrypt (work factor 11). Implemented + unit-tested.
- **Deploy + Keycloak/SSO swap** — _open, deferred._ Identity is isolated behind `ICurrentUser` /
  `ITokenService` so the swap won't touch entities, services, or endpoints.

### Implementation deviations (decided during build)

- **`User.PublicId` (Guid)** is the JWT `sub` and the value stored in audit fields (`CreatedBy` etc.),
  since `User.Id` is an int but `BaseEntity` audit columns are `Guid`. `public_id` is unique.
- **Roles are a data-driven catalog** (table + `/api/admin/roles`), not a code enum. Authorization is
  capability-based: JWT emits `is_admin` (from the role's `GrantsAdmin`), `role` (name), `role_id`;
  admin endpoints use `[Authorize(Policy = "Admin")]` requiring `is_admin`. The `Admin` role is
  `IsSystem` (immutable). _(Supersedes the earlier role-as-int note.)_
- **List/queue items are self-contained** — `CommentListItemDto` carries `Element` + `Replies` so the
  web component (pins) and the AI queue need no second request (see §7).
