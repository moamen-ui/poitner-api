# CLAUDE.md

> Instructions for Claude Code when working in this repository.

## Essential Reading

Since 2026-09-15 only the React dashboard exists (`pointer-dashboard/react`); Angular and Vue were
retired at tag `last-three-apps` / branch `legacy/angular-vue`. Any dashboard work targets React only.

- **[Cross-repo sync agents](../docs/roadmap/execution/01-OVERVIEW.md)** — read before changing API
  endpoints/DTOs: how the typed client is generated and who syncs the dashboard, and when.
- **[Integrate Pointer Skill](../API/wwwroot/pointer-init.md)** — the consumer-facing init skill, served at
  `/pointer-init.md` (same as the apply skill `skill.md`). Follow when asked to add/init the
  `<pointer-feedback>` widget in a host app (ask for variables → detect stack → inject loader → verify).

More on client generation:

- The React client is generated **in this repo** from the running API's Swagger spec —
  `orval.config.ts` → `npm run generate-clients` (honours `POINTER_SWAGGER_URL`, default
  `http://localhost:8090/swagger/v1/swagger.json`) → `npm run build-clients`.
- Output lands in `clients/react/` which is **gitignored** — regenerated every run,
  never hand-edited, never committed.
- Published to **GitHub Packages** as `@moamen-ui/pointer-react` by the
  *Publish API clients* workflow (that workflow generates from **production**, so an endpoint that is
  not deployed cannot be published yet). Consumers install the package; `export NODE_AUTH_TOKEN=$(gh auth token)`.
- `orval.config.ts` `filters.tags` gates everything: an action whose `[Tags("X")]` is not in that list
  generates nothing, silently.
- The API response envelope (`Result<T>`) is unwrapped by the app's interceptor/mutator; client types
  are the **inner** type.

## Quick Reference

See [../AGENTS.md](../AGENTS.md) for project overview, commands, and directory structure.

## Key Rules

1. If you add/change/remove an API endpoint or DTO on the backend, you MUST:
   a. Add `[ProducesResponseType(typeof(InnerType), 200)]` to the controller action — the **inner**
      type, never `Result<T>`
   b. Ensure the controller's `[Tags("X")]` value is in `orval.config.ts` `filters.tags`
   c. Record the consequence in the doc's **Dashboard tasks** section. Do **not** regenerate the client
      per PR — the [`dashboard-agent`](agents/dashboard-agent.md) does that **once per phase**, after
      the backend is done and before that phase's e2e run
2. Never hand-edit anything under `clients/` — it is generated and gitignored
3. The dashboard app imports from the package barrel (`@moamen-ui/pointer-react`), never deep paths,
   and never calls the API with raw `axios`/`fetch`
4. GET endpoints produce TanStack Query hooks; POST/PATCH/DELETE produce mutations
5. After mutations, invalidate the query
6. If a change touches a brand-carrying surface (table, column, entity, migration, endpoint, config
   key, served file, storage key, package/bin, domain, or any new customer-visible name), invoke the
   [`rebranding-agent`](agents/rebranding-agent.md) **as soon as it lands** — eagerly, not batched
7. If a change touches the **data layer** — anything under `Domain/Entity/`, `Infrastructure/Mappings/`,
   `Infrastructure/Migrations/`, `AppDbContext.cs`, a jsonb/json column shape, or a DB-stored enum —
   invoke the global [`db-architect`](~/.claude/agents/db-architect.md) agent **first**, before writing
   code. It is plan-only: it writes the execution doc under `docs/db/execution/`, a cheaper model
   implements it, and the change must cite the rule(s) in [`docs/db/DB-RULES.md`](../docs/db/DB-RULES.md)
   it follows. Its schema review lives in `docs/db/DB-REVIEW-<date>.md`. Never hand-write a migration
   without an execution doc behind it
