# Monetization foundations — IMPLEMENTATION PLAN (GLM)

> Independent plan written against `docs/planning/monetization/REQUIREMENTS.md`, grounded in the
> **actual** `pointer-api` codebase (EF Core + Postgres, Clean Architecture, `Result<T>`, Scrutor
> auto-DI of `*Service`, JWT claims `is_admin`/`is_super_admin`/`tenant`, `BaseEntity` audit,
> global EF query filters, snake_case mappings, orval client codegen). A separate reviewer will
> cross-check this against PLAN-CLAUDE.md.
>
> **All paths are relative to the repo root** unless noted. Code shapes are illustrative of the
> contract/conventions, not final copy-paste.

---

## 0. Guiding principles (non-negotiables from the codebase)

These are the load-bearing conventions every part below obeys — listed once so each section stays short.

1. **`Result<T>` everywhere** (`Application/Response/Result.cs`). New outcomes reuse
   `Success/Failure/NotFound/Conflict/Forbidden`; this plan adds ONE new flag, `IsLimitReached`
   (see §5.3), so dashboards can render an upgrade prompt deterministically.
2. **Scrutor auto-registration** (`Application/DependencyInjection.cs`): any class whose name ends
   in `Service` is wired `AsImplementedInterfaces().WithScopedLifetime()`. New services MUST be
   named `…Service` and have an `I…Service` — no manual DI except for `IBillingProvider`
   (single-instance seam, §10).
3. **EF mappings** live in `Infrastructure/Mappings/*Mapping.cs` as `IEntityTypeConfiguration<T>`,
   auto-applied via `ApplyConfigurationsFromAssembly`. Tables are `snake_case`; `BaseEntity` audit
   columns are mapped by hand in every mapping (see `AppSettingMapping.cs` for the template).
4. **Tenant isolation = query filters** in `Infrastructure/AppDbContext.cs`. Tenant-scoped rows carry
   a non-null `OwnerId == tenant PublicId`; global rows have `OwnerId == null` and NO filter
   (guarded by endpoint authorization, exactly like `AppSetting`).
5. **Controller conventions** (see `API/Controllers/Admin/SettingsController.cs`):
   `[ApiController]`, `api/admin/…` route, `[Authorize(Policy = Policies.SuperAdmin)]`,
   `[Tags("…")]` (drives orval tag-split), `[ProducesResponseType(typeof(InnerType), 200)]`
   annotating the **inner** type (not `Result<T>`), and the global `[Produces("application/json")]`
   filter (set in `Program.cs:14-17`).
6. **Tenant model**: a tenant = the self-owning Workspace-Admin `User` (`OwnerId == PublicId`);
   stakeholders are non-admin `User` rows with `OwnerId == <tenant PublicId>` (see
   `AuthService.RegisterAdminAsync` + `TenantService`). The "tenant id" in claims == that `OwnerId`.
7. **Migrations are additive** (`Infrastructure/Migrations/*`) — new tables/columns only, no
   destructive changes; seeding is idempotent in `API/Seed/AdminSeeder.cs` (runs on boot when
   `DBMigrationEnabled`).
8. **Validators auto-run** on model binding (`AddFluentValidationAutoValidation()` in `Program.cs:22`)
   — new write DTOs get a `…Validator` in `Application/Validators/`.

---

## 1. Phasing overview

All phases get built (per REQUIREMENTS §"Constraints"); the phases are purely for sequencing &
review. Each phase is independently shippable behind the migration.

| Phase | Scope | Ships value without the next? |
|---|---|---|
| **P1-core** | Entities, EF mapping, migration, Free-plan seed, `IEntitlementService` + 7 levers, tenant→plan link, super-admin plan CRUD, public `GET /api/plans`, orval regen, dashboard changes | Yes — full enforcement + dynamic pricing |
| **P1.5** | Signup plan selector, Tenants upgrade endpoint, `IBillingProvider`+`NoopBillingProvider` seam, Subscription billing fields | Yes — upgrade UX, payment-ready |
| **P2** (gated stubs) | `extensionEnabled`/`maxExtensionSites` activation tracking table + endpoints (ready to wire to the real extension when it exists) | Yes — gates return the right answers |
| **Deferred** (DISPLAY-ONLY keys) | Landing display of the ~10 display-only entitlements; enforcement later | n/a |

> **Decision:** I sequence enforcement (P1-core) **before** the signup selector & billing seam
> (P1.5), because enforcement is the riskiest, highest-value work and has zero external
> dependencies. The signup selector and billing seam are additive on top of the same model.

---

## 2. Data model (entities, enums, value objects)

All new files go under `Domain/Entity/` and `Domain/Enums/`. Plan/Subscription extend `BaseEntity`
(audit columns inherited). **`Plan` has NO `OwnerId` and NO query filter (global, super-admin-owned).
`Subscription` is tenant-scoped (`OwnerId == tenant PublicId`, strict-own filter).** See §11.

### 2.1 Enums (`Domain/Enums/`)

```
Domain/Enums/BillingInterval.cs     → enum { Monthly = 0, Yearly = 1 }
Domain/Enums/PlanDisplayState.cs    → enum { Visible = 0, ComingSoon = 1, Hidden = 2 }
Domain/Enums/SubscriptionStatus.cs  → enum { None = 0, PendingActivation = 1, Trialing = 2,
                                              Active = 3, PastDue = 4, Canceled = 5 }
```

> `PendingActivation` covers REQUIREMENTS §6 (paid plan chosen at signup → tenant created, plan
> "pending activation", super-admin activates). `None` is the default before any billing.

### 2.2 `Plan` entity — `Domain/Entity/Plan.cs` (global; super-admin managed)

```csharp
public class Plan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public decimal PriceMonthly { get; set; }            // in `Currency` minor units? → see Risk R3
    public string Currency { get; set; } = "USD";        // ISO 4217
    public BillingInterval Interval { get; set; } = BillingInterval.Monthly;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;           // false = no NEW subscriptions/enforcement frozen
    public PlanDisplayState DisplayState { get; set; } = PlanDisplayState.Visible;

    /// Marketing bullets (display-only). Stored JSONB; order preserved.
    public List<string> FeatureBullets { get; set; } = new();

    /// KEY→VALUE map. KEYS are validated against <see cref="EntitlementKeys"/> on every write;
    /// values are free-form strings parsed by the typed accessors. Stored JSONB.
    public Dictionary<string, string> Entitlements { get; set; } = new();
}
```

**Why a `Dictionary<string,string>` JSONB column (not a child table, not strongly-typed columns)?**
- Keys must stay **fixed-in-code** while **values are super-admin-editable** (REQUIREMENTS §1/§2).
  A flexible JSONB store + an in-code key registry (§4) gives exactly that: the admin edits
  values, the validator rejects unknown keys, and adding a key is a code change (which is the
  requirement — a new key must be enforceable/displayable).
- Avoids a column-per-key migration every time a key is added, and avoids a join per enforcement
  check. (Alternative: a `plan_entitlements` child table `(plan_id, key, value)` — more
  relational/row-editable but more queries. Noted as **R1**; I pick JSONB for simplicity + the
  whole map is always read/written atomically with the plan.)

### 2.3 `Subscription` entity — `Domain/Entity/Subscription.cs` (tenant-scoped)

This entity IS the **tenant→plan link** (REQUIREMENTS §3) **and** the **payment-ready shape**
(REQUIREMENTS §8) in one. The tenant's *effective plan* = this row's `PlanId`, falling back to the
default Free plan when no row exists.

```csharp
public class Subscription : BaseEntity
{
    /// Tenant boundary. == the self-owning admin User.PublicId. Strict-own (never null).
    public Guid OwnerId { get; set; }

    public int PlanId { get; set; }
    public Plan Plan { get; set; } = null!;

    // ── Payment-ready fields (no gateway calls now; §10) ──
    public string? BillingProvider { get; set; }            // null/"" = none; future: "stripe"|"paddle"
    public string? ExternalCustomerId { get; set; }
    public string? ExternalSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.None;
    public DateTime? CurrentPeriodEnd { get; set; }
    public DateTime? TrialEndsAt { get; set; }
}
```

> **Decision (tenant→plan link):** I do **not** add `PlanId` to `User`. The `Subscription` row holds
> it, keeping `User` clean and giving us the billing shape for free. Effective-plan resolution is one
> indexed lookup by `OwnerId` (§5.2). **Alternative considered:** a nullable `User.PlanId` column —
> simpler (no join) but couples plan to the user row and has no room for billing fields, forcing a
> second entity later. Flagged as **R2**.

### 2.4 `ExtensionSite` entity — `Domain/Entity/ExtensionSite.cs` (tenant-scoped, P2)

Only needed to enforce `maxExtensionSites` (distinct domains). Records first-seen origin per tenant.

```csharp
public class ExtensionSite : BaseEntity
{
    public Guid OwnerId { get; set; }     // tenant
    public string Origin { get; set; } = string.Empty;   // scheme + host, normalized lower-case
    public DateTime FirstSeenAt { get; set; }
}
```
Unique index on `(OwnerId, Origin)`. Enforced at the (future) extension-activation endpoint (§5.6).

---

## 3. EF mapping & migration

### 3.1 New mappings (`Infrastructure/Mappings/`)

Follow the `AppSettingMapping.cs` template exactly (manual `BaseEntity` column mapping + snake_case +
indexes). Three new files:

- `Infrastructure/Mappings/PlanMapping.cs`
  - `ToTable("plans")`; `Name` required `HasMaxLength(64)` + unique index; `Currency`
    `HasMaxLength(8)`; `Interval`/`DisplayState`/`IsActive`/`SortOrder` mapped.
  - `FeatureBullets` → `HasColumnType("jsonb")` with a list value-comparer
    (`element.ValueComparer = ...`).
  - `Entitlements` → `HasColumnName("entitlements").HasColumnType("jsonb")` with a
    `Dictionary<string,string>` value-comparer so EF tracks mutations correctly.
- `Infrastructure/Mappings/SubscriptionMapping.cs`
  - `ToTable("subscriptions")`; `OwnerId` required + **unique index** (one subscription per
    tenant); `PlanId` FK → `plans(id)` `OnDelete(Restrict)`; `BillingProvider`/`External*`
    `HasMaxLength(128)`; `Status` mapped.
- `Infrastructure/Mappings/ExtensionSiteMapping.cs` (P2)
  - `ToTable("extension_sites")`; unique index `(owner_id, origin)`.

### 3.2 `AppDbContext.cs` changes (`Infrastructure/AppDbContext.cs`)

1. Add `DbSet<Plan> Plans`, `DbSet<Subscription> Subscriptions`,
   `DbSet<ExtensionSite> ExtensionSites` (the latter P2).
2. **Query filters** — mirror the existing strict-own pattern (`Invite`, line 34):
   ```csharp
   b.Entity<Subscription>()
       .HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
   b.Entity<ExtensionSite>()
       .HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
   // Plan: NO query filter — it is global/super-admin-owned, exactly like AppSetting (line 49).
   ```
   This is the **tenant-isolation correctness** pivot (§11): `Plan` is global (filter-free,
   authorization-guarded); `Subscription`/`ExtensionSite` are strict-own tenant-scoped.

### 3.3 Migration (`Infrastructure/Migrations/<timestamp>_AddPlansAndSubscriptions.cs`)

- Created via `just migrate name="AddPlansAndSubscriptions"` (AGENTS.md).
- **Additive only**: `plans`, `subscriptions` (, `extension_sites` in P2). No changes to `users`.
- `Down` drops the three tables (safe — no existing data depends on them pre-migration).

### 3.4 Free-plan seed from existing AppSetting caps (REQUIREMENTS §3)

Implemented **idempotently in `API/Seed/AdminSeeder.cs`** (runs on boot when `DBMigrationEnabled`,
after `db.Database.MigrateAsync()`). This is preferred over raw SQL in the migration because it can
read the live `AppSetting` values via the existing `ISettingsService` and stay idempotent across
reboots.

**Seeding logic** (`AdminSeeder.SeedAsync`, new step "3) Seed default plan"):

1. Define **`FreePlanDefaults`** in code (`Domain` or a static in the seeder) — the canonical
   Free entitlement values for keys that have **no** existing AppSetting analogue:
   ```
   maxProjects = 3,  maxSeats = 5,  maxCommentsPerMonth = 100,
   extensionEnabled = false,  maxExtensionSites = 1,
   maxPredefinedActionsPerProject = 10,  maxTenantWidePredefinedActions = 10,
   + display-only defaults (retentionDays=90, emailsPerMonth=100, …)
   ```
2. **Override from existing AppSettings** where a semantic map exists (the "current global caps"
   from REQUIREMENTS §3 / product-context). Mapping table:
   | Entitlement key | Seeded from AppSetting (key in `ISettingsService`) | Fallback |
   |---|---|---|
   | `maxCommentsPerMonth` | *(none today — DemoCommentCap is demo-scoped, see R4)* | `FreePlanDefaults` |
   | `emailsPerMonth` | `EmailDailyCap` (`email_daily_cap`, default 250) | 100 |
   | (demo cap remains on `User.DemoCommentCapOverride`/`DemoCommentCap` — unchanged) | — | — |
3. Upsert the **Free** plan row (`Name == "Free"`, `SortOrder = 0`, `IsActive = true`,
   `DisplayState = Visible`, `PriceMonthly = 0`) with the merged entitlement map. Idempotent:
   if a `Free` row exists, leave its entitlements untouched (admin may have edited them).
4. Optionally seed a second **"Pro"** plan as `DisplayState = ComingSoon` (so the landing has
   something to render the ComingSoon state) — **optional, off by default**; I'd ship just Free and
   let the super-admin add plans via CRUD.

> **R4 (flagged risk / open decision):** the existing AppSettings caps are demo/email-scoped, not
> plan-scoped — there is **no** `maxProjects`/`maxSeats` AppSetting today. So "seed Free from
> existing caps" is only a partial map (§3.4 step 2). The bulk of Free entitlements come from
> `FreePlanDefaults`. I treat this as the intended reading ("use existing caps where they exist;
> sensible Free defaults otherwise") and flag it for the reviewer. The demo comment cap
> (`DemoCommentCap`) is **not** migrated into `maxCommentsPerMonth` because it is a demo-tenant TTL
> mechanism, not a general plan limit (see §5.5 coexistence).

---

## 4. Entitlement KEYS fixed-in-code; VALUES super-admin-editable

### 4.1 The fixed key registry — `Application/Common/EntitlementKeys.cs` (new)

A single source of truth for the key strings, their type, whether they are enforced in P1, and a
safe default. **This is the contract the validator (§4.3) and the enforcement layer (§5) both read.**

```csharp
public enum EntitlementType { Int, Bool }

public sealed record EntitlementSpec(
    string Key, EntitlementType Type, bool Enforced, string DefaultValue);

public static class EntitlementKeys
{
    // ── ENFORCED in P1 (the 7 levers) ──
    public const string MaxProjects = "maxProjects";
    public const string MaxSeats = "maxSeats";
    public const string MaxCommentsPerMonth = "maxCommentsPerMonth";
    public const string ExtensionEnabled = "extensionEnabled";
    public const string MaxExtensionSites = "maxExtensionSites";
    public const string MaxPredefinedActionsPerProject = "maxPredefinedActionsPerProject";
    public const string MaxTenantWidePredefinedActions = "maxTenantWidePredefinedActions";

    // ── DISPLAY-ONLY in P1 (shown on landing; enforced later) ──
    public const string RetentionDays = "retentionDays";
    public const string MaxEnvironments = "maxEnvironments";
    public const string MaxActiveInvites = "maxActiveInvites";
    public const string EmailsPerMonth = "emailsPerMonth";
    public const string ExtensionCommentsPerMonth = "extensionCommentsPerMonth";
    public const string MaxPendingSuggestions = "maxPendingSuggestions";
    public const string ExportImportEnabled = "exportImportEnabled";
    public const string PromptSuggestionsEnabled = "promptSuggestionsEnabled";
    public const string CustomStatusesEnabled = "customStatusesEnabled";
    public const string PrioritySupport = "prioritySupport";

    /// The complete, closed set. Adding a key = code change here (the requirement).
    public static readonly IReadOnlyDictionary<string, EntitlementSpec> All = new Dictionary<string, EntitlementSpec>
    {
        [MaxProjects] = new(MaxProjects, EntitlementType.Int, true, "3"),
        // …one entry per key, with Enforced flag + default…
    };

    public static bool IsKnown(string key) => All.ContainsKey(key);
}
```

Convention (REQUIREMENTS §2): `-1` (int) = unlimited; booleans gate features.

### 4.2 Typed accessors — `Application/Services/Interfaces/IEntitlementService.cs` (see §5)

The enforcement layer NEVER reads raw strings; it calls typed getters
(`GetIntAsync(key)`, `GetBoolAsync(key)`) that parse the stored value with the spec default. This is
where "fixed key, editable value" becomes safe: a malformed/missing value falls back to the spec
default rather than throwing.

### 4.3 Write-side validation (keeps keys fixed)

- **Super-admin plan create/update** goes through `PlanWriteDto` + `PlanWriteDtoValidator`
  (`Application/Validators/Plan/`). The validator:
  1. Rejects any entitlement key not in `EntitlementKeys.All` (→ "Unknown entitlement key '{k}'.").
  2. Type-checks each value against the spec (`int.TryParse` for `Int`, bool parse for `Bool`),
     allowing `-1` for ints.
  3. Enforces invariants: `IsActive=false` plans can't be the default signup plan; a plan with
     `DisplayState=Hidden` is fine.
- Because the validator runs on binding (`AddFluentValidationAutoValidation`), a bad payload is a
  `400` before it reaches the service — consistent with every other write DTO in the app.

---

## 5. Enforcement layer (the core work)

Goal (REQUIREMENTS §4): every limited action checks the tenant's plan entitlement BEFORE succeeding
and returns a clear "you've reached your plan's limit — upgrade" result. Built once, reused — **not
copy-pasted into each service**.

### 5.1 `IEntitlementService` — `Application/Services/Interfaces/IEntitlementService.cs` (new)

```csharp
public interface IEntitlementService
{
    /// The 7 enforced levers (mirrors REQUIREMENTS §4 + EntitlementKeys.Enforced).
    public enum Lever { MaxProjects, MaxSeats, MaxCommentsPerMonth,
                        ExtensionEnabled, MaxExtensionSites,
                        MaxPredefinedActionsPerProject, MaxTenantWidePredefinedActions }

    /// Resolves the effective entitlement value for the given tenant + key (typed).
    Task<int>  GetIntAsync(Guid tenantId, string key);
    Task<bool> GetBoolAsync(Guid tenantId, string key);

    /// Enforces a count-based lever. Counts ACTIVE rows for the tenant, compares to the plan limit,
    /// returns Failure(IsLimitReached) if at/over the limit. Grandfather-safe by construction (§5.4).
    Task<Result> EnforceCountAsync(Guid tenantId, Lever lever);

    /// Enforces a boolean feature gate (extensionEnabled). Failure(IsLimitReached) if gated off.
    Task<Result> EnforceFlagAsync(Guid tenantId, Lever lever);
}
```

Registered automatically by Scrutor as `EntitlementService` (§0.2). Implementation lives at
`Application/Services/Implementation/EntitlementService.cs`.

### 5.2 Effective-plan resolution (one indexed lookup)

```csharp
// EntitlementService — cached per request (scoped service → cheap)
private Plan? _plan;  // resolved lazily, once per request per tenant
async Task<Plan> ResolvePlanAsync(Guid tenantId)
{
    if (_plan is not null) return _plan;
    var sub = await _unitOfWork.Repository<Subscription>()
        .Query().AsNoTracking()
        .IgnoreQueryFilters()                  // enforcement may run under any caller
        .Where(s => s.OwnerId == tenantId && s.DeletedAt == null)
        .Select(s => s.PlanId)
        .FirstOrDefaultAsync();
    var planId = sub ?? (await FreePlanIdAsync());   // fall back to the default Free plan
    return _plan = await _unitOfWork.Repository<Plan>()
        .Query().AsNoTracking().FirstAsync(p => p.Id == planId && p.DeletedAt == null);
}
```
`FreePlanIdAsync()` caches the Free plan's id (looked up by `Name == "Free"` once). Typed getters
read `_plan.Entitlements[key]` and parse with the spec default. **No per-call plan row churn.**

### 5.3 A new `Result` flag for upgrade messaging — `Application/Response/Result.cs`

Add **one** field so dashboards/clients can deterministically render an upgrade prompt (instead of
string-matching the message):

```csharp
public bool IsLimitReached { get; protected init; }
public PlanLimit? Limit { get; protected init; }   // null unless IsLimitReached
public sealed record PlanLimit(string Lever, int Current, int Limit, int PlanId);
// + factory:  static Result<T> LimitReached(string msg, PlanLimit limit)
```
The controller still returns it as a `400` (BadRequest) like other `Failure`s — clients that don't
care keep working; clients that do read `limit`. (orval will model `PlanLimit` cleanly.)

### 5.4 "Count active + grandfather on downgrade" — automatic by construction

Every count-based `EnforceCountAsync` counts **only active (non-soft-deleted)** rows for the tenant
using `IgnoreQueryFilters()` + explicit `OwnerId == tenantId` + `DeletedAt == null` — exactly the
shape of the existing demo-cap count in `CommentService.CreateAsync` (lines 67-72). Because the
guard **only blocks the ADD** (it never touches existing rows), downgrade grandfathering is free:

- Tenant had 10 projects on Pro (`maxProjects=20`), downgraded to Free (`maxProjects=3`): the 10
  projects stay; the next create is blocked (`10 >= 3`) with the upgrade message. No data deleted,
  no background reconciliation job. (REQUIREMENTS §4 global rule.)

### 5.5 The 7 enforced levers — where each is checked

Each existing service gains `IEntitlementService` in its constructor (additive) and one guard call
**before** the create. The tenant id is resolved per the action's real isolation boundary (usually
`_currentUser.TenantId`; for comments, the project's owner — see below).

| # | Lever | Enforcement point (file) | Tenant id source | Count query |
|---|---|---|---|---|
| 1 | `MaxProjects` | `ProjectService.CreateAsync` (`Application/Services/Implementation/ProjectService.cs:24`) | `TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id` | active `Project` rows with that `OwnerId` |
| 2 | `MaxSeats` | `InviteService.AcceptAsync` (`…/InviteService.cs:210`) **and** `UserService`/`TenantService` direct-add paths | invite's `OwnerId` | active `User` rows with `OwnerId == tenant` |
| 3 | `MaxCommentsPerMonth` | `CommentService.CreateAsync` (`…/CommentService.cs:35`) | **the project's owner** (`projectOwnerId`, line 46-49) | active `Comment` rows, `OwnerId == projectOwner`, `CreatedAt >= UTC start-of-month` |
| 4 | `ExtensionEnabled` | conceptual extension-activation endpoint (P2 stub, §5.6) | tenant | flag gate (`GetBoolAsync`) |
| 5 | `MaxExtensionSites` | same activation endpoint (P2) | tenant | distinct active `ExtensionSite.Origin` for tenant |
| 6 | `MaxPredefinedActionsPerProject` | `PredefinedActionService.CreateTenantAsync`-equivalent for project scope + `ProjectService.CreateAsync` action loop (`…/ProjectService.cs:55-72`) + `ProjectService.ReconcileActionsAsync` | project's `OwnerId` | active `PredefinedAction` rows, `ProjectId == project`, `OwnerId == owner` |
| 7 | `MaxTenantWidePredefinedActions` | `PredefinedActionService.CreateTenantAsync` (`…/PredefinedActionService.cs:40`) | `TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id` | active tenant-wide `PredefinedAction` rows (`ProjectId == null`, `OwnerId == tenant`) |

**Coexistence with the existing demo cap:** the demo comment cap
(`User.DemoCommentCapOverride`/`ISettingsService.DemoCommentCap`, in `CommentService:53-74`) stays as
the **demo-tenant** tight gate; the new plan cap (`MaxCommentsPerMonth`) is the **general** gate for
all tenants. Both run on comment-create. Demo tenants get the tighter of the two automatically. (R4.)

**Calendar-month choice for lever 3:** I count comments with `CreatedAt >= new DateTime(now.Year,
now.Month, 1, 0,0,0, DateTimeKind.Utc)`. "Rolling 30 days" is the alternative; calendar month is
simpler to reason about and matches "rolling/calendar month" in REQUIREMENTS §4. Flagged as a minor
decision.

### 5.6 P2 extension-activation stubs

`extensionEnabled` + `maxExtensionSites` need an activation concept that doesn't fully exist yet
(the browser extension). I ship the **guard + tracking** ready to wire:
- A `POST /api/extension/activate` (anonymous-ish/widget-style, project-key resolved like
  `ProjectService.EnsureAsync`) that: checks `ExtensionEnabled`; records/looks-up the request origin
  in `ExtensionSite`; enforces `MaxExtensionSites` (grandfather-safe). It returns 200 / a limit
  result. The real extension later calls this; until then the lever is **enforced but inert**
  (nothing triggers it). No fake data is written.

---

## 6. Tenant → plan link

Covered by the `Subscription` entity (§2.3) + effective-plan resolution (§5.2). Summary of the link:

- **On signup (Free):** no `Subscription` row needed — effective plan falls back to the default Free
  plan (§5.2). Zero writes on the hot signup path. (R2 alternative would write `User.PlanId`.)
- **On paid signup (P1.5):** a `Subscription` row is created with `Status = PendingActivation`,
  `PlanId = <chosen>`, `OwnerId = newTenantPublicId`. Super-admin flips `Status = Active`.
- **On super-admin upgrade/downgrade (§7):** upsert the `Subscription` row's `PlanId`.
- **Existing tenants at migration:** get the default Free plan implicitly (no row). This satisfies
  REQUIREMENTS §3 ("Free plan is the baseline for existing tenants") with **zero backfill** — a
  missing subscription == Free. (If we later want explicit rows for reporting, a one-time backfill
  job can insert them; not required for correctness.)

---

## 7. Super-admin plan CRUD + Tenants upgrade

All under `api/admin/plans` + an upgrade action on the existing Tenants controller.

### 7.1 `PlanService` — `Application/Services/Implementation/PlanService.cs` + `IPlanService.cs`

CRUD over `Plan` (global; `IgnoreQueryFilters` not needed since Plan has no filter, but reads are
plain queries). Returns admin DTOs (full entitlement map + `IsActive`/`DisplayState`). Delete =
soft-delete (`DeletedAt = now`); reject delete of the `Free` plan (it's the fallback) and of any plan
with active subscriptions (return Conflict with a count, like `Role.HasUsers`).

### 7.2 `PlansController` — `API/Controllers/Admin/PlansController.cs`

```
[ApiController, Route("api/admin/plans"), Authorize(Policy=Policies.SuperAdmin), Tags("Plans")]
GET    api/admin/plans           → Result<List<PlanAdminResponse>>   (all, incl. Hidden/Inactive)
POST   api/admin/plans           → Result<PlanAdminResponse>          (PlanWriteDto)
PATCH  api/admin/plans/{id}      → Result<PlanAdminResponse>          (PlanWriteDto, partial)
DELETE api/admin/plans/{id}      → Result                              (soft-delete)
```
`ProducesResponseType(typeof(InnerType), 200)` on each (§0.5). Add **`"Plans"`** to the orval
`filters.tags` list (§9.2).

### 7.3 Tenants upgrade — extend `TenantsController` + `TenantService`

New endpoint on the **existing** `API/Controllers/Admin/TenantsController.cs`:

```
PATCH api/admin/tenants/{id:int}/plan   [Tags("Tenants")]  → Result
   body: { planId: int }   // or { planKey: string }
```
`TenantService.ChangePlanAsync(int tenantId, int planId)`:
1. Resolve int id → tenant `PublicId` (the controller already resolves int→PublicId via ListAsync;
   add a lightweight `TenantService.GetPublicIdAsync` to avoid the full list — mirrors the comment
   in `TenantsController.Delete`).
2. Validate the plan exists, `IsActive`, not deleted.
3. Upsert `Subscription` (`OwnerId = tenantPublicId`, `PlanId = planId`).
4. **Route through `IBillingProvider.ChangePlanAsync`** (§10) — no-op now, but the seam is exercised.
5. Entitlements apply immediately on the next request (effective-plan resolution reads the new
   `PlanId`). Grandfathering per §5.4 (no data touched).

Extend `TenantResponse` (`Application/DTOs/Tenant/TenantResponse.cs`) with `PlanId`, `PlanName`,
`SubscriptionStatus` so the Tenants admin UI can show + change the plan. `TenantService.ListAsync`
batch-loads subscriptions (one query, join to plans) — same pattern as its existing project/comment
count batches (lines 46-63).

---

## 8. Public `GET /api/plans` + static landing consumption

### 8.1 Public endpoint — `API/Controllers/PlansPublicController.cs` (new)

```
[ApiController, Route("api/plans"), AllowAnonymous, Tags("Plans")]
GET api/plans → Result<List<PlanPublicResponse>>
```
- No auth. Returns **only public fields**: `name, priceMonthly, currency, interval, sortOrder,
  displayState, featureBullets`. **No entitlement values, no `IsActive`, no ids beyond a stable
  `key`.** (Entitlements are enforcement internals; the landing shows `featureBullets`.)
- Server-side filter: `DisplayState != Hidden` AND `DeletedAt == null`. Order by `SortOrder`.
- Add `"Plans"` to orval tags BUT this anonymous endpoint must also be reachable by the **landing**
  which is not an orval client — so it's a plain JSON `fetch` (§8.2). Still tag it `Plans` for
  consistency; the dashboards can use the generated hook too.
- CORS: it's under `/api/plans` (not `/api/admin/*`) → open default policy applies (landing origin is
  not in the dashboard allow-list). Confirmed against `Program.cs:227-240` (`IsDashboardOnly` only
  matches `/api/admin/*` + sensitive auth). Good — no CORS change needed.

### 8.2 Landing page — `landing/index.html` (static, no build)

Today it's a single bilingual static HTML with a `STRINGS` dict + `data-i18n` and **no dynamic
fetch** (only the dogfood widget script, line 363). Changes (all client-side, no rebuild):

1. Add a `<section id="pricing" class="band"><div class="wrap section">
     <h2 class="section-title" data-i18n="pricing.title">Pricing</h2>
     <div id="pricing-grid" class="pricing-grid"></div>
   </div></section>` (place it after the Features section, before the team split).
2. Add `pricing.*` keys to both `en` and `ar` STRINGS (title, perMonth, comingSoon, ctaSignup,
   mostPopular, free). Bilingual requirement (REQUIREMENTS §5).
3. Add a small `<script>` (vanilla, after `initLang`) that:
   - `fetch('/api/plans')` (relative if served same-origin; else configured `API_BASE`). Wrap in
     try/catch — on failure, hide the section (graceful: never breaks the page).
   - Unwraps the `Result` envelope (`data.data`).
   - Renders one card per plan: name, price (`PriceMonthly` + `Currency` + interval suffix),
     `featureBullets` list, and a CTA (`https://app.pointer.moamen.work?plan=<key>` for Visible;
     greyed "Coming soon" badge + **no CTA** for `ComingSoon`). Hidden plans are already
     server-filtered.
   - Re-applies language on render so bullets localize if provided per-locale (or fall back to the
     stored strings).
4. CSS for `.pricing-grid` (responsive grid, `@media` rules matching the existing breakpoints).

> **No rebuild needed** — the landing is served as-is (it's deployed separately, but the file edit is
> the deliverable). Auto-updates on plan changes is inherent: the fetch runs on every page load.

---

## 9. Signup plan selector + client regen

### 9.1 Signup linkage (workspace signup only) — `AuthService.RegisterAdminAsync` (`…/AuthService.cs:231`)

- Extend `RegisterAdminRequest` (`Application/DTOs/Auth/RegisterAdminRequest.cs`) with
  `public int? PlanId { get; set; }` (optional; default = Free). Add a `RegisterAdminValidator`
  that, if `PlanId` is set, checks the plan exists + `IsActive` + `DisplayState != Hidden`.
- `RegisterAdminAsync`:
  - If `PlanId` is null/Free → exactly today's flow (pending super-admin approval; REQUIREMENTS §6).
  - If `PlanId` is a paid/active plan → after creating the (pending) tenant `User`, create a
    `Subscription` row (`OwnerId = newPublicId`, `PlanId`, `Status = PendingActivation`). The tenant
    is still `ApprovalStatus = Pending`; super-admin approval + a `Subscription.Status = Active`
    flip together. **No payment call** (§10 Noop).
- Stakeholder signup (`register` / `register-invite`) is **unchanged** — they don't choose a plan
  (REQUIREMENTS §6: "workspace signup only — NOT stakeholders").

### 9.2 orval client regeneration

- `orval.config.ts`: add `'Plans'` to `filters.tags` (line 6). The Tenants upgrade endpoint is
  already under `Tenants` (already tagged). The public `GET /api/plans` is also `Plans`.
- Run `npm run generate-clients` (AGENTS.md) with the API on `:8090`. Produces:
  - Angular: `getApiPlansResource` (httpResource) + `PlansService` mutations.
  - React: `useGetApiPlans` / `usePostApiAdminPlans` hooks.
  - Vue: `useGetApiPlans` / `usePostApiAdminPlans` composables.
- Commit `clients/**` + regenerated `openapi.json`.

---

## 10. Payment-READY seam (NO gateway calls now)

Design so Stripe/Paddle later = an adapter + webhook, not a rewrite (REQUIREMENTS §8).

### 10.1 `IBillingProvider` + `NoopBillingProvider`

- `Application/Abstractions/IBillingProvider.cs` (new):
  ```csharp
  public interface IBillingProvider {
      Task ActivateAsync(Subscription sub);     // create customer/subscription at the gateway
      Task ChangePlanAsync(Subscription sub, int newPlanId);
      Task CancelAsync(Subscription sub);
  }
  ```
- `Infrastructure/Billing/NoopBillingProvider.cs` (new): the default. Every method is a no-op that
  only sets local fields (e.g. `ActivateAsync` sets `Status = Active` if currently `PendingActivation`
  and `BillingProvider = null`). **Zero HTTP.**
- Registration: **manual** in `Infrastructure/DependencyInjection.cs` (not Scrutor — it's a
  single-instance seam, and later we'll pick the implementation from config):
  ```csharp
  s.AddScoped<IBillingProvider, NoopBillingProvider>();   // swap to StripeBillingProvider later via config
  ```
- Future Stripe: add `StripeBillingProvider` + `s.AddScoped<IBillingProvider>(sp =>
  config["Billing:Provider"] == "stripe" ? … : …)`. Webhooks land as a new `POST /api/billing/webhooks`
  endpoint that maps events → `Subscription` field updates. **No schema churn** — the fields already
  exist (§2.3).

### 10.2 Global billing settings (for later)

Add to `ISettingsService` const keys (mirroring the email pattern, §0): `DefaultSignupPlan`
(`default_signup_plan`), `TrialDays` (`trial_days`), `Currency` (`currency`). Billing **keys/secrets
stay env-only + masked** exactly like `Email:ApiKey` (see `SettingsController:61`
`EmailApiKeyConfigured` — report presence only, never the value). No new secrets in the DB.

### 10.3 Wiring

`TenantService.ChangePlanAsync` and the paid-signup path call `IBillingProvider` methods (§7.3, §9.1).
Noop today; the calls are the seams a real provider replaces.

---

## 11. Tenant-isolation correctness

The single most important invariant (REQUIREMENTS: "Plan is global/super-admin-owned; Subscription
is tenant-scoped"). Concretely, in THIS codebase:

| Entity | `OwnerId` | EF query filter (`AppDbContext.cs`) | Write path | Read path |
|---|---|---|---|---|
| `Plan` | **none** (global) | **none** (like `AppSetting`, line 49) | super-admin only (`Policies.SuperAdmin`) | public endpoint returns a safe projection; admin endpoint returns full |
| `Subscription` | `== tenant PublicId` (strict-own, never null) | `IsSuperAdmin \|\| OwnerId == TenantId` (like `Invite`, line 34) | super-admin (upgrade) or system (signup) | enforcement resolves by explicit `OwnerId` with `IgnoreQueryFilters` (safe — it's a count, not a cross-tenant read) |
| `ExtensionSite` | `== tenant PublicId` | strict-own | activation endpoint | same |

**Why Plan has no filter:** a global query filter on `Plan` keyed to `TenantId` would hide all plans
from every tenant (Plan has no tenant). Instead it's global and guarded by **authorization**
(`Policies.SuperAdmin` on `api/admin/plans`; anonymous-safe projection on `GET /api/plans`). This
mirrors `AppSetting` precisely (line 49 comment: "no filter — not tenant data; guarded by endpoint
authorization"). `IEntitlementService.ResolvePlanAsync` reads plans with a plain query (filter-free)
— correct.

**Why Subscription IS strict-own:** a tenant must never read another tenant's subscription. The
filter + the unique `(OwnerId)` index guarantee one row per tenant and no cross-tenant visibility.

**No leakage via enforcement counts:** `EnforceCountAsync` uses `IgnoreQueryFilters()` + an explicit
`OwnerId == tenantId` predicate (the same belt-and-suspenders pattern as
`PredefinedActionService.LoadOwnTenantWideAsync`, lines 75-85). It returns only a count/bool, never
row data.

---

## 12. Dashboard changes (×3: Angular / React / Vue)

The dashboards are a **separate repo** (`pointer-dashboard`) consuming the generated orval clients
(AGENTS.md). After `npm run generate-clients` (§9.2) each gets new typed hooks/services. Changes per
dashboard (mirror across all three):

1. **Plans admin page** (super-admin only): list/create/edit/delete plans; edit entitlement values
   (typed form bound to `PlanEntitlementsDto`), toggle `IsActive`/`DisplayState`, manage
   `FeatureBullets`. Uses `PlansService` (Angular) / `useGet/Post/Patch/DeleteApiAdminPlans` (React/Vue).
2. **Tenants admin page**: show each tenant's `PlanName` + `SubscriptionStatus`; a "Change plan"
   action calling `PATCH api/admin/tenants/{id}/plan`. Uses the regenerated Tenants tag.
3. **Upgrade prompts on limit errors**: an HTTP interceptor (Angular) / mutator (React `mutator.ts`,
   Vue `mutator.ts`) detects `IsLimitReached` in the `Result` envelope and surfaces an "Upgrade your
   plan" toast/banner with the `PlanLimit` payload (`lever`, `current`, `limit`). The envelope
   unwrapping already lives in those mutators/interceptors (AGENTS.md §"How each client consumes").
4. **Signup flow** (if the dashboard owns the workspace-signup form): add a plan selector
   (populated from `GET /api/plans`, `Visible` only) posting `planId` to `register-admin`.

> These are tracked in the dashboard repo, not here. The API deliverable is the regenerated client
> packages + the endpoints they call.

---

## 13. Migration & rollout

1. **Branch** the work; build incrementally per §1 phases.
2. **Migration** (`AddPlansAndSubscriptions`) is additive → safe to apply to prod ahead of code
   (tables just sit empty until the seeder runs). `DBMigrationEnabled=true` on boot runs
   `AdminSeeder` → seeds the Free plan from `FreePlanDefaults` + existing AppSettings (§3.4).
3. **Existing tenants** keep working: no `Subscription` row → effective plan = Free → enforcement
   begins applying Free limits to them immediately. ⚠️ **R5 (rollout risk):** if existing tenants
   currently have more projects/seats than the Free defaults (e.g. `maxProjects=3`), the
   grandfather rule means their **existing** data is untouched, but their **next create** is blocked.
   Mitigation options (pick before deploy): (a) set generous Free defaults that exceed all current
   tenants; (b) add a one-time backfill that creates a `Subscription` on a transitional "Legacy/Free"
   plan with unlimited-ish limits for pre-migration tenants; (c) soft-launch enforcement behind a
   setting (`ISettingsService.EnforcementEnabled`, default false, flip on after soak). **I recommend
   (c) for the first deploy** — ship the model + CRUD + landing first, flip enforcement on after
   verifying counts.
4. **Deploy order:** migrate DB → start API (seeds Free) → deploy dashboards (regen clients) →
   update landing. No downtime; additive only.
5. **Rollback:** the migration `Down` drops the new tables; code rollback to pre-plan build. Existing
   behavior is unchanged because no enforcement existed before. Safe.

---

## 14. End-to-end verification plan

Run via `just test` (unit/integration) + manual/Playwright for the landing & flows. New tests land
in `Tests/` mirroring existing service-test conventions.

### 14.1 Data model / mapping
- EF mapping snapshot updates (`AppDbContextModelSnapshot.cs`) include `plans`/`subscriptions`/
  `extension_sites` with correct columns, the `jsonb` columns, unique indexes (`plans.name`,
  `subscriptions.owner_id`, `extension_sites(owner_id,origin)`), FK `subscriptions.plan_id → plans`.
- Migration up/down round-trips on a clean DB and a populated DB.

### 14.2 Entitlement key contract
- `EntitlementKeys.All` contains exactly the enforced 7 + the listed display-only keys.
- `PlanWriteDtoValidator`: rejects unknown keys; rejects non-int for `Int` specs; accepts `-1`;
  rejects bad bool values. (unit)

### 14.3 Free-plan seed
- Fresh DB → exactly one `Free` plan with merged entitlements (defaults overridden by any existing
  AppSetting per §3.4 map). Re-run seeder → idempotent (no duplicate, values unchanged). (integration)

### 14.4 Enforcement — each of the 7 levers (the bulk of tests)
For each: set the tenant's plan limit to N; create N active rows → next create is
`IsLimitReached` with correct `PlanLimit{Current=N, Limit=N}`; soft-delete one → create succeeds
again; downgrade from a high limit to a low limit with N existing rows → existing rows untouched,
next create blocked (grandfather). Concretely:
- `MaxProjects`: `ProjectServiceTests`.
- `MaxSeats`: `InviteServiceTests` (accept path) — invite accept blocked at limit.
- `MaxCommentsPerMonth`: `CommentServiceTests` — calendar-month boundary (a comment dated last
  month doesn't count).
- `ExtensionEnabled`/`MaxExtensionSites`: `ExtensionActivationTests` (P2) — flag off → blocked;
  distinct origins counted.
- `MaxPredefinedActionsPerProject`: `ProjectServiceTests` + `PredefinedActionServiceTests`.
- `MaxTenantWidePredefinedActions`: `PredefinedActionServiceTests`.
- **Negative isolation:** tenant A at limit does NOT block tenant B (separate counts). (integration)

### 14.5 Effective-plan resolution
- Tenant with no `Subscription` → Free. Tenant with `Subscription.PlanId=X` → plan X. Cached per
  request (second call no extra query). (unit/integration)

### 14.6 Super-admin plan CRUD
- Create/Update/Delete via `PlansController`; delete Free → Conflict; delete plan with active
  subscription → Conflict with count; non-super-admin → 403. (integration)

### 14.7 Tenants upgrade
- `PATCH api/admin/tenants/{id}/plan` upserts subscription; next enforcement call uses new limits;
  existing over-limit data preserved. (integration)

### 14.8 Public `GET /api/plans`
- Returns only public fields; excludes `Hidden`; excludes soft-deleted; anonymous (no auth) 200.
- CORS: a cross-origin fetch from a non-dashboard origin succeeds (open default policy). (integration/manual)

### 14.9 Landing (Playwright, `web-component`-style or `.playwright-cli/`)
- `/api/plans` reachable; cards render for Visible plans; `ComingSoon` plan renders greyed with no
  CTA; `Hidden` plan absent; AR locale renders Arabic copy; fetch failure hides the section cleanly.

### 14.10 Signup selector
- `register-admin` without `PlanId` → today's pending flow, no subscription row.
- With paid `PlanId` → tenant created + `Subscription(Status=PendingActivation)`; super-admin
  activate → `Status=Active`.
- Stakeholder `register`/`register-invite` ignore plan entirely. (integration)

### 14.11 Payment seam
- `NoopBillingProvider.ActivateAsync` flips `PendingActivation→Active`, no HTTP.
- Swap to a fake `TestBillingProvider` → `ChangePlanAsync` invoked on upgrade (verify via spy).
- No external HTTP in any P1/P1.5 test (grep the diff for `HttpClient`/`http://`).

### 14.12 orval regen + types
- `npm run generate-clients` succeeds; `clients/{angular,react,vue}` export `Plans` tag hooks;
- `openapi.json` updated and committed; build (`npm run build` in clients where present) passes.

### 14.13 Lint/format/CI gate
- `just fmt` (CSharpier), `just test`, build the solution (`dotnet build`).
- No new warnings; controllers annotate inner types (§0.5).

---

## 15. Risks & open decisions (flagged for reviewer)

- **R1 — JSONB vs child table for entitlements.** I chose JSONB on `Plan` (atomic read/write, no
  join, key-set validated in code). Alternative: `plan_entitlements(plan_id,key,value)` for
  row-level editing. Reviewer: confirm JSONB is acceptable; the validator is the key-set gate either way.
- **R2 — tenant→plan link location.** I put `PlanId` on `Subscription` (not `User`), giving the
  billing shape for free and keeping `User` clean. Alternative: `User.PlanId` (simpler, no join).
  Reviewer: confirm.
- **R3 — `PriceMonthly` units.** I left it `decimal` with a `Currency` code. Decide minor-units vs
  decimal before the dashboard form is built. Low impact now (display-only until a gateway lands).
- **R4 — "seed Free from existing AppSetting caps" is a partial map.** Existing caps are
  demo/email-scoped; most Free entitlements come from `FreePlanDefaults`. Demo comment cap is NOT
  migrated into `maxCommentsPerMonth` (different mechanism). Reviewer: confirm reading.
- **R5 — rollout grandfathering for existing tenants.** Existing tenants may exceed new Free limits;
  their existing data is safe but next-create blocks. Recommended mitigation: ship enforcement
  behind `EnforcementEnabled` setting (off → on after soak), OR a transitional Legacy plan via
  one-time backfill. Reviewer: pick the rollout strategy.
- **R6 — `maxExtensionSites` tracking.** Requires a new `ExtensionSite` table + an activation
  concept that doesn't fully exist yet (browser extension). Shipped as a ready-to-wire guard (P2);
  enforced-but-inert until the extension calls it. Reviewer: confirm P2 placement is acceptable for
  "implement ALL phases".
- **R7 — demo cap vs plan cap coexistence.** Both gates run on comment-create for demo tenants
  (tighter wins). Reviewer: confirm we don't collapse the demo cap into the plan cap.
- **R8 — calendar vs rolling month for `maxCommentsPerMonth`.** I chose calendar month (UTC).
  Reviewer: confirm.
- **R9 — `IsLimitReached` Result addition.** Adds one field + `PlanLimit` record to the envelope.
  orval regen propagates it. Reviewer: confirm this is preferred over message-string-matching.

---

## 16. File map (new + changed)

**New:**
- `Domain/Entity/Plan.cs`, `Subscription.cs`, `ExtensionSite.cs`
- `Domain/Enums/BillingInterval.cs`, `PlanDisplayState.cs`, `SubscriptionStatus.cs`
- `Infrastructure/Mappings/PlanMapping.cs`, `SubscriptionMapping.cs`, `ExtensionSiteMapping.cs`
- `Infrastructure/Billing/NoopBillingProvider.cs`
- `Application/Abstractions/IBillingProvider.cs`
- `Application/Common/EntitlementKeys.cs`
- `Application/Services/Interfaces/IPlanService.cs`, `IEntitlementService.cs`
- `Application/Services/Implementation/PlanService.cs`, `EntitlementService.cs`
- `Application/DTOs/Plan/*` (PlanWriteDto, PlanAdminResponse, PlanPublicResponse, PlanEntitlementsDto)
- `Application/Validators/Plan/PlanWriteDtoValidator.cs`
- `API/Controllers/Admin/PlansController.cs`
- `API/Controllers/PlansPublicController.cs`
- `Infrastructure/Migrations/<ts>_AddPlansAndSubscriptions.cs` (+ `.Designer`, snapshot bump)
- `Tests/` — `EntitlementServiceTests`, `PlanServiceTests`, enforcement tests per lever

**Changed:**
- `Infrastructure/AppDbContext.cs` — 3 DbSets + 2 query filters (Subscription, ExtensionSite)
- `Application/Response/Result.cs` — `IsLimitReached` + `PlanLimit`
- `Application/Services/Implementation/ProjectService.cs` — `MaxProjects` + project-action caps guards
- `Application/Services/Implementation/CommentService.cs` — `MaxCommentsPerMonth` guard
- `Application/Services/Implementation/PredefinedActionService.cs` — tenant-wide cap guard
- `Application/Services/Implementation/InviteService.cs` — `MaxSeats` guard on accept
- `Application/Services/Implementation/AuthService.cs` — signup plan selector (`RegisterAdminAsync`)
- `Application/Services/Implementation/TenantService.cs` — `ChangePlanAsync` + `ListAsync` plan join
- `Application/DTOs/Auth/RegisterAdminRequest.cs` — `PlanId`
- `Application/DTOs/Tenant/TenantResponse.cs` — `PlanId`/`PlanName`/`SubscriptionStatus`
- `API/Controllers/Admin/TenantsController.cs` — `PATCH {id}/plan`
- `API/Seed/AdminSeeder.cs` — Free-plan seed step
- `Infrastructure/DependencyInjection.cs` — `IBillingProvider` → `NoopBillingProvider`
- `Application/Resources/MessageKeys.cs` — `Plan.*` (NotFound, LimitReached, etc.)
- `orval.config.ts` — add `'Plans'` tag
- `landing/index.html` — pricing section + fetch script + i18n keys
- `clients/**`, `openapi.json` — regenerated
