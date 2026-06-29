# Multi-Tenancy Phase 1 (Tenancy Core) Implementation Plan — rev 2 (post GLM review)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`). Implementer tags: **[GLM]** = delegate to opencode + GLM-5.2 in an isolated git worktree (mechanical/low-risk); **[Claude]** = implement/review closely (security-critical). Every delegated diff is reviewed before merge.

**Goal:** Make Pointer multi-tenant: a super admin (operator) sees/manages everything; scoped admins are tenants that see/manage only their own data, enforced **by EF global query filters (default-deny at the ORM)**.

**Architecture:** Denormalized `OwnerId` (tenant = scoped-admin `PublicId`) on every tenant-owned entity (Project, User, Role, StatusPresentation, Comment, Reply). `AppDbContext` applies **global query filters** keyed on the injected `ICurrentUser` so every read is scoped automatically; super admin short-circuits the filter. Writes stamp `OwnerId`; because a non-owned row is invisible, update/delete naturally 404. JWT carries `is_super_admin` + `tenant`. Uploads, stakeholder identity, and unique indexes are made tenant-aware. Tenant lifecycle + gated self-signup on top.

**Tech Stack:** .NET 8 (EF Core 8.0.11 / Postgres, auto-migrate on boot), xUnit (`Tests/`), orval `@moamen-ui/pointer-*`, Angular/React/Vue dashboards.

## Global Constraints

- **Security-critical (a miss = cross-tenant leak):** default-deny via EF query filters. Writes NEVER trust an `OwnerId` from the request body. A row hidden by the filter must 404, not 403 (don't leak existence). Super-admin requests are unfiltered (today's behaviour — must not change).
- **Enforcement model:** EF `HasQueryFilter` is the primary boundary. `.IgnoreQueryFilters()` is used ONLY in clearly-marked super-admin/system code paths (cascade delete, background jobs). Manual `.Where(OwnerId==...)` is a belt-and-suspenders supplement, never the only guard.
- Branch `feat/multitenancy-phase1` (from `main`) in both repos. Scrutor auto-registers `*Service`. Validators are NOT auto-invoked — validate inline. `Result`/`Result<T>` at `Pointer.Application.Response`. EF mappings auto-applied; migrations auto-apply on boot.
- Tenant root = scoped-admin user's `PublicId`. `OwnerId == null` = super-admin/global. Existing prod rows stay `OwnerId == null` (super-admin-only) — back-compat.
- Verify backend via `dotnet build` + `dotnet test` (xUnit) + a **live cross-tenant isolation curl matrix** (local docker `:8090`). Dashboards via `npm run build` + browser. Dashboard build env: `export PATH=/opt/homebrew/opt/node@26/bin:$PATH` + `NODE_AUTH_TOKEN=$(gh auth token)`.
- Do NOT deploy until the user asks. Demo tenants, the `demo.pointer.moamen.work` dashboard, the 24h cron, and the 10-comment cap are **Phase 2** (not here).

---

## Task 1 [Claude]: Ownership columns on all owned entities + AppSetting + index reshaping (migration)

**Files:** `Domain/Entity/{Role,User,Project,StatusPresentation,Comment,Reply}.cs`; `Domain/Entity/AppSetting.cs`; `Infrastructure/Mappings/*` (incl. new `AppSettingMapping`); `Infrastructure/AppDbContext.cs`; migration `AddTenancy`.

**Interfaces produced:** `Guid? OwnerId` on Role/User/Project/StatusPresentation/Comment/Reply; `bool Role.IsSuperAdmin`; `AppSetting { string Key; string Value }` (table `app_settings`, unique `key`).

- [ ] **Step 1:** Add `Guid? OwnerId` to all six entities; add `bool IsSuperAdmin` to `Role`. Create `AppSetting : BaseEntity`.
- [ ] **Step 2:** Mappings: map `owner_id` + `is_super_admin` (snake_case). Add non-unique `HasIndex(OwnerId)` on the six. `AppSettingMapping` (table `app_settings`, BaseEntity cols, `key` text required, `value` text).
- [ ] **Step 3 — index reshaping (CRITICAL, per review C4/C5):** in the mappings replace the existing global uniques:
  - `Project`: drop `HasIndex(Key).IsUnique()` → `HasIndex(Key, OwnerId).IsUnique()`.
  - `StatusPresentation`: drop `HasIndex(StatusValue).IsUnique()` → `HasIndex(StatusValue, OwnerId).IsUnique()`.
  - `User`: drop `HasIndex(Email).IsUnique()` → `HasIndex(Email, OwnerId).IsUnique()`.
- [ ] **Step 4:** `DbSet<AppSetting> AppSettings` in `AppDbContext`.
- [ ] **Step 5:** `dotnet ef migrations add AddTenancy -p Infrastructure -s API`. **Inspect the generated migration**: confirm it DROPs the old unique indexes and CREATEs the composite ones. Because Postgres treats NULLs as distinct (so multiple `(key, NULL)` global rows could collide-free), append to the migration a **partial unique index for the global scope** via raw SQL, e.g. `CREATE UNIQUE INDEX ix_projects_key_global ON projects(key) WHERE owner_id IS NULL;` (same for `status_presentations(status_value)` and `users(email)`), so global uniqueness still holds.
- [ ] **Step 6:** `dotnet build`. Commit `feat(api): tenancy ownership columns + composite/partial unique indexes`.

---

## Task 2 [Claude]: JWT claims + ICurrentUser tenant context + SuperAdmin policy (TDD)

**Files:** `Infrastructure/Auth/JwtTokenService.cs`; `Application/Abstractions/ICurrentUser.cs`; `Infrastructure/CurrentUser/HttpCurrentUser.cs`; `API/Auth/Policies.cs`; `API/Extensions/AuthenticationExtensions.cs`; `Tests/TokenServiceTests.cs`.

**Interfaces produced:** JWT claims `is_super_admin` and `tenant` (the user's `OwnerId`, omitted when null). `ICurrentUser.IsSuperAdmin : bool`, `ICurrentUser.TenantId : Guid?`. `Policies.SuperAdmin = "SuperAdmin"`.

- [ ] **Step 1 (test first):** extend `TokenServiceTests` — `IsSuperAdmin` role → `is_super_admin=true`; user with `OwnerId=X` → `tenant=X`. `dotnet test` fails.
- [ ] **Step 2:** `JwtTokenService.Issue`: add `is_super_admin` claim from `u.Role?.IsSuperAdmin`; add `tenant` claim when `u.OwnerId` non-null. Build claim list dynamically.
- [ ] **Step 3:** `ICurrentUser` += `bool IsSuperAdmin`, `Guid? TenantId`. `HttpCurrentUser`: read `is_super_admin` + parse `tenant`.
- [ ] **Step 4:** `Policies.SuperAdmin`; register `.AddPolicy(SuperAdmin, p => p.RequireClaim("is_super_admin","true"))`.
- [ ] **Step 5:** `dotnet test` + `dotnet build`. Commit `feat(api): super-admin + tenant JWT claims, SuperAdmin policy`.

---

## Task 3 [Claude]: EF global query filters = the tenancy boundary (TDD + live)

**Files:** `Infrastructure/AppDbContext.cs` (the DbContext already injects `ICurrentUser currentUser` for audit — reuse it); `Application/Common/TenantStamp.cs` (write-side helpers); `Tests/` (filter behaviour).

**Interfaces produced:**
- Global query filters in `OnModelCreating`:
  - Strict-own (super OR own): `Project`, `User`, `Comment`, `Reply` → `e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId`.
  - Own-plus-global (super OR own OR global): `Role`, `StatusPresentation` → `... || e.OwnerId == null` (tenants need global roles to assign + global status defaults to merge).
  - `AppSetting`: **no filter** (not tenant data; guarded by endpoint auth; read anonymously by `signup-enabled`).
- `Guid? TenantStamp.OwnerFor(ICurrentUser u)` = `u.IsSuperAdmin ? null : u.TenantId` — the value to stamp on new rows.

- [ ] **Step 1 (test first):** an xUnit test using the Npgsql/in-memory provider (or a SQLite in-memory `DbContext`) with a `FakeCurrentUser`: seed rows owned by A, B, null; assert a context with `currentUser=A(scoped)` reads only A's strict-own rows and (A + global) for Role/StatusPresentation; `currentUser=super` reads all; the `tenant` boundary cannot be bypassed by a normal LINQ query. `dotnet test` fails.
- [ ] **Step 2:** add the `HasQueryFilter`s in `OnModelCreating` (after `ApplyConfigurationsFromAssembly`). Add `TenantStamp.OwnerFor`. Document that super-admin/system paths must use `.IgnoreQueryFilters()` explicitly.
- [ ] **Step 3:** `dotnet test` + `dotnet build`. Live: with a scoped admin token, a raw `GET` of any list returns only their rows (proven more thoroughly in Task 4+). Commit `feat(api): tenant isolation via EF global query filters (tested)`.

> After this task, READS are safe-by-default and UPDATE/DELETE of a non-owned row naturally returns NotFound (the row is filtered out and won't load). Remaining per-service work is mostly **stamping OwnerId on create** + the explicit super-admin/system bypasses.

---

## Task 4 [Claude]: Projects + EnsureAsync + Stats — owner-stamp on create, tenant-bound EnsureAsync (live isolation)

**Files:** `Application/Services/Implementation/ProjectService.cs`, `StatsService.cs`.

- [ ] **Step 1:** Project create/`EnsureAsync` stamp `OwnerId = TenantStamp.OwnerFor(currentUser)`. `EnsureAsync(key)` looks up by `Key == k && OwnerId == OwnerFor(currentUser)` (the query filter already restricts, but match the owner explicitly so a tenant key never resolves to a global project). Update/disable: load (filter hides non-owned → null → NotFound). Two tenants may both have `my-app` (composite index allows it).
- [ ] **Step 2:** `StatsService`: its three queries (projects, users, comments-grouped) are now auto-scoped by the filters — remove any assumption of global visibility; for the super admin they still return all. Verify counts are per-tenant for a scoped admin.
- [ ] **Step 3:** `dotnet build` + live isolation curl (two scoped admins A,B): A lists only A's projects; A cannot GET/PATCH B's project (404); super admin sees both; stats are per-tenant. Record results.
- [ ] **Step 4:** Commit `feat(api): owner-stamp projects + tenant-bound EnsureAsync + scoped stats`.

---

## Task 5 [Claude]: Uploads — ownership-checked upload, authenticated download, owner-partitioned storage (per review C2)

**Files:** `API/Controllers/UploadsController.cs`; `Infrastructure/Storage/LocalFileStorage.cs`; `API/Program.cs` (stop serving `wwwroot/uploads` as public static files); `Application` upload service if present.

- [ ] **Step 1:** `POST /api/uploads`: resolve the target project by `(Key == project, OwnerId == OwnerFor(currentUser))` via the (now-filtered) context; 404 if the caller doesn't own such a project. Store under `uploads/{ownerOrGlobal}/{project}/...` (partition by owner; use a `global` segment when `OwnerId==null`).
- [ ] **Step 2:** Remove public static serving of `/uploads` (delete/scope the `UseStaticFiles` mapping for uploads in `Program.cs`). Add authenticated `GET /api/uploads/{ownerSeg}/{project}/{file}` (or `GET /api/uploads/{id}`) that loads the owning comment/project through the filtered context and streams the file only if the caller can see it; else 404.
- [ ] **Step 3:** Update `ScreenshotUrl` generation to the new authenticated path. `dotnet build` + live: A uploads to A's project (ok); A cannot upload to B's project key (404); A cannot fetch B's screenshot URL (404); super admin can fetch any. Record.
- [ ] **Step 4:** Commit `feat(api): tenant-scoped uploads + authenticated screenshot download`.

---

## Task 6 [Claude]: Comments — stamp owner, preserve author-privacy + tenancy, scope all mutation paths (per review I3/I5)

**Files:** `Application/Services/Implementation/CommentService.cs`.

- [ ] **Step 1:** On comment/reply **create**, stamp `OwnerId` = the comment's project's `OwnerId` (the tenant that owns the project), NOT `OwnerFor(currentUser)` directly — a stakeholder's comment belongs to the project's tenant. (Resolve the project, copy its `OwnerId`.) Replies inherit the parent comment's `OwnerId`.
- [ ] **Step 2:** The six read/mutate methods (`ListAsync`, `GetByIdAsync`, queue, `UpdateStatusAsync`, `EditAsync`, `SetVisibilityAsync`, `DeleteAsync`, `AddReplyAsync`) are auto-tenant-scoped by the Comment query filter. **Preserve the existing author-privacy rule** on top: `!c.IsPrivate || c.AuthorId == callerId` still applies (tenancy filter AND privacy filter both apply — privacy is not replaced). Add `.Include(c => c.Project)` only where a method needs the project (e.g. ownership-derived logic); the filter itself no longer requires it.
- [ ] **Step 3:** `dotnet build` + live: A sees only A's comments; A cannot read/patch/reply/delete B's comment (404); a private comment stays hidden from a same-tenant non-author; super admin sees all (still no private bypass — unchanged). Record.
- [ ] **Step 4:** Commit `feat(api): owner-stamp comments + preserve privacy under tenancy`.

---

## Task 7 [Claude]: Stakeholder identity under tenancy — widget register binds to the project's tenant (per review C3/C6)

**Files:** `API/Controllers/AuthController.cs` (`register`), `Application/Services/Implementation/AuthService.cs` (`RegisterAsync`), `Application/DTOs/Auth/RegisterRequest.cs`, the role list endpoint the widget calls.

- [ ] **Step 1:** `RegisterRequest` gains a required `ProjectKey`. `RegisterAsync` resolves the project by key **ignoring the tenant filter** (anonymous caller) → gets its `OwnerId`; the new stakeholder is stamped `OwnerId = project.OwnerId`, and the requested `RoleId` must be a role owned by that project's tenant or a global non-admin role (reuse the Task 8 allow-list). Reject if the project key is unknown. (Super-admin/global projects → stakeholder `OwnerId=null`, unchanged for the operator's own widgets.)
- [ ] **Step 2:** The widget's "available roles" fetch (`/api/roles`) must return the project-tenant's assignable roles — resolve by project key, return that owner's roles + global non-admin roles, via an `.IgnoreQueryFilters()` query keyed on the project's owner (anonymous caller has no tenant context).
- [ ] **Step 3:** `dotnet build` + live: registering via the widget for project `demo-x` (owned by tenant T) creates a stakeholder owned by T; that stakeholder logs into the widget and sees only T's project/comments; registering with an unknown project key is rejected. Record.
- [ ] **Step 4:** Commit `feat(api): bind widget-registered stakeholders to the project tenant`.

---

## Task 8 [Claude]: Users + Roles — owner-stamp + role-assignment allow-list (per review I4)

**Files:** `Application/Services/Implementation/UserService.cs`, `RoleService.cs`.

- [ ] **Step 1:** Reads auto-scoped (User strict-own; Role own+global). Create stamps `OwnerId = OwnerFor(currentUser)` (scoped admin's users/roles owned by it; super-admin → null/global). Update/delete auto-404 for non-owned.
- [ ] **Step 2 (privilege-escalation guard):** when a scoped admin assigns a role to a user, the target role must be **either** owned by the caller, **or** global (`OwnerId==null`) **and** `!IsSuperAdmin && !GrantsAdmin`. Never allow a scoped admin to assign an admin-tier (`GrantsAdmin` or `IsSuperAdmin`) role. Only the super admin may assign admin-tier roles. Enforce in `UserService` create/update.
- [ ] **Step 3:** `dotnet build` + live: A sees only its users/roles + global non-admin roles; A cannot assign the global `Admin`/`Workspace Admin` role to a stakeholder (rejected); A cannot read/edit B's user/role (404). Record.
- [ ] **Step 4:** Commit `feat(api): scope users/roles + block admin-role escalation`.

---

## Task 9 [Claude]: Per-tenant status catalog (per review I2)

**Files:** `Application/Services/Implementation/StatusCatalogService.cs`, `StatusAdminService.cs`.

- [ ] **Step 1:** `GetAllAsync()`: the Role/StatusPresentation filter already returns global (`null`) + the caller's tenant overrides. Merge **defaults → global(null) → tenant(scope)**. Anonymous/super (`TenantId==null` and not editing) → defaults+global. Add an explicit comment that anonymous and super both resolve to global here (per review M1).
- [ ] **Step 2:** `StatusAdminService` Upsert/Reset must filter by **both** `StatusValue == value && OwnerId == OwnerFor(currentUser)` (super → null/global; scoped → tenant) so the composite index is matched deterministically (per review I2). The soft-delete-revive fix stays.
- [ ] **Step 3:** `dotnet build` + live: A and B each rename a status → each sees only its own; A's widget stakeholder sees A's labels; anonymous sees defaults; super edits the global layer. Record.
- [ ] **Step 4:** Commit `feat(api): per-tenant status catalog resolution`.

---

## Task 10 [GLM]: Seed Workspace Admin role + Admin.IsSuperAdmin + MeResponse.isSuperAdmin (MOVED EARLY, per review C7)

**Files:** `API/Seed/AdminSeeder.cs`; `Application/DTOs/Auth/MeResponse.cs`; `AuthService.cs`.

- [ ] **Step 1:** `AdminSeeder.DefaultRoles`: set `Admin` → `IsSuperAdmin=true`; add `Workspace Admin` (`GrantsAdmin=true, IsSystem=true, IsSuperAdmin=false`). On existing DBs, idempotently patch the `Admin` row to `IsSuperAdmin=true`. Seeded roles are global (`OwnerId=null`); the seeded admin user `OwnerId=null`.
- [ ] **Step 2:** `MeResponse.IsSuperAdmin`; map from `user.Role.IsSuperAdmin` in `AuthService` (login + `/me`).
- [ ] **Step 3:** `dotnet build` + live: super admin `me.isSuperAdmin=true`. Commit `feat(api): seed Workspace Admin role + expose isSuperAdmin`.

---

## Task 11 [Claude]: Tenant lifecycle service + cascade hard-delete (TDD for delete)

**Files:** `Application/Services/{Interfaces/ITenantService.cs, Implementation/TenantService.cs}`; `Application/DTOs/Tenant/*`; `API/Controllers/Admin/TenantsController.cs` (`[Authorize(Policy=SuperAdmin)]`, `[Tags("Tenants")]`).

**Interfaces produced:** `ITenantService.ListAsync()/CreateAsync(req)/SetStatusAsync(id, action)/HardDeleteAsync(Guid tenantId)`. Endpoints `GET/POST/PATCH/DELETE /api/admin/tenants[/{id:int}]`.

- [ ] **Step 1:** `ListAsync` = users with role `GrantsAdmin && !IsSuperAdmin` (scoped admins) + counts (super-admin context → unfiltered). `CreateAsync` = an approved+active scoped admin (role `Workspace Admin`, `OwnerId = its own PublicId`). `SetStatusAsync` flips `ApprovalStatus`/`IsActive`.
- [ ] **Step 2 (test/curl first):** `HardDeleteAsync(tenantId)` runs under `.IgnoreQueryFilters()` (system path) inside a transaction: delete screenshot files (via `IFileStorage`) for the tenant's comments, then hard-delete (real removes) Replies → Comments → Projects → StatusPresentations → Roles → Users where `OwnerId == tenantId`. Seed a tenant with all entity types + a screenshot + a second tenant; assert all of tenant-1 is gone (rows + files) and tenant-2 is untouched. (Note: `Comment.AuthorId` is not a real FK to users — manual; deletion order is by `OwnerId`, not FK cascade.)
- [ ] **Step 3:** `TenantsController`. `dotnet build` + live verify. Commit `feat(api): tenant lifecycle + cascade hard-delete`.

---

## Task 12 [GLM]: Settings toggle + gated, rate-limited scoped-admin self-signup (per review I7)

**Files:** `Application/Services/{Interfaces/ISettingsService.cs, Implementation/SettingsService.cs}`; `API/Controllers/Admin/SettingsController.cs`; `AuthController.cs` (+`register-admin`, +`signup-enabled`); `AuthService.RegisterAdminAsync`; a simple IP rate-limit (ASP.NET `AddRateLimiter` fixed-window on the signup endpoint).

- [ ] **Step 1:** `SettingsService` get/set (default `scoped_admin_signup_enabled=false`). `RegisterAdminAsync`: 403 if disabled; else create a **Pending, inactive** scoped admin (role `Workspace Admin`, `OwnerId=self`). Login gate blocks pending.
- [ ] **Step 2:** `SettingsController` `[SuperAdmin]` GET/PUT; `signup-enabled` + `register-admin` `[AllowAnonymous]`. Add a fixed-window rate limiter (e.g. 5/hour/IP) on `register-admin`.
- [ ] **Step 3:** `dotnet build` + live: toggle off → `register-admin` 403; on → creates Pending; super admin approves → login works; 6th rapid signup from one IP → 429. Commit `feat(api): settings toggle + gated rate-limited scoped-admin signup`.

---

## Task 13 [Claude]: Regenerate + publish clients
- [ ] Add `Tenants` + `Settings` to `orval.config.ts` tags; `npm run generate-clients`; verify all 3 clients emit tenants/settings/register-admin/signup-enabled + models; commit `chore(clients): tenants/settings tags`. Publish after deploy.

---

## Tasks 14-NG / 14-REACT / 14-VUE [GLM, Claude reviews]: Dashboard super-admin features (×3)

**Shared contract:** bump `@moamen-ui/pointer-<fw>` + install. Hide super-admin-only nav (Tenants, Settings) when `!me.isSuperAdmin`. New super-admin **Tenants** page (list/create/approve/enable/disable/delete-with-confirm). New super-admin **Settings** page (self-signup toggle). New public **self-signup** page (shown only when `signup-enabled`), posts `register-admin`, success="pending approval". Full en+ar i18n. Super-admin-only routes gated on `isSuperAdmin`.
- [ ] **14-NG / 14-REACT / 14-VUE:** build per framework, `npm run build`, commit `feat(<fw>): super-admin tenants + settings + scoped-admin signup`.

---

## Task 15 [Claude]: Final isolation review + deploy (on user approval)
- [ ] **Step 1:** full cross-tenant curl matrix across EVERY endpoint (projects, comments, uploads/screenshots, users, roles, statuses, stats): A↔B denied (404); scoped→super endpoints 403; anonymous statuses=defaults+global; widget stakeholder bound to project tenant; cascade delete leaves neighbours intact. Have **opencode+GLM do an adversarial isolation review** of the final diff. Document results.
- [ ] **Step 2:** on approval — merge both repos to `main`; deploy API (auto-migrate); publish clients; build+deploy 3 dashboards; verify live.

---

## Self-Review (real gap checklist, per review M3)

| Spec requirement | Task |
|---|---|
| OwnerId on owned entities (+Comment/Reply denormalized) | T1 |
| Reshape unique indexes (project key, status_value, email) + global partial uniques | T1 |
| JWT super_admin+tenant claims, SuperAdmin policy | T2 |
| **Default-deny via EF global query filters** | T3 |
| Owner-stamp + scoping: projects/stats | T4 |
| **Uploads scoping + authenticated screenshot download** | T5 |
| Comments: owner-stamp + tenancy AND author-privacy + all 6 mutate paths | T6 |
| **Stakeholder identity bound to project tenant (widget register)** | T7 |
| Users/roles scoping + **admin-role escalation block** | T8 |
| Per-tenant statuses + (StatusValue,OwnerId) query | T9 |
| Seed Workspace Admin + isSuperAdmin (ordered before lifecycle) | T10 |
| Tenant lifecycle + cascade hard-delete (files + rows, IgnoreQueryFilters) | T11 |
| Settings toggle + gated, **rate-limited** self-signup | T12 |
| Clients | T13 |
| Dashboards ×3 (nav gating, tenants, settings, signup) | T14 |
| Isolation matrix + deploy | T15 |

**Notes carried from review:** I1 (re-parenting an existing widget into a tenant is out-of-scope onboarding for now — a future "reassign project owner" action); I6 (JWT `tenant` claim is stale until expiry on reparent — acceptable at 12h); M2 (`Comment.AuthorId` is not a real FK — delete by OwnerId, not cascade); M5 (`EnsureAsync` runs on the list path too — tenant-scoped, acceptable).

## Out of scope (Phase 2/3)
Phase 2 — demo tenants: one-click "Try demo" ephemeral scoped admin seeded with its OWN sample data, `IsDemo`+`ExpiresAt`, **10-comment cap**, 24h cleanup cron (reuses `HardDeleteAsync` via `IgnoreQueryFilters`/system context), abuse caps, `demo.pointer.moamen.work` dashboard (DNS already set). Phase 3 — billing/plans/quotas, email verification.
