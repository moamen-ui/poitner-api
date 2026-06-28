# AGENTS.md

> This file gives AI agents (opencode, Cursor, Windsurf, etc.) essential context about this repository.

## Project Overview

**Pointer API** is a .NET 8 Clean Architecture solution — the backend for the Pointer feedback tool.

- **Backend:** ASP.NET Core 8 + EF Core + PostgreSQL (solution root)
- **Serves:** the `<pointer-feedback>` web component (`/pointer.js`), the AI apply/init skills as
  markdown (`/skill.md`, `/pointer-init.md`), and a zero-dependency `/admin/` fallback page
- **Infrastructure:** Docker Compose (Postgres + API) + Justfile for dev; Compose + Caddy for prod
- **API Client Generation:** Orval generates typed client packages for Angular, React, and Vue from the
  live Swagger spec — all from a single `npm run generate-clients` command

> **The admin dashboard is a separate repo:**
> [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) (Angular 22 + Material +
> Transloco). It generates its API layer from this API's Swagger via Orval — **if you change
> endpoints or DTOs, regenerate the clients** (`npm run generate-clients`).

## Quick Reference

### Running the project

```bash
just up    # Start API + DB via Docker (API on :8090)
```

### Common commands

| Command | Where | Description |
|---|---|---|
| `just up` | repo root | Start API + Postgres via Docker |
| `just test` | repo root | Run .NET tests |
| `just fmt` | repo root | CSharpier format |
| `just migrate name="MyMigration"` | repo root | Add EF Core migration |
| `npm run generate-clients` | repo root | Regenerate all API client packages (Angular + React + Vue) |
| `npm run build` | web-component/ | Build `<pointer-feedback>` → `API/wwwroot/pointer.{js,css}` |

### Key conventions

1. **Backend controllers** must annotate the **inner** type via
   `[ProducesResponseType(typeof(InnerType), 200)]` (not `Result<T>`), plus the global
   `[Produces("application/json")]` filter — so Orval codegen stays clean.
2. **All API responses** are wrapped in `Result<T>` — the client mutator/interceptor unwraps it.
3. Behind the prod TLS proxy (Caddy), forwarded headers are honored so `/embed.js` + served skills
   emit `https` URLs.
4. **The web component is built, not hand-written.** `API/wwwroot/pointer.{js,css}` are build
   artifacts — edit the source in `web-component/src/` and run `npm run build`. Never edit the
   generated files directly.

### Directory structure

```
pointer-api/
├── API/              ← .NET controllers, Program.cs, Swagger + /embed.js, static assets
│   └── wwwroot/      ← served files; pointer.{js,css} are BUILD OUTPUT (from web-component/)
├── Application/      ← Services (Result + Scrutor), DTOs, FluentValidation
├── Domain/           ← Entities (BaseEntity audit), enums
├── Infrastructure/   ← EF Core + Postgres (snake_case), repositories, JWT, BCrypt
├── web-component/    ← <pointer-feedback> source → builds into wwwroot/
├── clients/          ← AUTO-GENERATED API client packages (do not edit manually)
│   ├── angular/      ← @pointer/api-angular (httpResource + HttpClient services)
│   ├── react/        ← @pointer/api-react   (TanStack Query hooks)
│   └── vue/          ← @pointer/api-vue     (TanStack Vue Query composables)
├── orval.config.ts   ← multi-client Orval config (Angular + React + Vue)
├── scripts/
│   └── generate-clients.mjs  ← downloads spec → runs Orval → creates barrel exports
├── openapi.json      ← downloaded Swagger spec (input to Orval)
├── package.json      ← root: orval, axios, prettier devDeps
├── docker-compose.yaml        ← dev (Postgres + API)
├── docker-compose.prod.yml    ← prod (Postgres + API + Caddy)  — see DEPLOY.md
├── Caddyfile / .env.example / DEPLOY.md   ← production deploy
├── Dockerfile
└── justfile
```

## API Client Generation (Orval)

Three typed client packages are generated from the same Swagger spec:

| Package | Client | Tech | Pattern |
|---|---|---|---|
| `@pointer/api-angular` | `angular` | Angular 19+ | `httpResource` functions (GETs) + `@Injectable` services (mutations) |
| `@pointer/api-react` | `react-query` | React + TanStack Query | `useQuery` / `useMutation` hooks |
| `@pointer/api-vue` | `vue-query` | Vue 3 + TanStack Vue Query | `useQuery` / `useMutation` composables |

### Regenerate after API changes

```bash
# API must be running on :8090
npm run generate-clients
```

### How each client consumes the API

**Angular** (`@pointer/api-angular`):
```ts
import { UsersService, getApiAdminUsersResource } from '@pointer/api-angular';
// GETs → signal-first httpResource functions (auto-refetch)
// POSTs/PATCHs → injectable service methods (Observable)
// Envelope unwrapped by the app's HTTP interceptor
```

**React** (`@pointer/api-react`):
```ts
import { useGetApiAdminUsers, usePostApiAdminUsers } from '@pointer/api-react';
// GETs → useQuery hooks
// POSTs → useMutation hooks
// Envelope unwrapped by the package's axios mutator (clients/react/mutator.ts)
```

**Vue** (`@pointer/api-vue`):
```ts
import { useGetApiAdminUsers, usePostApiAdminUsers } from '@pointer/api-vue';
// GETs → useQuery composables (accept refs/reactive)
// POSTs → useMutation composables
// Envelope unwrapped by the package's axios mutator (clients/vue/mutator.ts)
```
