# AGENTS.md

> This file gives AI agents (opencode, Cursor, Windsurf, etc.) essential context about this repository.

## Project Overview

**Pointer API** is a .NET 8 Clean Architecture solution — the backend for the Pointer feedback tool.

- **Backend:** ASP.NET Core 8 + EF Core + PostgreSQL (solution root)
- **Serves:** the `<pointer-feedback>` web component (`/pointer.js`), the AI apply/init skills as
  markdown (`/skill.md`, `/pointer-init.md`), and a zero-dependency `/admin/` fallback page
- **Infrastructure:** Docker Compose (Postgres + API) + Justfile for dev; Compose + Caddy for prod

> **The admin dashboard is a separate repo:**
> [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) (Angular 22 + Material +
> Transloco). It generates its API layer from this API's Swagger via Orval — **if you change
> endpoints or DTOs, regenerate it there** (`npm run generate-services`). The Orval conventions live
> in that repo's `docs/skills/orval-codegen/SKILL.md`.

## Critical Skill (READ FIRST)

- **[Integrate Pointer](API/wwwroot/pointer-init.md)** — the consumer-facing init skill, **served at
  `/pointer-init.md`** (same delivery as the apply skill `skill.md`): a developer installs it into
  their app's `.claude/skills/pointer-init/SKILL.md` and runs it to add the `<pointer-feedback>`
  widget (asks for project key/server/environment, detects the stack, injects the loader, wires env,
  verifies).

## Quick Reference

### Running the project

```bash
just up                    # Start API + DB via Docker (API on :8090)
```

### Common commands

| Command | Where | Description |
|---|---|---|
| `just up` | repo root | Start API + Postgres via Docker |
| `just test` | repo root | Run .NET tests |
| `just fmt` | repo root | CSharpier format |
| `just migrate name="MyMigration"` | repo root | Add EF Core migration |

### Key conventions

1. **Backend controllers** must annotate the **inner** type via
   `[ProducesResponseType(typeof(InnerType), 200)]` (not `Result<T>`), plus the global
   `[Produces("application/json")]` filter — so the dashboard's Orval codegen stays clean.
2. **All API responses** are wrapped in `Result<T>` — the dashboard's `apiInterceptor` unwraps it.
3. Behind the prod TLS proxy (Caddy), forwarded headers are honored so `/embed.js` + served skills
   emit `https` URLs.

### Directory structure

```
pointer-api/
├── API/              ← .NET controllers, Program.cs, Swagger + /embed.js, static assets (pointer.js, skills)
├── Application/      ← Services (Result + Scrutor), DTOs, FluentValidation
├── Domain/           ← Entities (BaseEntity audit), enums
├── Infrastructure/   ← EF Core + Postgres (snake_case), repositories, JWT, BCrypt
├── docker-compose.yaml        ← dev (Postgres + API)
├── docker-compose.prod.yml    ← prod (Postgres + API + Caddy)  — see DEPLOY.md
├── Caddyfile / .env.prod.example / DEPLOY.md   ← production deploy
├── Dockerfile
└── justfile
```
