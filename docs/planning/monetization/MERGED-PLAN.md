# Monetization foundations — MERGED PLAN (Claude + GLM, aligned)

Authoritative build spec. Both planners cross-reviewed and **converged** (see XREVIEW-*.md). This is
Claude's spine + GLM's grafts + shared decisions. Read PLAN-CLAUDE.md / PLAN-GLM.md for the long-form
detail; where they conflict, THIS doc governs. **No payment gateway integration; payment-READY seam only.**

## Data model
- **`Plan : BaseEntity`** — GLOBAL, no `OwnerId`, **no query filter** (authz-guarded like `AppSetting`).
  Fields: `Name`, **`Slug`** (stable machine id for `?plan=…` / signup), `PriceMonthly` (decimal),
  `Currency`, `Interval` (`Monthly|Yearly`), `SortOrder`, `IsActive`, `DisplayState`
  (`Visible|ComingSoon|Hidden`), `FeatureBullets` (List<string>), **`Entitlements`** = a typed
  `PlanEntitlements` owned VO via **`OwnsOne(...).ToJson("entitlements")`** (matches `Comment.Element`;
  no manual converter). [C1]
- **`PlanEntitlements` VO** — one named property per fixed key. **All int props are `int?`** (or resolved
  via a getter) so a MISSING key → **spec default, never `0`** [G7 — lockout fix]. Enforced (P1):
  `MaxProjects, MaxSeats, MaxCommentsPerMonth, ExtensionEnabled, MaxExtensionSites,
  MaxPredefinedActionsPerProject, MaxTenantWidePredefinedActions`. Display-only: `RetentionDays,
  MaxEnvironments, MaxActiveInvites, EmailsPerMonth, ExtensionCommentsPerMonth, MaxPendingSuggestions,
  ExportImportEnabled, PromptSuggestionsEnabled, CustomStatusesEnabled, PrioritySupport`. `-1`=unlimited.
- **`EntitlementCatalog`** (static) — metadata per key (label, kind Int/Bool, `Enforced`, `Default`).
  Single source consumed by the plan-write validator, enforcement, landing labels, and seeding. [C2]
  A unit test asserts the VO property set == the catalog keys (keeps them in sync).
- **`Subscription : BaseEntity`** — TENANT-SCOPED, strict-own filter (`superAdmin || OwnerId==TenantId`),
  **one per tenant** (`OwnerId` unique). Fields: `PlanId` (FK, Restrict), `Status`
  (`None|PendingActivation|Trialing|Active|PastDue|Canceled` [G3]), `BillingProvider?`,
  `ExternalCustomerId?`, `ExternalSubscriptionId?`, `CurrentPeriodEnd?`, `TrialEndsAt?`. Effective plan =
  this row's Plan; **missing row ⇒ Free**. [C7]
- **`ExtensionSite : BaseEntity`** — tenant-scoped; `OwnerId, Origin (normalized), FirstSeenAt`, unique
  `(OwnerId, Origin)`. Backs `MaxExtensionSites`. [G2]
- Enums: `BillingInterval`, `PlanDisplayState`, `SubscriptionStatus`.

## `Result` change [G1]
Add `IsLimitReached` (bool) + `PlanLimit { Lever, Current, Limit, PlanId }` + `LimitReached(msg, limit)`
factory to `Application/Response/Result.cs`. Enforcement returns it (as a 400). Clients detect the flag
(never string-match) to render "3/5 projects — upgrade". orval regen propagates `PlanLimit`.

## Enforcement — `IEntitlementService` [C4 + G7 + G8]
- `GetForTenantAsync(tenantId) → PlanEntitlements` — resolves Subscription→Plan (missing ⇒ Free),
  cached per request. Reads via plain query (Plan is filter-free) / `IgnoreQueryFilters`+explicit
  `OwnerId` for Subscription (count/lookup only, never cross-tenant rows).
- `CheckCountAsync(key, currentCount) → Result` — **compare-only**; caller passes the count (caller owns
  the entity+filter). Returns `LimitReached` when `limit != -1 && currentCount >= limit`. [C4 — keeps the
  service decoupled from every counted entity.]
- `EnforceFlagAsync(key) → Result` for booleans (`ExtensionEnabled`).
- Missing/malformed entitlement value → **spec default** (never 0/false). [G7]
- **Grandfather rule (global):** count only `DeletedAt==null` rows; the check runs ONLY on create, never
  touches existing rows → downgrade keeps existing data, blocks the next add. (Matches `CommentService.cs:67-70`.)
- **`EnforcementEnabled` kill-switch** (`ISettingsService`, default **false**): when off, `CheckCountAsync`/
  `EnforceFlagAsync` always pass. Deploy with it off, verify, flip on after soak. [G9, layered on C3]

### Enforced levers — sites (corrected)
| Lever | Site | Count |
|---|---|---|
| MaxProjects | `ProjectService.CreateAsync` | active Projects, `OwnerId==tenant` |
| MaxSeats | `InviteService.AcceptAsync` + `UserService.CreateAsync` (direct add) | active Users, `OwnerId==tenant` |
| MaxCommentsPerMonth | `CommentService.CreateAsync` | active Comments, `OwnerId==projectOwner`, `CreatedAt >= new DateTime(y,m,1,0,0,0,DateTimeKind.Utc)` [G8] |
| ExtensionEnabled / MaxExtensionSites | `POST /api/extension/activate` (P2 stub; enforced-but-inert until the real extension calls it) [G2] | flag / distinct `ExtensionSite.Origin` |
| MaxPredefinedActionsPerProject | **`ProjectService.cs` create-loop (`:55-72`) + `ReconcileActionsAsync` (`:289-334`)** [corrected] | active PredefinedActions, `ProjectId==p` |
| MaxTenantWidePredefinedActions | `PredefinedActionService.CreateTenantAsync` (`:40`) | active tenant-wide (`ProjectId==null`) |
- **Demo-cap coexistence** [G5]: the existing demo comment cap (`CommentService.cs:53-74`) stays; both run
  on comment-create for demo tenants (tighter wins). Demo cap is NOT migrated into `MaxCommentsPerMonth`.

## Seeding & existing tenants [C3 + G4 + G6]
- **Seed in `AdminSeeder`** (boot hook, `Program.cs:117-122`, idempotent try/catch), NOT raw SQL — so it
  can read live AppSettings and stay idempotent. New `PlanSeeder` step:
  1. `FreePlanDefaults` in code for all keys (e.g. maxProjects=3, maxSeats=5, maxCommentsPerMonth=100,
     extensionEnabled=false, maxExtensionSites=1, maxPredefinedActionsPerProject=10,
     maxTenantWidePredefinedActions=10, + display-only defaults).
  2. **Honest partial map** from existing AppSettings: only `emailsPerMonth ← EmailDailyCap`. There is NO
     maxProjects/maxSeats/etc. AppSetting today — the rest come from `FreePlanDefaults`. [G6]
  3. Upsert **Free** (`Slug=free`, price 0, Visible). Idempotent: if Free exists, leave entitlements
     (admin may have edited). Optionally seed nothing else — super-admin adds Pro/Team via CRUD.
  4. Seed a hidden **`Legacy`** plan (`Slug=legacy`, `IsActive=false, DisplayState=Hidden`, all `-1`)
     and **backfill `Subscription(Legacy, Status=Active)` for every existing tenant** so they're never
     retroactively limited. [C3] New signups default to Free.
- Migration `AddPlansAndSubscriptions` (+ `extension_sites`): additive; `Down` drops the tables.

## Super-admin plan CRUD [C2/C5/C6]
`PlansController` `[Authorize(SuperAdmin)] [Tags("Plans")]` `api/admin/plans`: GET(all), POST, PATCH{id},
DELETE{id}. `PlanWriteDtoValidator` (auto-runs) rejects unknown entitlement keys (via catalog),
type-checks values (allow `-1`). DELETE = soft; **block deleting Free (fallback) or any plan with active
Subscriptions** → Conflict+count (mirror role-delete-reassign; require moving tenants first). [C6]

## Tenant → plan link + upgrade [C7]
- `PATCH /api/admin/tenants/{id}/plan {planId}` (super-admin) → `TenantService.ChangePlanAsync`: upsert the
  tenant's Subscription.PlanId, route through `IBillingProvider.ChangePlanAsync` (Noop), entitlements apply
  next request (grandfathered). `TenantResponse` gains `PlanName`+`SubscriptionStatus` (batch-loaded in ListAsync).

## Public endpoint + landing [agreed]
- `GET /api/plans` `[AllowAnonymous] [Tags("Plans")]`, light rate-limit, **open CORS** (verified `/api/plans`
  is not in the dashboard-only set). Returns marketing fields only (`slug,name,priceMonthly,currency,interval,
  featureBullets,displayState,sortOrder`) for `DisplayState != Hidden`, ordered by SortOrder. No entitlement
  values/ids.
- **Landing** (`landing/index.html`, static): add a `<section id="pricing">` (after Features), `pricing.*`
  STRINGS keys (en+ar), and a vanilla `fetch('/api/plans')` script that renders cards (price + bullets +
  CTA `app.pointer.moamen.work?plan=<slug>`); `ComingSoon` → greyed + badge + no CTA; graceful hide on fetch
  failure. Auto-updates every load, no rebuild.

## Signup selector [agreed + G3]
`RegisterAdminRequest` gains optional `PlanId`/`planSlug` (workspace signup only; stakeholders unchanged).
Free → today's flow. Paid → create tenant + `Subscription(Status=PendingActivation)`; super-admin activates
(approval flip + `IBillingProvider.ActivateAsync` Noop → `Active`). Dashboard signup fetches `/api/plans`.

## Payment-READY seam [agreed]
`IBillingProvider { ActivateAsync; ChangePlanAsync; CancelAsync }` + `NoopBillingProvider` (local status
only, zero HTTP), **manual DI** in `Infrastructure/DependencyInjection.cs`. Later Stripe = a new adapter +
`POST /api/billing/webhooks`; no schema churn (Subscription already has the external ids/status/period).
Global settings (super-admin DB): `defaultSignupPlan`, `trialDays`, `currency`; provider keys env-only+masked.

## Dashboards (×3, separate repo) [agreed]
Plans admin page (CRUD + entitlement form from catalog + state toggles + feature bullets); Tenants page
plan dropdown (upgrade/downgrade) + PlanName/Status; interceptor/mutator detects `IsLimitReached` → upgrade
toast/banner with `PlanLimit`; signup plan selector. i18n en+ar. Built after `npm run generate-clients`
(add `'Plans'` tag to orval.config.ts).

## Isolation [agreed]
Plan global (no filter, authz-guarded like AppSetting); Subscription + ExtensionSite strict-own (like
Invite). Enforcement counts via `IgnoreQueryFilters` + explicit `OwnerId` (count/bool only, never rows).
`/api/plans` exposes marketing fields only.

## Build order (all phases; sequencing only)
1. Model + enums + mappings + migration + `EntitlementCatalog` + `Result.IsLimitReached`.
2. `PlanSeeder` (Free + Legacy backfill) in AdminSeeder.
3. `IEntitlementService` + `EnforcementEnabled` kill-switch + wire the 7 levers.
4. `PlanService` + `PlansController` (admin CRUD) + Tenants `ChangePlanAsync` + public `GET /api/plans`.
5. Signup selector + `IBillingProvider`/Noop seam + global settings keys.
6. `ExtensionSite` + activation endpoint (enforced-but-inert).
7. orval regen (`Plans` tag) → dashboards ×3 → landing pricing section.
8. Full E2E (below) → deploy (kill-switch OFF) → flip `EnforcementEnabled` on after soak.

## E2E (before deploy)
Per-lever: set limit N, create N, next create → `IsLimitReached{Current=N,Limit=N}`; soft-delete one →
succeeds; downgrade-with-N-existing → existing kept, next blocked (grandfather). Kill-switch OFF → no
enforcement; ON → enforced. Legacy-backfilled existing tenant → unlimited. Super-admin CRUD (delete Free/
in-use → Conflict; non-super → 403). Upgrade lifts limit immediately. `GET /api/plans` hides Hidden/
soft-deleted, anonymous 200, cross-origin OK. Landing renders Visible + ComingSoon(greyed,no CTA), AR
locale, graceful fetch-fail. Signup paid → PendingActivation → activate → Active; stakeholder signup
ignores plan. Noop billing flips states, zero HTTP. Tenant isolation intact (no cross-tenant plan/sub read).
Full unit + integration; `dotnet build` + tests green; GLM diff-review before merge.
