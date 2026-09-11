# CLAUDE.md

> Instructions for Claude Code when working in this repository.

## Essential Reading

- **[Cross-repo sync agents](../docs/roadmap/execution/01-OVERVIEW.md)** — read before changing API
  endpoints/DTOs: how the typed clients are generated and who syncs the dashboards, and when.
- **[Integrate Pointer Skill](../API/wwwroot/pointer-init.md)** — the consumer-facing init skill, served at
  `/pointer-init.md` (same as the apply skill `skill.md`). Follow when asked to add/init the
  `<pointer-feedback>` widget in a host app (ask for variables → detect stack → inject loader → verify).

More on client generation:

- Angular/React/Vue clients are generated **in this repo** from the running API's Swagger spec —
  `orval.config.ts` → `npm run generate-clients` (honours `POINTER_SWAGGER_URL`, default
  `http://localhost:8090/swagger/v1/swagger.json`) → `npm run build-clients`.
- Output lands in `clients/{angular,react,vue}/` which is **gitignored** — regenerated every run,
  never hand-edited, never committed.
- Published to **GitHub Packages** as `@moamen-ui/pointer-{angular,react,vue}` by the
  *Publish API clients* workflow (that workflow generates from **production**, so an endpoint that is
  not deployed cannot be published yet). Consumers install the package; `export NODE_AUTH_TOKEN=$(gh auth token)`.
- `orval.config.ts` `filters.tags` gates everything: an action whose `[Tags("X")]` is not in that list
  generates nothing, silently.
- The API response envelope (`Result<T>`) is unwrapped by each app's interceptor/mutator; client types
  are the **inner** type.

## Quick Reference

See [../AGENTS.md](../AGENTS.md) for project overview, commands, and directory structure.

## Key Rules

1. If you add/change/remove an API endpoint or DTO on the backend, you MUST:
   a. Add `[ProducesResponseType(typeof(InnerType), 200)]` to the controller action — the **inner**
      type, never `Result<T>`
   b. Ensure the controller's `[Tags("X")]` value is in `orval.config.ts` `filters.tags`
   c. Record the consequence in the doc's **Dashboard tasks** section. Do **not** regenerate clients
      per PR — the [`dashboard-agent`](agents/dashboard-agent.md) does that **once per phase**, after
      the backend is done and before that phase's e2e run
2. Never hand-edit anything under `clients/` — it is generated and gitignored
3. Dashboard apps import from the package barrel (`@moamen-ui/pointer-<fw>`), never deep paths, and
   never call the API with raw `axios`/`HttpClient`/`fetch`
4. GET endpoints produce signal-first `httpResource` functions (Angular) / TanStack Query hooks
   (React, Vue); POST/PATCH/DELETE produce services and mutations
5. After mutations, call `resource.reload()` (Angular) or invalidate the query (React/Vue)
6. If a change touches a brand-carrying surface (table, column, entity, migration, endpoint, config
   key, served file, storage key, package/bin, domain, or any new customer-visible name), invoke the
   [`rebranding-agent`](agents/rebranding-agent.md) **as soon as it lands** — eagerly, not batched
