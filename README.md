# Pointer API (provisional name)

A standalone **.NET 8** backend that replaces the original zero-dependency Node server for
[Pointer](../Pointer) — the element-level feedback tool. It moves Pointer's file-based JSON
storage to **PostgreSQL**, secures every comment behind a **real account** (no more anonymous,
self-reported names), and serves the browser web component + the AI apply skill.

It is built as its own service (own repo, own DB, own deploy) but **mirrors the conventions of
the `tuwaiq-clubs-api-dotnet-revamp` codebase** (Clean Architecture, EF Core, `Result<T>`,
Scrutor auto-registration, FluentValidation, CSharpier, Docker + `just`) so it feels native to
the team.

## Why this exists

The original Pointer server (`../Pointer/comments-skill/server.js`) is great for solo/local use,
but comments are **anonymous** (the author types their own name), storage is **flat JSON files**
with no locking, and there is **no auth**. This project fixes those three things while keeping
Pointer's multi-project, AI-apply workflow.

## Key decisions (locked during brainstorming)

| Decision | Choice |
|---|---|
| Where the backend lives | **Standalone .NET service** (own repo/DB/deploy), patterns cloned from clubs API |
| Who can comment | **Only authenticated accounts** — identity comes from the token, never self-reported |
| Auth system (for now) | **Local email/password accounts** (BCrypt), JWT issued by this service. **No Keycloak yet** — designed to swap to SSO later |
| Account provisioning | **Minimal admin UI** to create/disable users and assign roles |
| Roles | **Data-driven catalog** managed in the dashboard (add/rename/disable). Authorization is capability-based via each role's `GrantsAdmin`; the `Admin` role is protected. Seeded: Admin, Developer, PM, Tester, Client |
| Projects | **Self-register** — any app wiring `VITE_POINTER_PROJECT=<key>` auto-appears in the dashboard on first load/comment |
| AI apply-tool auth | Just another local account (a `Developer` "automation" user); the skill logs in with env creds |
| Data shape | Multi-project; rich element capture stored as a single Postgres `jsonb` column |

## Architecture

```
API/            controllers, JWT auth, static assets (pointer.js, skill.md, /admin), seeder
Application/    services (Result + Scrutor), DTOs, FluentValidation, ICurrentUser/ITokenService seams
Domain/         entities (BaseEntity audit), enums (Role, CommentStatus, EnvironmentTag), ElementCapture
Infrastructure/ EF Core + Postgres (snake_case), Repository/UnitOfWork, BCrypt hasher, JWT service
```

Identity is read only through `ICurrentUser`; tokens are issued only through `ITokenService` — so a
future swap to Keycloak is contained to those two seams.

## Installation

### Prerequisites

- **Docker** + Docker Compose — runs Postgres (and, optionally, the API).
- **.NET 8 SDK** — only if you want to run the API outside Docker.
- **Node ≥ 22.22 + npm** — only for the Angular admin dashboard (`admin-web/`).
- **`just`** (optional) — convenience commands; each recipe has a raw equivalent shown below.

### 1. Clone & configure

```bash
git clone <repo-url> pointer-api && cd pointer-api
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
`ADMIN__EMAIL` / `ADMIN__PASSWORD` (see [Default admin login](#default-admin-login)).

### 3. Admin dashboard (Angular — optional)

```bash
cd admin-web
npm install
npm start                     # → http://localhost:4200   (needs Node ≥ 22.22)
```

Sign in with the seeded admin; details in [Admin Web (Angular)](#admin-web-angular).

### 4. Verify

Open the URLs below and sign in at `/admin/` with the seeded admin. To add the feedback widget to
an app, see [Integrate an app](#integrate-an-app-two-line-loader); to wire the AI apply-tool, see
[Install the AI apply skill](#install-the-ai-apply-skill-in-a-consuming-repo).

| What | URL |
|---|---|
| Swagger | http://localhost:8090/swagger |
| Admin UI | http://localhost:8090/admin/ |
| Web component | http://localhost:8090/pointer.js |
| Apply skill (`pointer-feedback`) | http://localhost:8090/skill.md |
| Init skill (`pointer-init`) | http://localhost:8090/pointer-init.md |

## Default admin login

On first boot the API seeds one admin account from `ADMIN__EMAIL` / `ADMIN__PASSWORD` in `.env`:

| Field | Value |
|---|---|
| Email | `admin@pointer.local` |
| Password | `ChangeMe123!` |

Use these to sign in at **http://localhost:8090/admin/**. ⚠️ **Change `ADMIN__PASSWORD` in `.env`
before deploying anywhere real** (it's only a local default).

## Provision accounts + a project

Stakeholders can **self-sign-up** from the feedback widget (name, email, password, and a
non-admin role); the request lands in a **pending** queue an admin approves (and may change the
role for) or rejects from the dashboard. Admins can also create accounts directly:

1. Open **http://localhost:8090/admin/** and sign in as the seeded admin (credentials above).
2. **Roles →** (optional) add custom roles (e.g. Designer, QA) or rename/disable the defaults. Tick
   *Grants admin* to let a role into the dashboard. The `Admin` role is protected.
3. **Projects →** projects **self-register** when an app first connects, so you usually don't add them
   here — but you can pre-create one whose key matches `VITE_POINTER_PROJECT`, or rename/disable any.
4. **Users →** add stakeholder accounts (pick any role) and one **automation** account for the AI
   apply-tool.

## Integrate an app (two-line loader)

The app keeps the same env-gated loader in `index.html`; just point it at this API:

```bash
# apps/<app>/.env
VITE_POINTER_ENABLED=true
VITE_POINTER_SERVER=http://localhost:8090
VITE_POINTER_PROJECT=tuwaiq-clubs
```

On first use the toolbar appears without a popup; signing in (or self-signing-up) happens when the
user clicks the tool. The author and role come from the token. Element source paths still come from
the app's `data-component-source` stamping. The widget also captures an optional element screenshot
per comment, supports **private** comments (visible only to their author), and **archived** status.

> **Adding Pointer to another app?** Install the **`pointer-init`** skill and run it — it asks for the
> variables (project key, server URL, environment), detects the stack (Vite/Angular/Next/static),
> injects the loader, wires env, and verifies (see below).

## Install the Pointer skills (in a consuming repo)

Both skills are **served by the API** and install the same way — drop them into your repo's
`.claude/skills/` and run them:

```bash
# pointer-init — add the <pointer-feedback> widget to this app
mkdir -p .claude/skills/pointer-init
curl -s http://localhost:8090/pointer-init.md -o .claude/skills/pointer-init/SKILL.md

# pointer-feedback — list / apply the feedback queue with an AI tool
mkdir -p .claude/skills/pointer-feedback
curl -s http://localhost:8090/skill.md -o .claude/skills/pointer-feedback/SKILL.md
```

Then set the automation account credentials in your shell (NOT a Vite-exposed file):

```bash
export POINTER_EMAIL="automation@pointer.local"
export POINTER_PASSWORD="…"
```

Now tell your AI tool **"apply pending pointer comments"** — it reads `VITE_POINTER_SERVER`/
`VITE_POINTER_PROJECT` from the app's `.env`, logs in, fetches the `ReadyToApply` queue, applies each
item by `element.sourcePath`, and `PATCH`es it to `Applied` with an `appliedByLabel` for traceability.

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

## Admin Web (Angular)

A standalone **Angular 22** SPA in [`admin-web/`](admin-web/) is the primary admin dashboard
(Overview/stats, Roles, Users, Projects), built with Angular Material and talking to this API. The
build-free `/admin/` page served by the .NET app is kept as a zero-dependency fallback.

```bash
cd admin-web
npm install
npm start            # → http://localhost:4200  (needs Node ≥ 22.22)
```

- Talks to the API at `apiBase` in `src/environments/environment.ts` (default `http://localhost:8090`);
  CORS is already open server-side.
- The Angular API layer (services + models) is **auto-generated from Swagger via Orval** into
  `src/app/core/api/generated/` — never edit it by hand. After backend endpoint/DTO changes, run
  `npm run generate-services` (API must be up). See
  [`docs/skills/orval-codegen/SKILL.md`](docs/skills/orval-codegen/SKILL.md).
- Sign in with an admin account (e.g. the seeded `admin@pointer.local` / `ChangeMe123!`). Auth is the
  same local-account JWT; the SPA stores it and sends `Authorization: Bearer …`, and only roles whose
  `GrantsAdmin` is true can enter.
- `npm run build` → static bundle in `admin-web/dist/` (deployable to any static host).
- **Language + theme:** header toggles for **AR/EN** (Arabic flips to RTL) and **light/dark**. Each
  user's choice is saved in the DB (`PATCH /api/me/preferences`, columns on `users`) and restored on
  next login/device; first visit falls back to browser language + system theme (then `en`/`dark`).
- See [`docs/ADMIN_WEB_DESIGN.md`](docs/ADMIN_WEB_DESIGN.md) and
  [`docs/ADMIN_PREFS_I18N_DESIGN.md`](docs/ADMIN_PREFS_I18N_DESIGN.md) for the designs.

## Docs

- [`docs/DESIGN.md`](docs/DESIGN.md) — full architecture, auth, data model, endpoints, web-component & apply flow, admin UI.
- [`docs/ADMIN_WEB_DESIGN.md`](docs/ADMIN_WEB_DESIGN.md) — the Angular admin SPA design.
- [`docs/PLAN.md`](docs/PLAN.md) — phased implementation plan.
- [`docs/TASKS.md`](docs/TASKS.md) — living implementation tracker.

### Skills for AI agents

Repo conventions are also captured as agent-readable skills (see [`AGENTS.md`](AGENTS.md) /
[`CLAUDE.md`](CLAUDE.md)):

- [`docs/skills/orval-codegen/SKILL.md`](docs/skills/orval-codegen/SKILL.md) — *(internal)*
  regenerating the Angular API layer from Swagger.
- [`API/wwwroot/pointer-init.md`](API/wwwroot/pointer-init.md) — *(consumer, served at
  `/pointer-init.md`)* the `pointer-init` skill for adding the `<pointer-feedback>` widget to a host app.

## Status

✅ **Implemented & verified locally** (Phases 0–9). Backend, auth, admin UI, web component, and AI
skill are working; full API + browser e2e pass. Remaining before team use: choose the final product
name, deploy the service, and (later) the optional Keycloak/SSO swap.
