# AGENTS.md

> This file gives AI agents (opencode, Cursor, Windsurf, etc.) essential context about this repository.

## Project Overview

**Pointer API** is a .NET 8 Clean Architecture solution with an Angular 22 SPA (`admin-web/`).

- **Backend:** ASP.NET Core 8 + EF Core + PostgreSQL (solution root)
- **Frontend:** Angular 22 + Material + Transloco (`admin-web/`)
- **Infrastructure:** Docker Compose (Postgres + API), Justfile commands

## Critical Skills (READ THESE FIRST)

- **[Orval Code Generation](docs/skills/orval-codegen/SKILL.md)** — How `admin-web/` generates type-safe
  Angular services/models from the API's Swagger spec using Orval. **If you touch API endpoints or DTOs,
  you must regenerate the frontend services.** See the skill for full instructions.
- **[Integrate Pointer](API/wwwroot/pointer-init.md)** — the consumer-facing init skill, **served at
  `/pointer-init.md`** (same delivery as the apply skill `skill.md`): a developer installs it into their
  app's `.claude/skills/pointer-init/SKILL.md` and runs it to add the `<pointer-feedback>` widget
  (asks for project key/server/environment, detects the stack, injects the loader, wires env, verifies).

## Quick Reference

### Running the project

```bash
just up                    # Start API + DB via Docker (API on :8090)
cd admin-web && npm start  # Start Angular dev server on :4200
```

### Common commands

| Command | Where | Description |
|---|---|---|
| `just up` | repo root | Start API + Postgres via Docker |
| `just test` | repo root | Run .NET tests |
| `just migrate name="MyMigration"` | repo root | Add EF Core migration |
| `npm run generate-services` | admin-web/ | Regenerate Angular API services from Swagger spec |
| `npm start` | admin-web/ | Angular dev server |
| `npm run build` | admin-web/ | Production build |

### Key conventions

1. **Backend controllers** must have `[ProducesResponseType(typeof(InnerType), 200)]` and the global
   `[Produces("application/json")]` filter for Orval to generate typed code.
2. **Frontend API code** is auto-generated — never edit files in `src/app/core/api/generated/`.
3. **Frontend imports** use `@api/*` alias (e.g., `import { UsersService } from '@api/users/users.service'`).
4. **All API responses** are wrapped in `Result<T>` — the `apiInterceptor` unwraps automatically.

### Directory structure

```
pointer-api/
├── API/              ← .NET controllers, Program.cs, Swagger config
├── Application/      ← Services, DTOs, Result<T> wrapper
├── Domain/           ← Entities, enums
├── Infrastructure/   ← EF Core, repositories
├── admin-web/        ← Angular 22 SPA
│   ├── src/app/core/api/generated/   ← AUTO-GENERATED (do not edit)
│   ├── src/app/core/api/extract-message.ts
│   ├── src/app/core/auth/auth.interceptor.ts   ← apiInterceptor (envelope unwrap)
│   ├── orval.config.ts
│   ├── scripts/generate-services.mjs
│   └── openapi.json  ← downloaded Swagger spec
├── docs/skills/orval-codegen/SKILL.md  ← READ THIS for API codegen workflow
├── docker-compose.yaml
├── Dockerfile
└── justfile
```
