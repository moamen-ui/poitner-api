# AGENTS.md

> This file gives AI agents (opencode, Cursor, Windsurf, etc.) essential context about this repository.

## Project Overview

**Pointer API** is a .NET 8 Clean Architecture solution — the backend for the Pointer feedback tool.

- **Backend:** ASP.NET Core 8 + EF Core + PostgreSQL (solution root)
- **Serves:** the `<pointer-feedback>` web component (`/widget.js`), the AI apply/init skills as
  markdown (`/skill.md`, `/pointer-init.md`), and a zero-dependency `/admin/` fallback page
- **Infrastructure:** Docker Compose (Postgres + API) + Justfile for dev; Compose + Caddy for prod
- **API Client Generation:** Orval generates a typed React client package from the
  live Swagger spec — a single `npm run generate-clients` command

> Since 2026-09-15 only the React dashboard exists (`pointer-dashboard/react`); Angular and Vue were
> retired at tag `last-three-apps` / branch `legacy/angular-vue`. Any dashboard work targets React only.
>
> **The admin dashboard is a separate repo:**
> [`pointer-dashboard`](https://github.com/moamen-ui/pointer-dashboard) — the React app. It does not
> generate anything: its typed client is generated **here** from this API's Swagger via Orval and
> installed there as a package. If you change endpoints or DTOs, annotate the inner type and check the
> tag is in `orval.config.ts` `filters.tags`; the client is regenerated once per phase by the
> [`dashboard-agent`](.claude/agents/dashboard-agent.md), not per change.

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
| `npm run generate-clients` | repo root | Regenerate the React API client package |
| `npm run clients:local` | repo root | Local loop: generate, build, and publish the client to local Verdaccio |
| `npm run build` | web-component/ | Build `<pointer-feedback>` → `API/wwwroot/widget.{js,css}` |
| `npx pointer-feedback apply` | app repos | Apply pending feedback via CLI (`.pointer/pointer.sh` is no-Node fallback) |

### Key conventions

1. **Backend controllers** must annotate the **inner** type via
   `[ProducesResponseType(typeof(InnerType), 200)]` (not `Result<T>`), plus the global
   `[Produces("application/json")]` filter — so Orval codegen stays clean.
2. **All API responses** are wrapped in `Result<T>` — the client mutator/interceptor unwraps it.
3. Behind the prod TLS proxy (Caddy), forwarded headers are honored so `/embed.js` + served skills
   emit `https` URLs.
4. **The web component is built, not hand-written.** `API/wwwroot/widget.{js,css}` are build
   artifacts — edit the source in `web-component/src/` and run `npm run build`. Never edit the
   generated files directly. `API/wwwroot/pointer.{js,css}` are stale leftovers, not build output
   (see [`web-component/CLAUDE.md`](web-component/CLAUDE.md)).
5. **Customer-visible names are frozen** in [`docs/ON-DISK-CONTRACT.md`](docs/ON-DISK-CONTRACT.md)
   (`.pointer/` files, env vars, element attributes, storage keys, served URLs, …). A PR that adds a
   new one must update that doc **and** `Tests/OnDiskContractTests.cs` in the same commit — the guard
   test fails otherwise.
6. **Environments vs Tags:** The `AppEnvironment` catalog (`local`/`prod`/`staging`/custom) determines where a project's URLs live, which is **distinct** from the `EnvironmentTag` (Local/Staging/Production) a developer tags a comment with. See [`docs/roadmap/execution/R1-09-project-environment-urls.md`](docs/roadmap/execution/R1-09-project-environment-urls.md) for details.

### Directory structure

```
pointer-api/
├── API/              ← .NET controllers, Program.cs, Swagger + /embed.js, static assets
│   └── wwwroot/      ← served files; widget.{js,css} are BUILD OUTPUT (from web-component/)
├── Application/      ← Services (Result + Scrutor), DTOs, FluentValidation
├── Domain/           ← Entities (BaseEntity audit), enums
├── Infrastructure/   ← EF Core + Postgres (snake_case), repositories, JWT, BCrypt
├── web-component/    ← <pointer-feedback> source → builds into wwwroot/
├── clients/          ← AUTO-GENERATED API client package (do not edit manually)
│   └── react/        ← @moamen-ui/pointer-react (TanStack Query hooks)
├── orval.config.ts   ← Orval config (React client)
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

## Source mapping (`cli/src/vite/`)

A Vite plugin ships with the CLI, exported as `pointer-feedback/vite`. It stamps each component's
root element with `data-component-source="<8-hex>"` — a hash of the component's repo-relative path
plus its export name — and writes `.pointer/manifest.json` mapping hash back to file. The hash
rather than the path is the point: production HTML then discloses nothing about the source layout.

The manifest is **gitignored and regenerated**, never committed: it is derived from the tree, and a
committed copy is wrong the moment anyone renames a file. `manifest.prev.json` is rotated on each
write, which is what lets a hash captured before a rename still resolve to the name it had.

Wire it in with `pointer init --source-map` (edits `vite.config.*`, sets `VITE_POINTER_SOURCE=true`
in the development env file). `pointer get --json` resolves a hash, `pointer map --from-source`
rebuilds the manifest offline, and `doctor` reports a missing one. Vite only — Angular, Next and
server-rendered stacks have no equivalent yet, and the widget falls back to matching on the element
snapshot there.

## Public documentation (`landing/docs/`)

The public docs site is plain HTML served from `landing/docs/`, indexed by `pages.json`, and linked
from the landing nav and footer.

**A feature's documentation page is written with the feature, in the same change.** Not afterwards:
written later, by someone re-reading code they have forgotten, a page costs several times as much
and comes out wrong. Each execution doc names its page in a `## Docs` section; an item whose page is
missing is not done.

Three rules keep the site coherent:

1. **`pages.json` is the manifest.** Add your page's entry (`file`, `title`, `nav`, `summary`) in the
   same change. The index renders from it, and every page's nav block is generated from it — so a
   page missing from the manifest is a page nothing links to. An entry may set `external: true`, or
   use a site-absolute `/path`, for a link that is not a file under `docs/`.
2. **Use the shared shell.** Link `assets/docs.css`; do not inline a `<style>` block. Eight private
   copies of the same tokens is how a colour change gets made seven times and missed once.
3. **Every claim must be true of the code as it exists.** These pages are read as promises. A
   fact-check of the first four found a CLI exit code that did not exist, a migration that never
   happened, and an ownership restriction that was not real; a later pass on the data page found two
   false privacy claims. If a contract asks you to document something unbuilt, say so rather than
   writing it.

`e2e/docs/` enforces 1 and 2 mechanically, plus dark mode, RTL and that no page links to a file
nobody wrote.

## API Client Generation (Orval)

A single typed client package is generated from the Swagger spec:

| Package | Client | Tech | Pattern |
|---|---|---|---|
| `@moamen-ui/pointer-react` | `react-query` | React + TanStack Query | `useQuery` / `useMutation` hooks |

### Regenerate after API changes

```bash
# API must be running on :8090
npm run generate-clients
```

### Local development loop (Verdaccio)

To test client changes locally in the dashboard without publishing to GitHub Packages or deploying:

```bash
# Verdaccio on :4873 + API on :8090
npm run clients:local
# Then run the printed install command with --no-save in the dashboard app
```

### How the client consumes the API

**React** (`@moamen-ui/pointer-react`):
```ts
import { useGetApiAdminUsers, usePostApiAdminUsers } from '@moamen-ui/pointer-react';
// GETs → useQuery hooks
// POSTs → useMutation hooks
// Envelope unwrapped by the package's axios mutator (clients/react/mutator.ts)
```
