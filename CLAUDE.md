# CLAUDE.md

Guidance for Claude Code (and other AI agents) working in this repository.
**[AGENTS.md](AGENTS.md) is the canonical agent guide** — read it first; this file mirrors its
essentials so Claude surfaces them too.

## Project

**Pointer API** — a .NET 8 Clean Architecture backend (ASP.NET Core + EF Core + PostgreSQL) with
an Angular 22 admin SPA (`admin-web/`) and a drop-in feedback Web Component served at
`/pointer.js`. Run locally with `just up` (API + Postgres via Docker, API on `:8090`) and
`cd admin-web && npm start` (dev server on `:4200`).

## Critical Skills (READ THESE FIRST)

- **[Orval Code Generation](docs/skills/orval-codegen/SKILL.md)** — `admin-web/` generates its
  Angular API services/models from the backend Swagger spec via Orval. **If you change API
  endpoints or DTOs, you MUST regenerate** (`cd admin-web && npm run generate-services`, with the
  API running on `:8090`). **Never hand-edit `admin-web/src/app/core/api/generated/`.**
- **[Integrate Pointer](API/wwwroot/pointer-init.md)** — the consumer-facing init skill, **served at
  `/pointer-init.md`** (same delivery model as the apply skill `skill.md`). A developer installs it into
  their app's `.claude/skills/pointer-init/SKILL.md` and runs it to add the `<pointer-feedback>` widget:
  it asks for the variables (project key, server URL, environment), detects the stack
  (Vite/Angular/Next/static), injects the loader, wires env, and verifies.

## Key conventions

1. All API responses are wrapped in `Result<T>`; the Angular `apiInterceptor` unwraps `.data`.
   Controllers annotate the **inner** type via `[ProducesResponseType(typeof(Inner), 200)]`.
2. Frontend imports use the `@api/*` alias; never relative paths into `generated/`.
3. EF migrations: `just migrate name="..."`. Tests: `just test`.
4. Don't commit `admin-web/openapi.json` edits by hand — it's the downloaded spec.
