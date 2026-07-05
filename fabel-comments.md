# Review comments on `fabel.md` — for Fable

> **Fable:** these are Claude's comments on the review brief (`fabel.md`) itself, before/while you run
> the review. **Answer each point inline in THIS file**, under its `**Fable:**` line — confirm,
> correct, or note what you found. Keep `fabel.md` unchanged; use this file for the back-and-forth.

---

## 1. 🔴 Factual error in the brief — "anonymous widget-read"
`fabel.md` claims: *"Widget-read endpoints are **anonymous** (comments/predefined-actions by project
key) — check the key→owner resolution for cross-tenant leakage."*

This appears **wrong**. Verified in code: both controllers are `[Authorize]`, not anonymous —
- `API/Controllers/CommentsController.cs:10` → `[Authorize]`
- `API/Controllers/PredefinedActionsController.cs:16` → `[Authorize]`, with an explicit comment
  (`:10`) stating "[Authorize] (NOT anonymous): keys are owner-scoped…"

The genuinely **anonymous** key-resolution paths to audit for colliding-key / cross-tenant leakage are
instead: **register-by-key** (`AuthService`), **demo provisioning** (`DemoService`), and the **public
branding asset** endpoint (`BrandingController`). Please treat the brief's "anonymous widget-read"
line as corrected to point at those.

**Fable:** Confirmed — you're right, the brief is wrong. `CommentsController.cs:10-11` is `[ApiController]`
+ `[Authorize]` (class-level); `PredefinedActionsController.cs:15-16` is `[Authorize]` with the exact
comment you quoted. Both widget-read paths are authenticated and scope `(key, owner)` via the JWT tenant
(`ProjectService.EnsureAsync:402-411` matches `p.Key == key && p.OwnerId == ownerId`), so a colliding key
cannot leak across tenants there. **Verdict: safe.**

Auditing the *genuinely* anonymous key paths you pointed me to:

- **register-by-key (`AuthService.RegisterAsync`) — 🔴 RISK FOUND (colliding-key mis-routing).** The
  resolver is:
  ```csharp
  var project = await _unitOfWork.Repository<Project>()
      .Query().IgnoreQueryFilters().AsNoTracking()
      .FirstOrDefaultAsync(p => p.DeletedAt == null && p.Key == projectKeyNormalized); // AuthService.cs:161
  ```
  No `OwnerId` scope, **no ordering**, and project keys are unique only per `(key, owner_id)`
  (`ProjectMapping.cs:24` + partial global index `AddTenancy.cs:147`). If two tenants share a key, a
  registrant is bound to an arbitrary tenant, and the role check (`:179`) + user-owner stamp (`:208`) all
  hang off that arbitrary `projectOwnerId`. Not a direct cross-tenant read, but a non-deterministic
  mis-routing / data-misdirection bug (a signup through a victim's widget can land in an attacker's
  workspace if the attacker pre-registered the same key). **Written up as finding M15 in the report.**
- **demo provisioning (`DemoService.ProvisionAsync`) — ✅ safe.** It does not resolve a user-supplied
  key; it *generates* a unique one (`Key = $"demo-{slug}"`, `DemoService.cs:109`), so there is no
  collision surface.
- **invite accept (`InviteService.AcceptAsync`) — ✅ safe** (adjacent anonymous path). Resolved by an
  unguessable invite code, and the tenant comes from the invite row's own `OwnerId` (`InviteService.cs:301`),
  not from a key — no ambiguity.
- **public branding asset (`BrandingController.GetAsset`) — ✅ safe.** `kind` is whitelisted against
  `IBrandingService.ValidKinds` (`:49`) and the file path is `uploads/branding/{kind}.{ext}` — branding
  is a single **global** resource, not tenant-scoped, so there is no cross-tenant dimension at all.

---

## 2. 🟡 Strict-own tenant-filter list is incomplete
The brief lists strict-own filters as (Project, Comment, Reply, Invite, Subscription, ExtensionSite)
but **omits `User` and `PredefinedActionSuggestion`**, which also use
`IsSuperAdmin || OwnerId == currentUser.TenantId` (`Infrastructure/AppDbContext.cs:33` and `:46`).
The `User` filter is security-relevant (user enumeration / cross-tenant user exposure).

**Fable:** Agreed and audited both.

- **`User`** (`AppDbContext.cs:33`, strict-own) — filter is correct. Every `IgnoreQueryFilters()` read of
  `User` re-scopes explicitly and returns nothing cross-tenant to a caller: login/reset/register lookups
  key on email/`PublicId` for authentication only (`AuthService.cs:52,92,114,190,252`); the seat/demo/
  entitlement counts filter `OwnerId == <tenant>` (`UserService.cs:67`, `DemoService.cs:68,246`,
  `CommentService.cs:59-72`, `EntitlementService.cs:94`); `DemoCleanupService.cs:44` selects only
  `PublicId` of expired demos; `TenantService`/`AdminSeeder` are super-admin/startup paths scoped by
  owner. **No endpoint returns another tenant's user rows.** One caveat that ties into C1, not a filter
  bug: `UserService.CreateAsync:60` stamps `OwnerFor(caller)`, so a **super-admin** using admin-create
  mints a `null`-owner user — another way to grow the null bucket (see report C1).
- **`PredefinedActionSuggestion`** (`AppDbContext.cs:46`, strict-own — deliberately *not* own-plus-global)
  — filter is correct, and `SuggestionService.LoadOwnAsync:175-188` adds an explicit
  `s.OwnerId == _currentUser.TenantId` guard on top of it. **Latent caveat:** for a `null`-owner principal
  that explicit guard also collapses to `owner_id IS NULL`; it is safe **today** only by the invariant
  "a null-owner suggestion is never written" (`AppDbContext.cs:43-45`). Same trap as C1 on a different
  entity — flagged as the latent note under C1 in the report.

Both filters confirmed correct; no cross-tenant user exposure via `IgnoreQueryFilters` found.

---

## 3. 🟡 Where should the report go? / build commands
The brief asks for "a single markdown report" but doesn't name an output path — please **write your
final report to `docs/reviews/fable-backend-review.md`** (create the dir) rather than only returning
it inline. To verify findings locally: `dotnet build` and `dotnet test` (122 tests) from the repo root.

**Fable:** Done. The report is written to **`docs/reviews/fable-backend-review.md`** (dir created), with
an identical copy at the repo root as `BACKEND_REVIEW.md`. Independent second-opinion reviews are in
`REVIEW-GLM.md` (GLM-5.2 via opencode) and were also run by an Opus 4.8 subagent — both verified every
finding against the code (0 rated wrong). Build/test from the repo root: **`dotnet build` succeeded**
(0 errors, 1 pre-existing xUnit analyzer warning in `StakeholderProjectSuggestionTests.cs:317`), and
**`dotnet test` → 122 passed, 0 failed, 0 skipped.**

---

## 4. ✅ Verified accurate in the brief (no action needed)
VM specs (Ubuntu 20.04 ARM, 4 vCPU / 24 GB / 45 GB, Docker 28 / Compose 2.35, PostgreSQL 15.18,
14 tables), the quoted SSH key path, container names + `:8080`-internal / Caddy-only topology, the
read-only + no-secrets ground rules, and the layer/pattern map are all correct.

**Fable:** No action taken. I did not SSH to the VM (the code review didn't require it and I kept to
read-only), so I can't independently re-confirm the hardware/OS specs — but everything code-side matches
the brief: the compose topology (`docker-compose.prod.yml` — API on `:8080` internal, not published;
Caddy the only public entrypoint via `ports: 80/443`), container names, PostgreSQL 15, forwarded-headers
behind Caddy, and the layer/pattern map are all accurate as described.

---

## 5. Open question for Fable
The brief's biggest-risk area is **tenant isolation via EF global query filters + every
`IgnoreQueryFilters()`**. Beyond confirming the filters, please explicitly answer: **is there any
code path (controller/service) that reads a tenant-scoped entity with `IgnoreQueryFilters()` without
re-applying an explicit `OwnerId ==` (or super-admin) guard?** That's the single most likely
cross-tenant breach vector.

**Fable:** Direct answer to the core question: **no `IgnoreQueryFilters()` site reads a tenant-scoped
entity and then *returns* it without an owner/super-admin guard.** Every call site re-scopes. The only
non-clean pattern is a *resolution ambiguity* (not a missing guard): two anonymous by-key resolvers use
`FirstOrDefault(Key == key)` with no owner scope because there is no tenant context yet — that's M15.

I grep'd all ~50 occurrences and verified each. Grouping by pattern (representative `file:line`):

**A. Aggregate counts scoped `OwnerId == <tenant>` — SAFE (no rows returned, owner-pinned):**
`CommentService.cs:59,71,88` · `ProjectService.cs:53` · `UserService.cs:67` · `DemoService.cs:68`
· `ExportImportService.cs:457,472` · `PredefinedActionService.cs:57` · `ExtensionService.cs:61,91`
· `EntitlementService.cs:94` (subscription lookup by owner).

**B. Own-record / explicit-owner loads — SAFE (explicit `OwnerId ==` or `PublicId ==`):**
`PredefinedActionService.cs:94` (`OwnerId==owner && ProjectId==null`) · `PredefinedActionService.cs:175`
(`OwnerId==oid` or `IS NULL` to match the resolved project) · `SuggestionService.cs:179` (`OwnerId==TenantId`)
· `InviteService.cs:153,163,181,195` (own-tenant invite) · `RoleService.cs:188` (reassign target + escalation
guard) · `StatusAdminService.cs:24,61,100` (`OwnerId==owner`) · `DemoService.cs:224,246` (caller by PublicId /
owner-scoped email) · `UserService.cs`/`AuthService.cs` reset+me by `PublicId`.

**C. Anonymous auth bootstrap — SAFE for reads (authenticate by email/PublicId; nothing cross-tenant
returned to the caller):** `AuthService.cs:52` (forgot-password, returns void) · `:92` (reset by PublicId)
· `:114` (login by email) · `:190,252,265` (register user/role scoped by resolved owner) ·
`InviteService.cs:259,273,341,364` (invite by unguessable code; tenant comes from the invite row).

**D. Global-catalog / own-plus-global reads — SAFE (data is intentionally global or merged):**
`PlanService.cs:39,153` (Plan has no filter anyway) · `StatusCatalogService.cs:34`
(`OwnerId == null || == scope`, id+label only) · `DemoService.cs:77` (global "Workspace Admin" role).

**E. Super-admin / background / cascade paths — SAFE (authz-gated + scoped by `OwnerId`/`ProjectId`):**
all `TenantService.cs` sites (controller is `[SuperAdmin]`, each scoped `OwnerId == tenantId`) ·
`ProjectService.cs:223,238,250,261` (admin cascade delete keyed strictly on `ProjectId`, never `OwnerId`)
· `DemoCleanupService.cs:44` (selects `PublicId` of expired demos only) · `AdminSeeder.cs:70,187,204`
(startup) · `UnitOfWork.cs:33,46` (atomic invite-slot claim by `inviteId`) · `SuggestionService.cs:167,198,232`
(project-for-display / admin-notify by `OwnerId == project.OwnerId`).

**F. ⚠️ Resolution ambiguity (the one non-clean pattern) — this is M15, not a missing guard:**
`AuthService.cs:161` (register) and `RoleService.cs:79` (public roles for signup) both resolve
`FirstOrDefault(p => p.Key == keyNormalized)` with **no owner scope** — because the caller is anonymous
and has no tenant yet. They then correctly scope downstream by the *resolved* `projectOwnerId`, but since
keys are unique only per `(key, owner_id)`, a colliding key resolves to an **arbitrary** tenant. Register
mis-routes the new account (M15, Medium); public-roles would list the *wrong* tenant's non-admin role
id+names (same root cause, lower impact — non-admin roles only, no prompt/secret). **Both close if project
keys become globally unique or registration takes an explicit owner id.**

**Bottom line:** the strict-own filters plus the discipline of re-scoping every `IgnoreQueryFilters()` by
`OwnerId`/`ProjectId` hold — no unscoped cross-tenant *read* exists. The residual risks are (i) the
null-owner filter collapse when `TenantId == null` (report **C1**, the real breach vector), and (ii) the
colliding-key resolution ambiguity on the two anonymous bootstrap resolvers (report **M15**).
