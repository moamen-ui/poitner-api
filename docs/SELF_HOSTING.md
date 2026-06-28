# Self-hosting & development

This is the maintainer guide for **Pointer API** — running your own instance, the architecture, and
building the web component. For *consuming* Pointer (adding the widget, getting the skills), see the
[README](../README.md). For production deploy, see [DEPLOY.md](../DEPLOY.md).

**Pointer API** is a standalone **.NET 8** backend for [Pointer](../README.md): PostgreSQL storage,
every comment behind a real account (JWT), and it serves the browser web component + the AI skills.
It follows mainstream .NET conventions (Clean Architecture, EF Core, `Result<T>`, Scrutor
auto-registration, FluentValidation, CSharpier, Docker + `just`).

## Key decisions

| Decision | Choice |
|---|---|
| Where the backend lives | **Standalone .NET service** (own repo/DB/deploy) |
| Who can comment | **Only authenticated accounts** — identity comes from the token, never self-reported |
| Auth system (for now) | **Local email/password accounts** (BCrypt), JWT issued by this service. **No Keycloak yet** — designed to swap to SSO later |
| Account provisioning | **Admin UI** (dashboard) to create/disable users and assign roles; stakeholders can self-sign-up (admin approves) |
| Roles | **Data-driven catalog** managed in the dashboard. Authorization is capability-based via each role's `GrantsAdmin`; the `Admin` role is protected. Seeded: Admin, Developer, PM, Tester, Client |
| Projects | **Self-register** — any app wiring `VITE_POINTER_PROJECT=<key>` auto-appears on first load/comment |
| AI apply-tool auth | Just another local account (a `Developer` "automation" user); the skill logs in with env creds |
| Data shape | Multi-project; rich element capture stored as a single Postgres `jsonb` column |

## Architecture

```
API/            controllers, JWT auth, static assets (skill.md, /admin) + built pointer.{js,css}, seeder
Application/    services (Result + Scrutor), DTOs, FluentValidation, ICurrentUser/ITokenService seams
Domain/         entities (BaseEntity audit), enums (Role, CommentStatus, EnvironmentTag), ElementCapture
Infrastructure/ EF Core + Postgres (snake_case), Repository/UnitOfWork, BCrypt hasher, JWT service
web-component/  <pointer-feedback> source (TypeScript + SCSS) → builds to API/wwwroot/pointer.{js,css}
```

Identity is read only through `ICurrentUser`; tokens are issued only through `ITokenService` — so a
future swap to Keycloak is contained to those two seams.

## Local setup

### Prerequisites

- **Docker** + Docker Compose — runs Postgres and (Option A) the API.
- **.NET 8 SDK** — only if you run the API outside Docker (Option B).
- **`just`** (optional) — convenience commands; each has a raw equivalent below.

```bash
docker --version && docker compose version   # required
dotnet --version                             # Option B only — expect 8.x
```

### 1. Clone & configure

```bash
git clone https://github.com/moamen-ui/poitner-api pointer-api && cd pointer-api
cp .env.example .env          # set JWT__SigningKey / ADMIN__* (change before any real deploy)
```

### 2. Run the API + database

```bash
# Option A — full stack in Docker (Postgres + API, hot reload)   ← recommended
just up                       # or: docker compose up -d
# API on http://localhost:8090, Postgres on :5433

# Option B — Postgres in Docker, API run locally (needs the .NET 8 SDK)
docker compose up -d db
ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer;Username=pointer;Password=pointer" \
ASPNETCORE_URLS="http://0.0.0.0:8090" dotnet run --project API
```

On boot the API auto-migrates (`DBMigrationEnabled=true`) and **seeds one admin** from
`ADMIN__EMAIL` / `ADMIN__PASSWORD`.

### 3. Verify

| What | URL |
|---|---|
| Swagger | http://localhost:8090/swagger |
| Admin UI (fallback) | http://localhost:8090/admin/ |
| Web component | http://localhost:8090/pointer.js |
| Apply skill (`pointer-feedback`) | http://localhost:8090/skill.md |
| Init skill (`pointer-init`) | http://localhost:8090/pointer-init.md |

## Default admin login

On first boot the API seeds one admin from `ADMIN__EMAIL` / `ADMIN__PASSWORD` in `.env`:

| Field | Value |
|---|---|
| Email | `admin@pointer.local` |
| Password | `ChangeMe123!` |

⚠️ **Change `ADMIN__PASSWORD` before deploying anywhere real** (it's only a local default).

## Provision accounts + a project

Stakeholders **self-sign-up** from the widget (name, email, password, a non-admin role); the request
lands in a **pending** queue an admin approves (or rejects) from the dashboard. Admins can also
create accounts directly:

1. Sign in to the dashboard as the seeded admin.
2. **Roles →** (optional) add custom roles or rename/disable defaults. Tick *Grants admin* to let a
   role into the dashboard. The `Admin` role is protected.
3. **Projects →** projects **self-register** on first connect — you usually don't add them here, but
   you can pre-create one matching `VITE_POINTER_PROJECT`, or rename/disable any.
4. **Users →** add stakeholder accounts and one **automation** account for the AI apply-tool.

## Building the web component

The served `API/wwwroot/pointer.js` and `pointer.css` are **build artifacts** — don't edit them
directly. Source is a TypeScript + SCSS project in [`web-component/`](../web-component/) (esbuild +
sass; dependency-free output).

```bash
cd web-component
npm install            # first time only
npm run build          # → ../API/wwwroot/pointer.{js,css}
npm run watch          # rebuild on change
npm run typecheck      # tsc --noEmit
```

- Source: `web-component/src/` (`element.ts`, `auth-ui.ts`, `capture.ts`, `templates.ts`, …) +
  `web-component/src/styles/` (SCSS partials + `_variables.scss`).
- **Theming:** styles use `var(--pf-*, default)` tokens (defaults in `_variables.scss`); consumers
  override per project from their own CSS.
- After editing, run `npm run build` and commit the regenerated `wwwroot/pointer.*` (the Docker image
  bakes in `wwwroot`).

## Commands (`just`)

```bash
just up         # docker compose up (API + Postgres)
just down       # stop
just build      # dotnet build
just test       # dotnet test
just fmt        # CSharpier format
just migrate <Name>   # add EF migration
just db-update  # apply migrations
just psql       # psql into the db
```

## Admin dashboard (separate repo)

The dashboard (Overview/stats, Roles, Users, Projects, signup approvals) is a standalone **Angular
22** SPA in [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard). The build-free
`/admin/` page served by the .NET app is kept as a zero-dependency fallback.

- Talks to this API at its `apiBase` (dev → `http://localhost:8090`); CORS is open server-side. Its
  `production` build bakes in the deployed API host via `fileReplacements`.
- Its API layer is **auto-generated from this API's Swagger via Orval** — after you change
  endpoints/DTOs, regenerate it **in that repo** (`npm run generate-services`, API up). See that
  repo's `docs/skills/orval-codegen/SKILL.md`.
- **Language + theme:** AR/EN (Arabic flips to RTL) and light/dark, saved per-user in the DB
  (`PATCH /api/me/preferences`).
- In production it's served as static files by Caddy at `app.pointer.moamen.work` — see
  [DEPLOY.md](../DEPLOY.md).

## Deploy

Production runs on a VM (Docker Compose: Postgres + API + Caddy with auto-TLS). Full setup, DNS,
config files, and the push-then-pull update flow are in **[DEPLOY.md](../DEPLOY.md)**. Live: API
`https://api.pointer.moamen.work`, dashboard `https://app.pointer.moamen.work`.

## Design docs

- [`docs/DESIGN.md`](DESIGN.md) — full architecture, auth, data model, endpoints, web-component & apply flow.
- [`docs/ADMIN_WEB_DESIGN.md`](ADMIN_WEB_DESIGN.md) — the Angular admin SPA design.
- [`docs/ADMIN_PREFS_I18N_DESIGN.md`](ADMIN_PREFS_I18N_DESIGN.md) — preferences / i18n / theme design.
- [`docs/PLAN.md`](PLAN.md) — phased implementation plan.
- [`docs/TASKS.md`](TASKS.md) — living implementation tracker.

## Status

✅ **Implemented, verified, and deployed** (Phases 0–9). Backend, auth, admin UI, web component, and
AI skill are working; full API + browser e2e pass. Remaining: choose the final product name and
(later) the optional Keycloak/SSO swap.
