# Multi-Tenancy Phase 1 (Tenancy Core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax. Each task is tagged with a suggested implementer — **[GLM]** = delegate to opencode + GLM-5.2 in an isolated git worktree (mechanical/low-risk), **[Claude]** = implement/review closely (security-critical scoping). Every delegated diff is still reviewed before merge.

**Goal:** Make Pointer multi-tenant: a super admin (operator) sees/manages everything; scoped admins are tenants that see/manage only their own data, enforced server-side default-deny.

**Architecture:** Add an `OwnerId` (tenant = scoped-admin `PublicId`) to Project/User/Role/StatusPresentation; comments scope via their project. JWT carries `is_super_admin` + `tenant`. A shared scoping helper filters every scoped-admin read and guards every write. Super-admin path is unfiltered (unchanged). New tenant-lifecycle + settings + cascade-hard-delete on top.

**Tech Stack:** .NET 8 (EF Core 8.0.11 / Postgres, auto-migrate on boot), xUnit (`Tests/`), orval `@moamen-ui/pointer-*`, Angular/React/Vue dashboards.

## Global Constraints

- **Security-critical:** a scoping miss = cross-tenant data leak. Default-deny. Writes never trust an `OwnerId` from the request body. Update/delete on a non-owned row returns **NotFound** (don't leak existence).
- Branch `feat/multitenancy-phase1` (cut from `main`) in pointer-api; matching branch in pointer-dashboard.
- `CommentStatus { Open=1, ReadyToApply=2, Applied=3, Archived=4 }` unchanged. Services auto-register via Scrutor (`*Service` → interface). Validators are NOT auto-invoked — validate inline in services. `Result`/`Result<T>` envelope at `Pointer.Application.Response`. EF mappings auto-applied via `ApplyConfigurationsFromAssembly`; migrations auto-apply on boot.
- Tenant root = scoped-admin user's `PublicId`. `OwnerId == null` = super-admin/global. Existing prod rows stay `OwnerId == null` (super-admin-only) — back-compat.
- Super-admin requests are **unfiltered** (today's behaviour, must not change).
- Verify backend via `dotnet build` + `Tests/` xUnit (`dotnet test`) for scoping-helper logic + a **live curl isolation matrix** against the running API (local docker `:8090`). Dashboard via `npm run build` + browser smoke. Build env for dashboards: `export PATH=/opt/homebrew/opt/node@26/bin:$PATH` + `NODE_AUTH_TOKEN=$(gh auth token)`.
- Do NOT deploy until the user asks.

---

## Task 1 [GLM]: Ownership columns + AppSetting entity + migration

**Files:**
- Modify: `Domain/Entity/Role.cs` (add `bool IsSuperAdmin`, `Guid? OwnerId`)
- Modify: `Domain/Entity/User.cs` (add `Guid? OwnerId`)
- Modify: `Domain/Entity/Project.cs` (add `Guid? OwnerId`)
- Modify: `Domain/Entity/StatusPresentation.cs` (add `Guid? OwnerId`)
- Create: `Domain/Entity/AppSetting.cs`
- Create: `Infrastructure/Mappings/AppSettingMapping.cs`
- Modify: `Infrastructure/Mappings/{Role,User,Project,StatusPresentation}Mapping.cs` (map the new columns, snake_case: `is_super_admin`, `owner_id`)
- Modify: `Infrastructure/AppDbContext.cs` (`DbSet<AppSetting> AppSettings`)
- Generated: migration `AddTenancyColumns`

**Interfaces:**
- Produces: `OwnerId` (Guid?) on Role/User/Project/StatusPresentation; `Role.IsSuperAdmin` (bool); `AppSetting { string Key; string Value }` (BaseEntity), table `app_settings`, unique index on `key`.

- [ ] **Step 1:** Add `public bool IsSuperAdmin { get; set; }` and `public Guid? OwnerId { get; set; }` to `Role`; `public Guid? OwnerId { get; set; }` to `User`, `Project`, `StatusPresentation`.
- [ ] **Step 2:** Create `AppSetting : BaseEntity { public string Key {get;set;}=""; public string Value {get;set;}=""; }`.
- [ ] **Step 3:** Mapping for each new column mirroring the existing `ProjectMapping` style: `b.Property(x => x.OwnerId).HasColumnName("owner_id");` and `b.Property(x => x.IsSuperAdmin).HasColumnName("is_super_admin");`. Add `b.HasIndex(x => x.OwnerId);` on Project/User/Role/StatusPresentation (scoping filters by it). `AppSettingMapping`: table `app_settings`, BaseEntity cols, `key` required + unique, `value` text.
- [ ] **Step 4:** Add `public DbSet<AppSetting> AppSettings => Set<AppSetting>();` to `AppDbContext`.
- [ ] **Step 5:** `dotnet ef migrations add AddTenancyColumns -p Infrastructure -s API`. Confirm it adds the columns + `app_settings` table.
- [ ] **Step 6:** `dotnet build` (0 errors). Commit `feat(api): tenancy ownership columns + app_settings (migration)`.

---

## Task 2 [Claude]: JWT claims + ICurrentUser tenant context + SuperAdmin policy

**Files:**
- Modify: `Infrastructure/Auth/JwtTokenService.cs`
- Modify: `Application/Abstractions/ICurrentUser.cs`
- Modify: `Infrastructure/CurrentUser/HttpCurrentUser.cs`
- Modify: `API/Auth/Policies.cs`
- Modify: `API/Extensions/AuthenticationExtensions.cs`
- Test: `Tests/TokenServiceTests.cs` (extend)

**Interfaces:**
- Consumes: `Role.IsSuperAdmin`, `User.OwnerId` (Task 1).
- Produces: JWT claims `is_super_admin` ("true"/"false") and `tenant` (the user's `OwnerId` GUID, omitted when null). `ICurrentUser.IsSuperAdmin : bool`, `ICurrentUser.TenantId : Guid?`. `Policies.SuperAdmin = "SuperAdmin"`.

- [ ] **Step 1 (test first):** In `Tests/TokenServiceTests.cs` add a test: issuing a token for a user whose role `IsSuperAdmin` emits `is_super_admin=true`; a user with `OwnerId = X` emits `tenant=X`. Run `dotnet test` → fails.
- [ ] **Step 2:** In `JwtTokenService.Issue`, append claims:
```csharp
new Claim("is_super_admin", (u.Role?.IsSuperAdmin ?? false) ? "true" : "false"),
```
and, when `u.OwnerId is { } owner`, `new Claim("tenant", owner.ToString())`. (Build the claim list dynamically.)
- [ ] **Step 3:** `ICurrentUser` gains `bool IsSuperAdmin { get; }` and `Guid? TenantId { get; }`. `HttpCurrentUser`: `IsSuperAdmin => ...FindFirst("is_super_admin")?.Value == "true";` and `TenantId => Guid.TryParse(...FindFirst("tenant")?.Value, out var g) ? g : null;`.
- [ ] **Step 4:** `Policies.SuperAdmin = "SuperAdmin"`. In `AuthenticationExtensions` add `.AddPolicy(Policies.SuperAdmin, p => p.RequireClaim("is_super_admin", "true"))`.
- [ ] **Step 5:** `dotnet test` (token tests pass) + `dotnet build`. Commit `feat(api): super-admin + tenant JWT claims, SuperAdmin policy`.

---

## Task 3 [Claude]: Scoping helper + ownership guard (the core) — TDD

**Files:**
- Create: `Application/Abstractions/IOwned.cs` (`Guid? OwnerId { get; set; }`)
- Modify: `Domain/Entity/{Project,User,Role,StatusPresentation}.cs` to implement `IOwned`
- Create: `Application/Common/TenantScoping.cs` (extension methods)
- Test: `Tests/TenantScopingTests.cs`

**Interfaces:**
- Produces:
  - `IQueryable<T> ScopedTo<T>(this IQueryable<T> q, ICurrentUser u) where T : IOwned` — super admin → unchanged; else → `q.Where(e => e.OwnerId == u.TenantId)`.
  - `bool OwnsOrSuper(this ICurrentUser u, IOwned e)` — super admin → true; else → `e.OwnerId == u.TenantId`.
  - `Guid? OwnerStampFor(this ICurrentUser u)` — the `OwnerId` to stamp on new rows (super admin → null; scoped → `u.TenantId`).

- [ ] **Step 1 (tests first):** In `Tests/TenantScopingTests.cs`, build a `List<FakeOwned>().AsQueryable()` with rows owned by A, B, and null. Assert: a scoped user (TenantId=A, not super) `.ScopedTo` returns only A's rows; a super admin returns all; `OwnsOrSuper` is false for B's row when caller is A, true for super; `OwnerStampFor` = A for scoped, null for super. Use a tiny `FakeCurrentUser` test double. Run `dotnet test` → fails.
- [ ] **Step 2:** Define `IOwned`; have the four entities implement it (the `OwnerId` property from Task 1 satisfies it). Implement `TenantScoping` exactly per the Interfaces signatures above.
- [ ] **Step 3:** `dotnet test` (scoping tests pass) + `dotnet build`. Commit `feat(api): tenant scoping helper + ownership guard (tested)`.

---

## Task 4 [Claude]: Scope the Projects service + Comments paths — TDD + live isolation curl

**Files:**
- Modify: `Application/Services/Implementation/ProjectService.cs` (list/get/create/update/disable + `EnsureAsync`)
- Modify: `Application/Services/Implementation/CommentService.cs` (list/get/queue/status/reply scope via project owner)
- Modify: the admin stats query (`StatsService.cs`) to `ScopedTo` projects + comments
- Modify: controllers only if they must pass `ICurrentUser` (services already take it where needed)

**Interfaces:**
- Consumes: `ScopedTo`, `OwnsOrSuper`, `OwnerStampFor`, `ICurrentUser` (Task 3).
- Produces: project + comment reads return only owned (or super = all); creates stamp owner; `EnsureAsync(key)` for a scoped admin creates the project owned by that admin; cross-tenant project key collisions are namespaced by owner (two tenants may each have `my-app` — `EnsureAsync` matches on `Key && OwnerId == scope`).

- [ ] **Step 1:** Inject `ICurrentUser` where missing. In `ProjectService`: every list/get `.ScopedTo(currentUser)`; `EnsureAsync` looks up `Key == k && OwnerId == currentUser.OwnerStampFor()` and stamps `OwnerId` on create; update/disable load + `if (!currentUser.OwnsOrSuper(p)) return NotFound`. Apply the same to `StatsService` (scope projects + the comment group query).
- [ ] **Step 2:** `CommentService`: list/get/queue filter comments to projects the caller owns (`.Where(c => projectIdsOwnedByCaller.Contains(c.ProjectId))` or join on `Project.OwnerId`); status-change/reply/delete load the comment + its project and require `OwnsOrSuper(project)` else NotFound. Super admin unfiltered.
- [ ] **Step 3:** `dotnet build`. Live isolation matrix (local `:8090`): create two scoped admins A and B (via Task 6 endpoint, or seed), each creates a project + comment; assert A's `GET` lists only A's project/comments, A cannot PATCH/GET B's comment (404), super admin sees both. Record the curl results in the task report.
- [ ] **Step 4:** Commit `feat(api): scope projects, comments, stats by tenant`.

---

## Task 5 [Claude]: Scope Users, Roles, and per-tenant Statuses — TDD + live isolation curl

**Files:**
- Modify: `Application/Services/Implementation/UserService.cs`
- Modify: `Application/Services/Implementation/RoleService.cs`
- Modify: `Application/Services/Implementation/StatusCatalogService.cs` + `StatusAdminService.cs`

**Interfaces:**
- Consumes: scoping helpers.
- Produces: a scoped admin sees/creates only its own users & roles; status catalog resolves **defaults → global (`OwnerId==null`) → tenant (`OwnerId==scope`)**; `GET /api/statuses` (no params) resolves by the authenticated caller; anonymous → defaults+global only.

- [ ] **Step 1:** `UserService` + `RoleService`: list/get `.ScopedTo`; create stamps `OwnerId = currentUser.OwnerStampFor()` (a scoped admin's new users/roles are owned by it; super-admin creates global, `OwnerId=null`); update/delete guard `OwnsOrSuper` else NotFound. A scoped admin may only assign its **own** roles (validate the roleId is owned-or-global).
- [ ] **Step 2:** `StatusCatalogService.GetAllAsync()` takes the caller into account: load overrides where `OwnerId == null` (global) plus `OwnerId == caller.TenantId` (tenant); merge tenant-over-global-over-defaults. Anonymous caller (no `TenantId`) → global+defaults. `StatusAdminService`: list/upsert/reset operate on the caller's layer — super admin edits `OwnerId=null`, scoped admin edits `OwnerId=scope` (the unique index on `status_value` becomes `(status_value, owner_id)` — adjust the mapping + migration here).
- [ ] **Step 3:** `dotnet build`. Live isolation curl: A and B each rename a status → each sees only its own label; the public widget `GET /api/statuses` as A's stakeholder shows A's labels; anonymous shows defaults. A cannot see B's users/roles. Record results.
- [ ] **Step 4:** Commit `feat(api): scope users, roles, and per-tenant status catalog`.

---

## Task 6 [Claude]: Tenant lifecycle service + cascade hard-delete — TDD for delete

**Files:**
- Create: `Application/Services/Interfaces/ITenantService.cs` + `Implementation/TenantService.cs`
- Create: `Application/DTOs/Tenant/*` (TenantResponse, CreateTenantRequest)
- Modify: `Application/Abstractions/IFileStorage.cs` usage (delete files)
- Create: `API/Controllers/Admin/TenantsController.cs` (`[Authorize(Policy = SuperAdmin)]`)

**Interfaces:**
- Produces: `ITenantService` with `ListAsync()`, `CreateAsync(CreateTenantRequest)`, `SetStatusAsync(id, approve|enable|disable)`, `HardDeleteAsync(Guid tenantId)`. Endpoints `GET/POST/PATCH/DELETE /api/admin/tenants[/{id:int}]`, all SuperAdmin-only.

- [ ] **Step 1:** `TenantService`: `ListAsync` = users whose role `IsSuperAdmin==false && GrantsAdmin==true` (the scoped admins) + counts. `CreateAsync` creates an **approved, active** scoped-admin user (role = "Workspace Admin"), `OwnerId = its own PublicId`. `SetStatusAsync` flips `ApprovalStatus`/`IsActive`. 
- [ ] **Step 2 (test first):** `HardDeleteAsync(tenantId)` — write a focused test/curl that seeds a tenant with a project+comment+reply+screenshot+user+role+status override, runs delete, and asserts every owned row is gone, the screenshot file is deleted, and **another tenant's data is untouched**. Implement: gather owned projects → comments → delete screenshot files via `IFileStorage`; hard-delete replies, comments, projects, status overrides, roles, users (the tenant) where `OwnerId == tenantId`; in a transaction, FK-safe order.
- [ ] **Step 3:** `TenantsController` wiring (mirror `Admin/RolesController` shape; SuperAdmin policy). `[Tags("Tenants")]`.
- [ ] **Step 4:** `dotnet build` + live verify (create tenant, delete tenant → gone, neighbour intact). Commit `feat(api): tenant lifecycle + cascade hard-delete`.

---

## Task 7 [GLM]: AppSetting service + settings endpoints + self-signup gate

**Files:**
- Create: `Application/Services/{Interfaces/ISettingsService.cs, Implementation/SettingsService.cs}`
- Create: `API/Controllers/Admin/SettingsController.cs` (SuperAdmin) + a public `signup-enabled` action on `AuthController`
- Modify: `Application/Services/Implementation/AuthService.cs` (add `RegisterAdminAsync`)
- Modify: `API/Controllers/AuthController.cs` (add `register-admin` + `signup-enabled`)

**Interfaces:**
- Consumes: `AppSetting`, scoping (settings are global super-admin data).
- Produces: `GET/PUT /api/admin/settings` (SuperAdmin) reading/writing `scoped_admin_signup_enabled`; `GET /api/auth/signup-enabled` (anonymous → `{enabled:bool}`); `POST /api/auth/register-admin` (anonymous) creates a **Pending** scoped-admin (role "Workspace Admin", `OwnerId=self`), 403 when the setting is off.

- [ ] **Step 1:** `SettingsService` get/set by key (default `scoped_admin_signup_enabled=false`). `RegisterAdminAsync`: if setting off → `Result.Failure`/forbidden; else create Pending+inactive scoped-admin (mirror the existing `RegisterAsync` but role = Workspace Admin, `ApprovalStatus=Pending`, `IsActive=false`, `OwnerId=self`). Login gate already blocks pending.
- [ ] **Step 2:** Controllers. Settings `[Authorize(Policy=SuperAdmin)]`; `signup-enabled` + `register-admin` `[AllowAnonymous]`. `[Tags("Settings")]` / reuse `Auth`.
- [ ] **Step 3:** `dotnet build` + live curl (toggle off → register-admin 403; toggle on → creates Pending; super admin approves → login works). Commit `feat(api): settings toggle + gated scoped-admin self-signup`.

---

## Task 8 [GLM]: Seed Workspace Admin role; super-admin flag; MeResponse.isSuperAdmin

**Files:**
- Modify: `API/Seed/AdminSeeder.cs`
- Modify: `Application/DTOs/Auth/MeResponse.cs` (+`bool IsSuperAdmin`) and `UserResponse` if it carries admin flags
- Modify: `Application/Services/Implementation/AuthService.cs` (map `IsSuperAdmin` into MeResponse on login + `/me`)

**Interfaces:**
- Produces: seeded role `Workspace Admin` (`GrantsAdmin=true, IsSuperAdmin=false, IsSystem=true`); `Admin` role seeded with `IsSuperAdmin=true`; the seeded admin user `OwnerId=null`; `MeResponse.IsSuperAdmin`.

- [ ] **Step 1:** In `AdminSeeder.DefaultRoles`, set `Admin` → `IsSuperAdmin=true`; add `("Workspace Admin", GrantsAdmin:true, IsSystem:true, IsSuperAdmin:false)`. On existing DBs, also patch the `Admin` row to `IsSuperAdmin=true` if false (idempotent seed step).
- [ ] **Step 2:** `MeResponse.IsSuperAdmin`; map it in `AuthService` from `user.Role.IsSuperAdmin`.
- [ ] **Step 3:** `dotnet build` + live: super admin login → `me.isSuperAdmin=true`; a scoped admin → false. Commit `feat(api): seed Workspace Admin role + expose isSuperAdmin`.

---

## Task 9 [Claude]: Regenerate + publish clients

- [ ] **Step 1:** API running on `:8090` with all new endpoints. Add `Tenants` + `Settings` to `orval.config.ts` tag filter. `npm run generate-clients`.
- [ ] **Step 2:** Verify all 3 clients emit tenants + settings + register-admin + signup-enabled hooks/services + the new models. Commit `chore(clients): add tenants/settings tags`.
- [ ] **Step 3:** Publish (after deploy, like prior phases) → bump version; record it.

---

## Tasks 10-NG / 10-REACT / 10-VUE [GLM, Claude reviews]: Dashboard super-admin features (×3)

> Built per-framework (GLM in a worktree, Claude reviews each diff). Bump `@moamen-ui/pointer-<fw>` to the published version + install first.

**Shared contract:**
- `MeResponse.isSuperAdmin` now exists → **hide super-admin-only nav** (Tenants, Settings) when `!isSuperAdmin`. Scoped admins keep the normal (now auto-scoped) dashboard.
- New super-admin-only **Tenants** page: list scoped admins; create; approve/enable/disable; **delete** (confirm dialog — cascade hard-delete). Uses the generated tenants hooks.
- New super-admin-only **Settings** page (or section): toggle `scoped_admin_signup_enabled`.
- New **public self-signup page** for scoped admins, shown only when `GET /api/auth/signup-enabled` is true; posts to `register-admin`; success message = "pending approval".
- Full i18n (en + ar), consistent with existing pages. Admin-only routes behind the existing admin guard; super-admin-only routes additionally gated on `isSuperAdmin`.

- [ ] **10-NG:** Angular — `features/tenants`, `features/settings`, public `features/signup`; routes + nav gating on `isSuperAdmin`; `npm run build`. Commit `feat(ng): super-admin tenants + settings + scoped-admin signup`.
- [ ] **10-REACT:** same in React. Commit `feat(react): …`.
- [ ] **10-VUE:** same in Vue. Commit `feat(vue): …`.

---

## Task 11 [Claude]: Final isolation review + deploy (on user approval)

- [ ] **Step 1:** Cross-tenant isolation matrix (curl) across ALL scoped endpoints: tenant A vs B reads/writes → denied; scoped admin → super-admin endpoints 403; anonymous statuses → defaults+global only; cascade delete leaves neighbours intact. Document results.
- [ ] **Step 2:** On user approval: merge both repos to `main`; deploy API (auto-migrate adds columns); publish clients; build+deploy 3 dashboards. Verify live.

---

## Self-Review

**Spec coverage:** data model (T1), auth/claims/policy (T2), scoping core (T3) + applied to projects/comments/stats (T4) + users/roles/statuses (T5), tenant lifecycle + cascade delete (T6), settings + gated self-signup (T7), seed + isSuperAdmin (T8), clients (T9), dashboards incl. super-admin nav gating + signup page (T10×3), isolation review + deploy (T11). ✅
**Placeholders:** scoping/auth tasks carry concrete signatures + code; mechanical tasks ([GLM]) have exact files + column names + endpoint shapes. Dashboard tasks reference the shared contract + generated hooks (names confirmed in T9), per the established per-framework pattern.
**Type consistency:** `OwnerId`/`IsSuperAdmin`/`TenantId`/`ScopedTo`/`OwnsOrSuper`/`OwnerStampFor`/`HardDeleteAsync`/`isSuperAdmin` used consistently across tasks.

## Out of scope (Phase 2/3)
Demo tenants (seeded per-tenant sample data, IsDemo+ExpiresAt, Try-demo, 10-comment cap, 24h cron reusing `HardDeleteAsync`, abuse caps, demo subdomain); billing/plans/quotas; email verification.
