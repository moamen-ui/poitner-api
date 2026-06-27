# CLAUDE.md

Guidance for Claude Code (and other AI agents) working in this repository.
**[AGENTS.md](AGENTS.md) is the canonical agent guide** — read it first; this file mirrors its
essentials so Claude surfaces them too.

## Project

**Pointer API** — a .NET 8 Clean Architecture backend (ASP.NET Core + EF Core + PostgreSQL) that
serves a drop-in feedback Web Component at `/pointer.js`, the AI apply/init skills as served
markdown, and a zero-dependency `/admin/` fallback page. Run locally with `just up` (API + Postgres
via Docker, API on `:8090`).

> The **admin dashboard is a separate repo**:
> [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) (Angular 22 SPA). It
> consumes this API's Swagger spec via Orval — so **if you change endpoints or DTOs, regenerate the
> dashboard's API layer there** (`npm run generate-services`, API up on `:8090`). The Orval
> conventions live in that repo's `docs/skills/orval-codegen/SKILL.md`.

## Critical Skill (READ FIRST)

- **[Integrate Pointer](API/wwwroot/pointer-init.md)** — the consumer-facing init skill, **served at
  `/pointer-init.md`** (same delivery model as the apply skill `skill.md`). A developer installs it
  into their app's `.claude/skills/pointer-init/SKILL.md` and runs it to add the `<pointer-feedback>`
  widget: it asks for the variables (project key, server URL, environment), detects the stack
  (Vite/Angular/Next/static), injects the loader, wires env, and verifies.

## Key conventions

1. All API responses are wrapped in `Result<T>`. Controllers annotate the **inner** type via
   `[ProducesResponseType(typeof(Inner), 200)]` (not the wrapper) so the dashboard's Orval codegen
   stays clean; the global `[Produces("application/json")]` filter is required too.
2. EF migrations: `just migrate name="..."`. Tests: `just test`. Format: `just fmt` (CSharpier).
3. Behind the production TLS proxy (Caddy) the API honors `X-Forwarded-Proto/For` so `/embed.js`
   and the served skills emit `https` URLs.

## Deploy

Production runs on a VM via Docker Compose (Postgres + API + Caddy). Config is committed:
[`docker-compose.prod.yml`](docker-compose.prod.yml), [`Caddyfile`](Caddyfile),
[`.env.prod.example`](.env.prod.example) — full guide in [`DEPLOY.md`](DEPLOY.md).
