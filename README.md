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

## Quick start (local)

Requires Docker (for Postgres) and either the .NET 8 SDK or Docker for the API.

```bash
cp .env.example .env          # adjust JWT__SigningKey / ADMIN__* if you like

# Option A — full stack in Docker (Postgres + API, hot reload)
just up                       # API on http://localhost:8090, Postgres on :5433

# Option B — Postgres in Docker, API locally
docker compose up -d db
ConnectionStrings__Default="Host=localhost;Port=5433;Database=pointer;Username=pointer;Password=pointer" \
ASPNETCORE_URLS="http://0.0.0.0:8090" dotnet run --project API
```

On boot the API auto-migrates (`DBMigrationEnabled=true`) and **seeds one admin** from
`ADMIN__EMAIL` / `ADMIN__PASSWORD`.

Useful URLs:

| What | URL |
|---|---|
| Swagger | http://localhost:8090/swagger |
| Admin UI | http://localhost:8090/admin/ |
| Web component | http://localhost:8090/pointer.js |
| AI skill | http://localhost:8090/skill.md |
| Test host page | http://localhost:8090/test.html |

## Default admin login

On first boot the API seeds one admin account from `ADMIN__EMAIL` / `ADMIN__PASSWORD` in `.env`:

| Field | Value |
|---|---|
| Email | `admin@pointer.local` |
| Password | `ChangeMe123!` |

Use these to sign in at **http://localhost:8090/admin/**. ⚠️ **Change `ADMIN__PASSWORD` in `.env`
before deploying anywhere real** (it's only a local default).

## Provision accounts + a project

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

On first use the overlay shows an **email/password login** (the local account); the author and role
come from the token. Element source paths still come from the app's `data-component-source` stamping.

## Install the AI apply skill (in a consuming repo)

```bash
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

## Docs

- [`docs/DESIGN.md`](docs/DESIGN.md) — full architecture, auth, data model, endpoints, web-component & apply flow, admin UI.
- [`docs/PLAN.md`](docs/PLAN.md) — phased implementation plan.
- [`docs/TASKS.md`](docs/TASKS.md) — living implementation tracker.

## Status

✅ **Implemented & verified locally** (Phases 0–9). Backend, auth, admin UI, web component, and AI
skill are working; full API + browser e2e pass. Remaining before team use: choose the final product
name, deploy the service, and (later) the optional Keycloak/SSO swap.
```

> Note: commits are intentionally **not** made by the tooling — all work stays in the working tree.
