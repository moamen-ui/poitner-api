# Fable — Backend Review Brief (Pointer API)

> Review agent: **Fable 5**. This file is your task brief. Read it fully before starting.

## Your goal

Perform a **deep, adversarial review of the entire `pointer-api` backend** and produce a
prioritized, actionable report covering four dimensions:

1. **Backend best practices** — layering/Clean-Architecture discipline, error handling, async/await
   correctness, DI lifetimes, validation, logging, configuration/secrets handling, API design
   (status codes, idempotency, pagination), testability.
2. **Security** — authn/authz, **multi-tenant isolation** (the highest-risk area — see below),
   injection/SSRF/path-traversal, JWT handling, rate limiting, CORS, file uploads, secret exposure,
   IDOR/broken-object-level-auth, mass-assignment, information leakage in responses/errors.
3. **Database structure** — schema/normalization, data types, **indexes vs. the actual query
   patterns**, nullable/owner columns, unique constraints, migration hygiene, N+1 queries, EF query
   plans, connection/pooling.
4. **Code structure & performance** — cohesion/coupling, dead code, duplication, hot-path
   allocations, sync-over-async, unbounded queries, caching opportunities, and anything that won't
   scale past a single small VM.

**Output format** — a single markdown report grouped by dimension, findings sorted
**Critical → High → Medium → Low**. Each finding MUST have: a one-line title, the exact
`file:line`, *why* it's a problem (with a concrete exploit/impact or perf scenario), and a specific
fix (code sketch where useful). Distinguish **confirmed** issues from **suspected** (say which you
verified). Do not pad with generic advice — every item must be grounded in this codebase. End with
a short "top 5 to fix first" list.

**Ground rules**
- This is a **review, not a change** — do not edit source, do not commit, do not deploy.
- The VM runs **live production**. Any VM/DB inspection must be **read-only** (SELECTs, `EXPLAIN`,
  `docker logs`, `\d` — never `INSERT/UPDATE/DELETE/ALTER`, never restart containers).
- **Never print or exfiltrate secrets** (`.env.prod`, JWT signing key, DB password, API keys). You
  may confirm *that* a secret is set, not its value.
- Verify claims against the code before asserting them; prefer reading the real query/filter over
  assuming.

## What you're reviewing

**Repo (local):** `/Users/momen/Desktop/REPOS/pointer-api` — a .NET 8 **Clean Architecture** monorepo.
- `Domain/` — entities + value objects (EF-persisted). `Application/` — services (interface +
  `*Service` impl, auto-registered via **Scrutor**), DTOs, `Result<T>` envelope, FluentValidation
  validators, abstractions. `API/` — controllers, `Program.cs` (rate limiting, CORS, auth, static
  files, forwarded headers), `wwwroot/` (served widget `pointer.js`/`pointer.css`, `skill.md`),
  `Seed/`. `Infrastructure/` — `AppDbContext`, EF `Migrations/`, `Auth/` (JWT), `Storage/`
  (uploads + HMAC-signed URLs). `Tests/` — xUnit (122 tests). `docs/planning/` — feature specs.
- Ignore these (not the backend under review): `web-component/`, `extension/`, `landing/`,
  `clients/`, `node_modules/`.

**Stack & key patterns to scrutinize:**
- EF Core 8 + **Npgsql/PostgreSQL 15**, **snake_case** columns. Migrations in
  `Infrastructure/Migrations/`.
- **`Result<T>`** response envelope everywhere (`isSuccess/isNotFound/isConflict/isForbidden/
  isLimitReached/limit/data/message`).
- **Multi-tenant isolation via EF global query filters** in `Infrastructure/AppDbContext.cs` —
  e.g. `currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId`, with three variants:
  strict-own (Project, Comment, Reply, Invite, Subscription, ExtensionSite), own-plus-global
  (`|| e.OwnerId == null` for Role, StatusPresentation, PredefinedAction), and no-filter
  (AppSetting, Plan). **Audit every filter + every `IgnoreQueryFilters()` call** — a wrong filter or
  an unscoped `IgnoreQueryFilters` is a cross-tenant data breach. `TenantStamp.OwnerFor` +
  `HttpCurrentUser` (JWT `tenant`/`sub` claims) feed these. Note: legacy rows exist with
  `owner_id = NULL` ("global"), and the JWT only sets the `tenant` claim when `OwnerId != null` —
  reason carefully about null-tenant semantics in the filters.
- **JWT HS256** (`Infrastructure/Auth/JwtTokenService.cs`), signing key from config/env.
- **Rate limiting** (`API/Program.cs`) — policies `signup`/`demo`/`plans`; check auth/login coverage.
- **Uploads** (`API/Controllers/UploadsController.cs` + `Infrastructure/Storage/UploadSigner.cs`) —
  HMAC-signed URLs, path-traversal guards, content-type/size limits. Also the public
  `BrandingController` asset endpoint.
- **Entitlements/enforcement** (`Application/Services/Implementation/EntitlementService.cs`,
  `EntitlementCatalog`, `Subscription`, `Plan`) — a kill-switch (`enforcement_enabled`) gates it.
- Widget-read endpoints are **anonymous** (comments/predefined-actions by project key) — check the
  key→owner resolution for cross-tenant leakage on colliding keys.
- Emails (`AuthService`/`DemoService`/`UserService` → `IEmailService`) — best-effort, anonymous-safe
  reset flow.

Start by reading `AGENTS.md` / `CLAUDE.md` (if present) and `Infrastructure/AppDbContext.cs`, then
fan out.

## Accessing the production VM (read-only, only if runtime/DB inspection helps)

You do **not** need the VM to review the code — but for query plans, index checks, real data
distribution, or runtime logs, use SSH. **Read-only only.**

- **SSH key (note the space + parens — quote the whole path):**
  `"/Users/momen/Desktop/PRIVATE/ssh-key-2026-06-27 (3).key"`
- **Connect:**
  ```bash
  ssh -i "/Users/momen/Desktop/PRIVATE/ssh-key-2026-06-27 (3).key" ubuntu@145.241.155.196
  ```
- **Repo on the VM:** `~/pointer-api` (git clone; `.env.prod` holds secrets — do not read/print it).
- **Containers** (Docker Compose): `pointer-api-api-1` (.NET API, listens `:8080` inside the network,
  not published to the host — Caddy is the only public entrypoint), `pointer-api-db-1`
  (PostgreSQL 15), `pointer-api-caddy-1` (TLS + reverse proxy + static).
- **Inspect the DB (read-only):**
  ```bash
  docker exec pointer-api-db-1 psql -U pointer -d pointer -c "\dt"                 # tables
  docker exec pointer-api-db-1 psql -U pointer -d pointer -c "\d+ comments"        # a table's schema+indexes
  docker exec pointer-api-db-1 psql -U pointer -d pointer -c "EXPLAIN <query>;"    # query plan
  docker exec pointer-api-db-1 psql -U pointer -d pointer -c "select * from pg_stat_user_indexes;"
  ```
- **API logs / EF SQL:** `docker logs pointer-api-api-1 --since 1h` (EF logs the generated SQL —
  useful for spotting N+1 / missing indexes).
- **Live API through Caddy:** `https://api.pointer.moamen.work` (Swagger at `/swagger`, OpenAPI at
  `/swagger/v1/swagger.json`).

### Current VM specs (Oracle Cloud, ARM)
| | |
|---|---|
| OS | Ubuntu 20.04.6 LTS (`aarch64` / ARM) |
| Kernel | Linux 5.15.0-1081-oracle |
| CPU | **4 vCPU** (Ampere ARM) |
| RAM | **24 GB** (~22 GB free at idle) |
| Disk | 45 GB root, ~40% used (~28 GB free) |
| Docker | 28.1.1 · Compose v2.35.1 |
| DB | PostgreSQL 15.18 · database `pointer` ~9 MB · 14 tables |
| Topology | single VM: Postgres + API + Caddy via Docker Compose; volumes `pgdata`, `uploads` persist |

Scale context for the perf review: this is a **single small ARM VM**, one API container, one
Postgres, no read replica, no external cache. Flag anything that assumes more, and call out the
first bottleneck you'd hit as usage grows.
