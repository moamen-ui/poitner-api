# Pointer API — Backend Review

**Reviewer:** Fable 5 · **Date:** 2026-07-05 · **Scope:** `pointer-api` .NET 8 backend
(Domain / Application / API / Infrastructure). **Review only — no source was modified.**

**Method.** Every finding was traced by reading the actual code (query, filter, controller,
migration, mapping). Findings are tagged **[Confirmed]** (mechanism verified in code) or
**[Suspected]** (depends on runtime data or a config I could not observe). `file:line` points at the
exact anchor. The live production DB was not queried; a few perf items note where an `EXPLAIN` on the
VM would turn a Suspected into a Confirmed. Portions were cross-checked by independent review passes;
where a suspicion was **refuted** by the code it is recorded at the end so it isn't re-raised.

## Executive summary

For **normal (non-null-owner) tenants the isolation model is sound.** EF global query filters scope
every entity; `Repository.GetByIdAsync` deliberately uses a *filtered* query (not `FindAsync`);
project keys are unique per `(key, owner_id)` so colliding keys across tenants resolve correctly;
every `IgnoreQueryFilters()` call I audited re-scopes explicitly by `OwnerId`/`ProjectId`; no IDOR,
no mass-assignment, and no privilege escalation via `RoleId` was found (admin-granting roles are
rejected server-side). HMAC upload/reset signing is constant-time and enforces expiry. Login,
signup, and demo are rate-limited.

The material risks cluster in three areas: **null-owner ("global"/legacy) semantics**, **token
lifecycle**, and **query-shape/memory issues that won't survive growth on the single 4-vCPU / 24 GB
VM with one Postgres and no cache.** The single most important item is **C1** — the tenancy
migration never back-filled `owner_id`, and the design collapses a null-owner principal's isolation
filter to "see the entire null-owner bucket."

**Counts: 1 Critical · 6 High · 15 Medium · 10 Low.**

---

## 1. Security & Multi-Tenant Isolation

### C1 — [Confirmed · Critical] Null-owner principals see the entire legacy/global bucket across projects
`Infrastructure/Migrations/20260629130828_AddTenancy.cs:27-68` · `Infrastructure/Auth/JwtTokenService.cs:29-30` · `Infrastructure/AppDbContext.cs:32-37` · `Application/Services/Implementation/AuthService.cs:208` · `Application/Services/Implementation/CommentService.cs:233-252`

The `AddTenancy` migration adds every `owner_id` column as `nullable: true` with **no back-fill
`UPDATE`** — all pre-tenancy rows keep `owner_id = NULL`. The JWT only emits the `tenant` claim when
`OwnerId != null` (`JwtTokenService.cs:29`), so a null-owner principal has `TenantId == null`, and
the strict-own filter `IsSuperAdmin || e.OwnerId == currentUser.TenantId` then compiles to
`owner_id IS NULL` (the same EF null-parameter semantics `PredefinedActionService.ResolveInScopeAsync:183`
relies on deliberately). So **any non-super-admin principal with a null `owner_id` sees every
null-owner row of every filtered entity, across all projects.** This is reachable for *new* data:
`AuthService.RegisterAsync:208` stamps a stakeholder `OwnerId = projectOwnerId`, so anyone who
registers against a null-owner project (the marketing landing, or the dogfooded `pointer-api`
project) becomes a null-owner account. The bucket is **not** frozen legacy data either:
`CommentService.CreateAsync:103` stamps `OwnerId = projectOwnerId` and reply-create (`:282`) inherits
it, so normal commenting on a null-owner project keeps growing it. `CommentService.GetByIdAsync:233-252`
fetches by id + the collapsed filter with **no `ProjectId` scope**, so such a user can enumerate
integer comment ids and read any null-owner comment anywhere.

> **Latent same-trap on `PredefinedActionSuggestion`.** `AppDbContext.cs:46` gives it a *strict-own*
> filter and `SuggestionService.LoadOwnAsync` re-scopes by `TenantId`, which for a null-owner principal
> also collapses to `owner_id IS NULL`. Safe **today** only by the data invariant "a null-owner
> suggestion is never written" (`AppDbContext.cs:43-45`); if a future change ever writes one, the same
> cross-tenant collapse appears here. Treat that invariant as load-bearing and assert it.

**Fix:** (1) data migration to back-fill `owner_id` to each row's true owner, leaving `NULL` only for
genuine global catalog rows (which for *filtered* entities should be none); (2) stop minting
null-owner user accounts — give the global project a real owner, or scope widget/global stakeholders
by `ProjectId` rather than a null tenant; (3) add an explicit project/owner constraint to
`GetByIdAsync` so a by-id read can never cross a project boundary.

> **No regression guard exists for this exact case.** `Tests/TenantQueryFilterTests.cs` asserts
> isolation only for **non-null** tenants and runs on the **InMemory** (LINQ-to-objects) provider, not
> Npgsql — so it never exercises the C1 scenario (`TenantId == null, IsSuperAdmin == false`). The
> cheapest durable fix alongside the data back-fill is a test: a null-tenant, non-super-admin principal
> must return **zero** rows for `Comment`/`Project`/`User`, ideally against a real Postgres.

### H1 — [Confirmed · High] No token revocation: disabled/rejected users and post-reset sessions stay valid
`Application/Services/Implementation/AuthService.cs:82-104` · `Infrastructure/Auth/JwtTokenService.cs:31-32` · `docker-compose.prod.yml` (`JWT__LifetimeHours: "12"`)

Stateless JWTs, 12-hour lifetime, no deny-list or version claim. `ResetPasswordAsync` changes the
hash but does not invalidate existing tokens (a stolen session survives a reset up to 12 h); an admin
disabling/rejecting a user blocks *login* but the existing bearer keeps working until expiry.
**Fix:** add a `SecurityStamp`/`token_version` claim persisted on the user, bumped on password
change / disable / reject and validated in `OnTokenValidated`; or shorten lifetime + refresh tokens.

### H2 — [Confirmed · High] Password-reset tokens are replayable within their 30-minute window
`Infrastructure/Auth/ResetTokenService.cs:25-47` · `AuthService.cs:82-104`

Reset tokens are stateless HMAC over `{publicId}|{exp}` with no single-use tracking; the same token
resets the password repeatedly until expiry, and issuing a new token does not invalidate older ones.
A leaked link (proxy log, referrer, shared inbox) is fully replayable. **Fix:** persist a per-user
reset nonce or `password_changed_at` and include it in the signed payload; invalidate on first use
and on any password change.

### H3 — [Confirmed/Suspected · High] Demo provisioning emails arbitrary unverified addresses and returns credentials inline
`Application/Services/Implementation/DemoService.cs:44-63,183-186,199-208` · `Application/DTOs/Demo/DemoSessionResponse.cs:7` · `API/Controllers/DemoController.cs:16-18`

`ProvisionAsync` validates only the *format* of `recipientEmail`, then emails credentials to it —
an email-sending primitive aimed at arbitrary victims using your domain's reputation. It's bounded by
`demo` (3/hr/IP) and 3/day per email, but the per-email limit keys on the *victim's* address, so an
attacker rotates victims under the per-IP cap. Additionally, when the email **send fails** the
plaintext password is returned in the anonymous HTTP response (`DemoService.cs:203`, the
`emailSent == false` branch), enabling automated demo-account harvesting. (Note: hitting the per-email
daily cap returns early at `:62-63` with a message and **no** credentials — only a send *failure*
leaks the password.) **Fix:** double opt-in (verify-link) or CAPTCHA/Turnstile before
provisioning; gate the inline-password fallback behind a production flag; tighten the per-IP budget.

### H4 — [Confirmed · High] Email lookups use `Email.ToLower()` on an indexed column → sequential scan
`AuthService.cs:117,192,251` · `DemoService.cs:251` · `Infrastructure/Mappings/UserMapping.cs:25-26`

The unique index is `(email, owner_id)` and emails are stored already-normalized, yet every
login / register / forgot-password / demo-upgrade filters `u.Email.ToLower() == emailNormalized`. The
function on the column makes it non-sargable → **sequential scan of `users` on every login attempt**,
which is both a latency and a CPU cost under credential-stuffing on 4 vCPU. The same non-sargable
`.ToLower()` pattern also appears on smaller tables — `RoleService.CreateAsync/UpdateAsync`
(`RoleService.cs:30,136`, `Name.ToLower()`) and `UserService.CreateAsync` (`:46`, `Email.ToLower()`) —
lower impact but the same fix applies. **Fix:** query `u.Email == emailNormalized` (already normalized)
or add `CREATE INDEX ix_users_lower_email ON users (lower(email))`. Confirm with `EXPLAIN` on the VM.

### H5 — [Confirmed · High] Workspace/project export loads the entire comment graph (with JSON blobs) into memory
`Application/Services/Implementation/ExportImportService.cs:65-101,103-168`

`ExportWorkspaceAsync` loads *all* of a tenant's comments **with replies and JSON element captures**
(`.Include(c => c.Replies)`, `ToListAsync()` at :100), then builds a parallel DTO list (~2× copy),
then the controller serializes it (3rd copy) — no streaming, no paging, and **no export cap** (import
is capped at 5000; export is not). Each `ElementCapture` can carry ~8 KB snapshot + 8 KB styles +
8 KB rules (`CommentMapping.cs:42-53`). A large tenant can spike memory to multiple GB on the 24 GB
box → GC thrash / OOM while holding a request thread + DB connection. **Fix:** keyset-paginate and
stream (`IAsyncEnumerable` + `Utf8JsonWriter` in ~500-row batches), `.Select` into the export DTO so
only needed columns are fetched, and add an export ceiling.

### H6 — [Confirmed · High] List / apply-queue endpoints return the full DOM-snapshot blob for every row
`CommentService.cs:167-171,478-496,519-533` (`MapToListItem`/`MapToApplyItem` copy `Snapshot`/`ComputedStyles`/`AppliedCssRules`) · page size up to 100 (`:163,210`)

A single list page can be tens of MB; serialization CPU + LOH allocations scale with page size, and
concurrent listing on 4 vCPU degrades fast — a list view rarely needs full computed-styles text.
**Fix:** a lightweight list DTO (selector, page URL, viewport, screenshot URL) and load heavy fields
only in `GetByIdAsync`; project the query so the blob isn't fetched for lists.

### M1 — [Confirmed · Medium] One signing key is reused for JWTs, reset tokens, and upload URLs
`JwtTokenService.cs:16` · `ResetTokenService.cs:20-22` · `Infrastructure/Storage/UploadSigner.cs:22-24`

All three key their HMAC/JWT off the single `JWT:SigningKey`; no domain separation, so a key
disclosure has maximal blast radius and rotating the JWT key breaks every signed image URL. No
cross-protocol confusion is currently exploitable (message formats differ). **Fix:** derive
per-purpose subkeys via HKDF with distinct labels, or configure separate secrets.

### M2 — [Suspected · Medium] Forwarded-headers config trusts any proxy; per-IP rate-limit integrity leans entirely on Caddy
`API/Program.cs:33-34,140-146`

`KnownProxies.Clear()` + `KnownNetworks.Clear()` is the documented "trust `X-Forwarded-For` from
anyone" escape hatch, and the limiter partitions on `RemoteIpAddress`. In the current topology this
is *likely* safe — Caddy 2.7+ discards untrusted inbound XFF, the API isn't published to the host
(`docker-compose.prod.yml`), and `ForwardLimit` defaults to 1 (rightmost hop = the client Caddy
appended). But the safety is a property of the deployment, not the code: if the API is ever exposed
directly, or `ForwardLimit`/Caddy's XFF handling changes, the limiter on every anonymous auth
endpoint (login brute-force, demo abuse, forgot-password flooding) becomes spoofable. Two independent
review passes flagged this as the top anti-abuse concern. **Fix:** set explicit `ForwardLimit = 1`
and restore `KnownNetworks`/`KnownProxies` to the Caddy/Docker subnet as defense-in-depth.

### M3 — [Confirmed · Medium] Request `Host` is reflected into returned URLs across five anonymous surfaces
`API/Controllers/BrandingController.cs:36` · `API/Program.cs:206` · `API/Program.cs:263` · `appsettings.json:8` (`AllowedHosts: "*"`)

With `AllowedHosts: "*"`, a spoofed `Host` header poisons every place the app reflects
`$"{Request.Scheme}://{Request.Host}"` into a response body. The report initially cited only branding;
verification found **five** surfaces, and two are worse than branding:
- `Program.cs:206` injects the origin into **`install.sh`** (a `curl | bash` installer) and into
  `skill.md` / `pointer-init.md` — a poisoned Host poisons the install script's own server URL.
- `Program.cs:263` reflects it into the body of **`/embed.js`** as `var server = '<origin>'`.
- `BrandingController.cs:36` (branding response) and the reset-email link (derives from branding).

`Host` is Kestrel-validated (no quote injection) so this is host-poisoning, not XSS. **Fix:** build
the public base from configured `Pointer:PublicUrl` (already used in `DemoController.cs:23`) at **all
five** sites, and pin `AllowedHosts`.

### M4 — [Confirmed · Medium] Per-email demo throttle is non-atomic and bloats `app_settings` unboundedly
`DemoService.cs:57-63,189-196`

The daily per-email limit is stored as an `AppSetting` row keyed `demo_email_{email}_{yyyymmdd}`,
read-checked-then-incremented without a transaction (TOCTOU — two concurrent requests both read 0),
and these rows are **never deleted**, so the global settings table grows one row per (email, day)
forever. **Fix:** move counters to a dedicated table/cache with an atomic upsert increment + TTL;
keep `app_settings` for config only.

### M5 — [Confirmed · Medium] Uploaded file content is never validated; trust is on client-set headers
`API/Controllers/UploadsController.cs:64-70`

Acceptance is decided by the client `Content-Type` header + filename extension; bytes are never
sniffed. Serving is *mitigated* (forced image `Content-Type` from extension + `Cache-Control: private`,
`:137-144`), so render-as-HTML is blocked, but a stored polyglot is defense-in-depth-exposed for any
future direct-serve path. **Fix:** verify magic bytes server-side; add `X-Content-Type-Options: nosniff`.

### M15 — [Confirmed · Medium] Anonymous register-by-key resolves a non-globally-unique key with `FirstOrDefault` → cross-tenant misrouting
`Application/Services/Implementation/AuthService.cs:157-166`

`RegisterAsync` (anonymous, widget signup) resolves the project with
`FirstOrDefaultAsync(p => p.DeletedAt == null && p.Key == projectKeyNormalized)` — **no `OwnerId`
scope, no ordering** — then hangs the entire registration off that project's `OwnerId` (role scope at
`:179`, user-owner stamp at `:208`). But project keys are unique only per **`(key, owner_id)`**
(`ProjectMapping.cs:24` + the partial global index in `AddTenancy.cs:147`), so two tenants can legitimately
share a key like `app`/`web`/`dashboard`. When they do, a widget registrant for that key is attached to
an **arbitrary** tenant (whichever row the DB returns first). If an attacker pre-created a project with
the victim's key in their own workspace, a user signing up through the victim's widget can be routed
into the attacker's tenant (Pending there), and their subsequent widget comments — which resolve the
same colliding key — land in the attacker's workspace. Not a direct cross-tenant *read*, but a
non-deterministic mis-routing / data-misdirection bug on the bootstrap path. The authenticated widget
paths avoid this by scoping `(key, owner)` via the JWT tenant; registration has no tenant yet, so the
collision is inherent. **Fix:** make project keys globally unique, or require an explicit tenant/owner
identifier (not just the key) at registration so resolution is deterministic.

### L1 — [Confirmed · Low] `LocalFileStorage.DeleteAsync` path guard uses prefix match without separator
`Infrastructure/Storage/LocalFileStorage.cs:51` — `StartsWith(uploadsRoot, ...)` (no trailing
separator) would accept a sibling `.../uploads-evil/...`. `GetFile` (`UploadsController.cs:131`) and
`DeleteOwnerFilesAsync` (`:73`) correctly use `uploadsRoot + separator`. Input is app-generated today
so it's not reachable, but the guard is inconsistent. **Fix:** append `+ Path.DirectorySeparatorChar`.

### L2 — [Confirmed · Low] Unhandled exceptions bypass the `Result` envelope (bare 500)
`API/Program.cs:150-161` maps only `UnauthorizedAccessException`; there is no global handler /
ProblemDetails. Not an info leak in Production, but clients expecting the envelope get an unparseable
error, and silent `catch {}` blocks (`AuthService.cs:74`, `LocalFileStorage.cs:54,81`) hide failures.
**Fix:** add global exception-handling middleware that logs and returns a consistent `Result`.

### L3 — [Confirmed · Low] Weak admin defaults shipped in `.env.prod.example`
`.env.prod.example` (`ADMIN_EMAIL=admin@pointer.local`, `ADMIN_PASSWORD=change-me`) · `API/Seed/AdminSeeder.cs:56-91`

The seeder is config-driven with no hardcoded secret (good) and reconciles the super-admin on every
boot, but an operator who deploys without changing the example values gets a known super-admin
password. The JWT key is validated ≥32 bytes at startup (`AuthenticationExtensions.cs:17-22`).
**Fix:** refuse to seed on the placeholder password, or document a mandatory change in `DEPLOY.md`.

---

## 2. Database Structure

### M6 — [Confirmed · Medium] Comment list has no index supporting its hot filter + sort
`CommentService.cs:140-171` · `Infrastructure/Mappings/CommentMapping.cs:35,58`

The list query filters `ProjectId == x AND DeletedAt IS NULL` (+ optional Status/Environment) and
sorts `ORDER BY created_at DESC` with `Skip/Take`. Existing indexes are `(owner_id)` and
`(project_id, status)` — neither covers the `created_at` ordering, so each page sorts the project's
matching comments. This is the endpoint that degrades first as comment counts grow. **Fix:** add
`(project_id, created_at DESC)` (ideally partial `WHERE deleted_at IS NULL`); consider
`(owner_id, project_id, created_at DESC)`.

### M7 — [Confirmed · Medium] `owner_id`-only indexes don't match the real `(owner_id, project_id, deleted_at)` predicates
`CommentMapping.cs:35` and the plan/demo-cap counts `CommentService.cs:69-72,86-89`, `ProjectService.cs:51-54`

Tenant filtering adds an implicit `owner_id = @tenant` to nearly every query, and the cap counts
filter `owner_id == owner AND deleted_at IS NULL (AND created_at >= monthStart)`. Single-column
`owner_id` indexes leave `deleted_at`/`created_at`/`project_id` as post-filter checks. **Fix:**
composite/partial indexes matching the real predicates; verify with `pg_stat_user_indexes` + `EXPLAIN`.

### M8 — [Confirmed(config)/Suspected(impact) · Medium] Npgsql pool has no `MaxPoolSize` against a small single Postgres
`Infrastructure/DependencyInjection.cs:16` (only `EnableRetryOnFailure`)

Default pool ceiling (~100) per container can exceed Postgres default `max_connections` once request
concurrency + the hourly sweep + long-held connections (H5, M10) stack up, yielding connection
exhaustion rather than graceful queueing. **Fix:** set an explicit `MaxPoolSize` (e.g. 40-50) sized
to Postgres `max_connections`.

### L4 — [Suspected · Low] Random GUID (v4) primary/owner keys fragment B-trees at scale
`Domain/Entity/*`, `UserMapping.cs:23-24` — random UUIDs cause page splits / poor locality. Postgres
has no clustered index so impact is mild and volume is trivial now. **Fix:** prefer UUID v7 for new
id columns if/when volume grows; low priority.

---

## 3. Backend Best Practices

### M9 — [Confirmed · Medium] `Include(c => c.Replies)` without `AsSplitQuery()` fans the large `element` blob across reply rows
`CommentService.cs:143,199,238,258,302,339` · `ExportImportService.cs:79`

EF's default single-JOIN duplicates the parent's columns per child; because the parent carries a
large `element` JSON blob, a comment with a big capture and N replies ships ~N× that blob. Across a
100-row page or a full export this multiplies Postgres I/O, network, and LOH allocations. **Fix:**
`.AsSplitQuery()` or project to DTOs so the blob is fetched once per comment.

### M10 — [Confirmed · Medium] Import commits up to 5,000 comments × 500 replies in one change-tracker / `SaveChanges`
`ExportImportService.cs:354` (AddAsync in loop) flushed once at `:221`/`:268`; bounds `:20-21`

Worst case ~5,000 comments + up to 2.5 M replies all tracked (EF keeps entity + snapshot, ~2× each)
and flushed in one long transaction, each comment serializing its JSON blob — a multi-GB spike + a
long write-lock on the shared Postgres. **Fix:** batch (`SaveChanges` + `ChangeTracker.Clear()` every
~200-500) inside a transaction; enforce a total-reply cap.

### M11 — [Confirmed · Medium] No caching anywhere; the anonymous widget-read fan-out hits Postgres every call
No `IMemoryCache`/`AddMemoryCache` registered (`Program.cs`, `Infrastructure/DependencyInjection.cs`). `Application/Services/Implementation/BrandingService.cs` issues **8 sequential `AppSetting` queries** per `GET /api/branding` (anonymous, every widget load) via `SettingsService.cs:10-19,44-64`; `ProjectService.EnsureAsync:394-422` re-resolves the project key on every widget read/create; status presentations and the plan catalog are re-read per request (`StatusCatalogService`, `EntitlementService.ResolveAsync:85-117` caches per-request only).

On one Postgres with no replica this is the path to DB CPU + connection-pool saturation. **Fix:**
`AddMemoryCache()` and layer short-TTL caches over branding/settings, project-key→id, status
presentations, and the plan catalog, with write-through invalidation.

### M12 — [Confirmed · Medium] `AuthController.Me` runs a synchronous DB query (sync-over-async)
`API/Controllers/AuthController.cs:42-48` · `AuthService.cs:321-339` (`.FirstOrDefault()`)

`Me()` is non-async and executes a blocking EF query on the request thread, tying up a thread-pool
thread per call under load — the only sync DB path in the service layer. **Fix:** make
`IAuthService.Me` async (`FirstOrDefaultAsync`) and `await` it.

### M13 — [Confirmed · Medium] `Result`→`ActionResult` translation is copy-pasted across ~67 sites and `IsForbidden` diverges
All controllers; e.g. `Admin/ProjectsController.cs:46,57` uses `StatusCode(403, result)` while
`Admin/InvitesController.cs:35` / `Admin/PredefinedActionsController.cs:35` use `Forbid()` (which
**discards the `Result` body**). `Application/Response/Result.cs` already models every status.
**Fix:** one `ToActionResult(this Result)` extension switching over the flags once, so forbid
behavior is uniform and a new status is a one-line change.

### L5 — [Confirmed · Low] `PreferencesService.UpdateAsync` returns a drifted `MeResponse` (claims non-admin)
`Application/Services/Implementation/PreferencesService.cs:46-56` hand-builds `MeResponse` with
`RoleName = ""`, `IsAdmin = false`, and omits `IsSuperAdmin` entirely (no `.Include(u => u.Role)`),
diverging from the canonical `UserMapper.ToMeResponse` (`Application/Common/UserMapper.cs:12-23`). If
the dashboard refreshes auth state from this response, an admin is visually downgraded after changing
a preference. **Fix:** load the role and call `UserMapper.ToMeResponse`.

### L6 — [Confirmed · Low] No structured logging / observability
Only `DemoCleanupService` logs; no request logging, correlation ids, or error logging in the silent
`catch` blocks (`AuthService.cs:74`, `LocalFileStorage.cs:54,81`). **Fix:** add request + error
logging; log inside the currently silent catches.

### L7 — [Confirmed · Low] Migrations + admin seeding run automatically on every boot
`API/Program.cs:128-134` + `docker-compose.prod.yml` (`DBMigrationEnabled: "true"`). Fine for a single
container; racy if ever scaled to >1 replica and an availability risk on deploy. **Fix:** gate to a
one-shot init job / single instance, or document the single-replica constraint.

### L8 — [Confirmed · Low] Open default CORS + `AllowedHosts: "*"`
`Program.cs:87` (`AllowAnyOrigin`) is a deliberate, documented choice for the embeddable widget
(bearer, no cookies → not CSRF-exploitable) and the dashboard surface is correctly allow-listed
(`:88-92,238-251`). `AllowedHosts: "*"` is the residual (see M3). **Fix:** pin `AllowedHosts`.

---

## 4. Code Structure & Performance

### M14 — [Confirmed · Medium] `TenantService.HardDeleteAsync` materializes the full tenant graph then deletes row-by-row
`Application/Services/Implementation/TenantService.cs:361-412`

Each table is loaded with `ToListAsync()` (including `Comment` rows with their `element` blobs) purely
to `RemoveRange` it, inside a transaction run on every hourly demo sweep — wasteful memory + change
tracking + a long write-lock competing with live traffic. Scoping (`OwnerId == tenantId`) and the
execution-strategy transaction wrapper are correct. **Fix:** EF Core 8 `ExecuteDeleteAsync()` per
table in FK-safe order (set-based DELETE, no materialization). Optionally re-assert `IsDemo` inside
the delete path as defense-in-depth.

### L9 — [Confirmed · Low] `StatsService` per-project rollup is O(projects × groups) in memory
`Application/Services/Implementation/StatsService.cs:48-72` re-scans `grouped` for each project. The
SQL side is one efficient `GroupBy`; only the in-memory join is quadratic. Negligible now. **Fix:**
build a `Dictionary<int, List<row>>` once and look up per project.

### L10 — [Confirmed · Low] `EntitlementCatalog.ResolveInt/ResolveBool` use reflection per enforcement check
`Application/Common/EntitlementCatalog.cs:92-112` (`GetProperty`). Minor CPU. **Fix:** precompute a
static dictionary of compiled accessors. (The `All` catalog dictionary is correctly `static readonly`
— not rebuilt per call.)

**First bottleneck as usage grows:** the single Postgres — CPU and connection pool — saturated by the
entirely uncached anonymous widget-read fan-out (branding = 8 serial `AppSetting` queries +
project-key resolution + status reads; M11), amplified by the missing `(project_id, created_at)`
index (M6) and the full-blob list payloads (H6). The unbounded export / import / hard-delete memory
paths (H5, M10, M14) are the catastrophic-failure risk a single large tenant can trigger at any time.

---

## Top 5 to fix first

1. **C1 — Back-fill `owner_id` and close the null-owner isolation collapse.** Audit the null bucket,
   back-fill legacy rows, stop minting null-owner user accounts, and scope by-id reads by project.
   The only finding that can cross a data boundary.
2. **H1 + H2 — Token & reset lifecycle.** Add a security-stamp/version claim so disable / reject /
   password-change invalidate live JWTs, and make reset tokens single-use.
3. **H4 + M6 — Two hot-path DB fixes.** Drop the `Email.ToLower()` non-sargable filter and add the
   `(project_id, created_at DESC)` comment index — removes the two sequential scans on the busiest
   paths.
4. **H5 + H6 — Bound the memory paths.** Stream/paginate export and stop shipping the full DOM-snapshot
   blob in list responses; these are what a single large tenant uses to OOM the box.
5. **H3 + M2 — Anti-abuse.** Gate demo provisioning (verify-link/CAPTCHA) and harden the
   forwarded-headers trust so per-IP limits can't be neutralized.

---

## Verified-clean / refuted (so they aren't re-raised)

- **No IDOR / no mass-assignment / no role-escalation.** Admin id-taking endpoints
  (`UsersController`, `RolesController`, `SuggestionsController`, `InvitesController.Revoke`) resolve
  through the filter-respecting `Repository.GetByIdAsync` or explicit own-owner loads; write DTOs
  never expose `OwnerId`/`IsSuperAdmin`/`ApprovalStatus`, and register/invite/user-create all reject
  `GrantsAdmin`/`IsSuperAdmin` roles server-side (`AuthService.cs:177-178`, `InviteService.cs:369-370`,
  `UserService.cs:57-58,123-124,185-186`).
- **Login is rate-limited** (`AuthController.cs:17`, `signup` = 5/hr/IP) and does not reveal account
  existence (password verified before status; forgot-password always 200).
- **`ExecuteInTransactionAsync` correctly uses `CreateExecutionStrategy`** (`UnitOfWork.cs:59-72`) — no
  retry-strategy-vs-manual-transaction bug.
- **`IgnoreQueryFilters()` sites are safe:** each re-scopes by explicit `OwnerId`/`ProjectId`
  (demo/plan caps, cascade delete, hard delete, anonymous login/register, entitlement resolve).
- **Project-key collisions across tenants are safe** — unique `(key, owner_id)` + `EnsureAsync`
  matches `OwnerId` (`ProjectMapping.cs:24`, `ProjectService.cs:402-411`).
- **`LocalFileStorage` streams** (`SaveAsync` → `CopyToAsync`; downloads via `PhysicalFile`) — no
  whole-file buffering. **`StatsService`** aggregates in SQL, not C#. **`EntitlementCatalog.All`** is
  static, not per-call. **No `.Result`/`.Wait()`** sync-over-async on any hot path except M12.
- **`DemoCleanupService`** creates a scope per iteration and selects tightly (`IsDemo && ExpiresAt <
  now`); upgraded accounts are excluded (`IsDemo=false; ExpiresAt=null`).
