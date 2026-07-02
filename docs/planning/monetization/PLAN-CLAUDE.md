# Monetization foundations — PLAN (Claude), independent draft vs REQUIREMENTS.md

## Data model
- **`Plan : BaseEntity`** — GLOBAL (super-admin-owned; no tenant filter, like a public catalog). Fields:
  `Name`, `Slug` (stable machine id used by landing/signup), `PriceMonthly` (decimal), `Currency`,
  `Interval` enum (`Monthly|Yearly`), `SortOrder`, `IsActive` (subscribable/enforced), `DisplayState`
  enum (`Visible|ComingSoon|Hidden`), `FeatureBullets` (JSON `List<string>`, display-only marketing),
  and **`Entitlements`** — a typed owned value object serialized to one JSON column (`OwnsOne(...).ToJson`,
  same pattern as `Comment.Element`).
- **`PlanEntitlements`** (owned VO) — one **named C# property per fixed key** (compile-safe, no magic
  strings, adding a lever = add a property, NO per-key migration since it's JSON):
  enforced → `MaxProjects, MaxSeats, MaxCommentsPerMonth, ExtensionEnabled(bool), MaxExtensionSites,
  MaxPredefinedActionsPerProject, MaxTenantWidePredefinedActions`; display-only → `RetentionDays,
  MaxEnvironments, MaxActiveInvites, EmailsPerMonth, ExtensionCommentsPerMonth, MaxPendingSuggestions,
  ExportImportEnabled, PromptSuggestionsEnabled, CustomStatusesEnabled, PrioritySupport`. Convention:
  int `-1` = unlimited; bool for gates. **Keys are fixed = the property set** → super-admin edits values,
  never keys (they physically can't add a property via the API).
- **`Subscription : BaseEntity`** — TENANT-SCOPED (strict-own query filter, one per tenant, `OwnerId`
  unique). Fields: `PlanId`, `Status` enum (`None|Trialing|Active|PastDue|Canceled`), `BillingProvider`
  (string?, null now), `ExternalCustomerId?`, `ExternalSubscriptionId?`, `CurrentPeriodEnd?`,
  `TrialEndsAt?`, `ActivatedAt?`. A tenant's **effective plan = its Subscription.Plan** (default Free).

## Entitlement registry (fixed keys, one source of truth)
Static `EntitlementCatalog` describing each key: display label, kind (Int/Bool), `Enforced` flag,
default. Consumed by: super-admin CRUD validation (sane values, unlimited only where allowed), the public
landing DTO (labels), the enforcement service (lookup), and seeding. This is the ONLY place a key is
declared; the typed VO mirrors it.

## Seeding & existing tenants (migration)
- Seed default plans: **Free** (real limits), **Pro**, **Team** (`Visible`), each with entitlement values;
  Free's caps informed by today's implicit usage. Free comment/predefined defaults reuse the current
  `AppSetting` values where sensible.
- **Existing tenants must NOT be retroactively restricted.** Create an internal **`Legacy`** plan
  (`IsActive=false, DisplayState=Hidden`, unlimited entitlements) and backfill a `Subscription(Legacy,
  Status=Active)` for every current tenant. New signups default to **Free**. (Clean, no surprise lockouts.)

## Enforcement layer (the real work) — `IEntitlementService`
- `GetForTenantAsync(tenantId)` → the tenant's effective `PlanEntitlements` (resolve Subscription→Plan;
  cache per-request via a scoped field).
- `CheckAsync(key, currentActiveCount)` → `Result` OK / `Failure(MessageKeys.Plan.LimitReached, {key})`.
  `-1` ⇒ always OK. Booleans: `IsEnabled(key)`.
- **Grandfather rule (global):** the check is `activeCount < limit` (count only `DeletedAt==null`). A
  downgrade never deletes; it only makes the next `Check` fail with an upgrade message.
- Enforcement calls (P1): `ProjectService.CreateAsync` (MaxProjects) · `UserService.CreateAsync` +
  invite accept + `InviteService`/seat additions (MaxSeats) · `CommentService.CreateAsync`
  (MaxCommentsPerMonth = count `OwnerId==tenant && CreatedAt>=firstOfMonthUtc`) · widget/extension paths
  (`ExtensionEnabled`, `MaxExtensionSites` — track activated domains; simplest: count distinct comment
  source domains or a per-tenant extension-sites set) · `ProjectService` reconcile + `PredefinedActionService.CreateTenantAsync`
  (the two prompt caps). Each returns a `Conflict`/`Failure` with an upgrade message the dashboards surface.

## Super-admin plan management
- `PlansController` `[Authorize(Policy=SuperAdmin)]` `[Tags("Plans")]`: `GET` (all incl. hidden), `POST`
  create, `PATCH {id}` update (name/price/state/sortOrder/featureBullets/entitlement values), `DELETE {id}`
  — soft-delete BUT block if any active Subscription references it (require moving those tenants first,
  mirroring role-delete-reassign). `PATCH` validates entitlement values via the catalog.
- `PlanEntitlementsDto` mirrors the VO; CRUD only accepts known properties (unknown JSON ignored by binding).

## Public endpoint + landing (auto-updating)
- `GET /api/plans` `[AllowAnonymous]`, light rate-limit, **open-origin CORS** (landing is a different
  origin): returns public DTO (`slug, name, priceMonthly, currency, interval, featureBullets,
  displayState, sortOrder` + a curated public entitlement summary) for `DisplayState != Hidden`, ordered
  by SortOrder.
- **Landing** (`landing/index.html`, static): add a small inline script that fetches `/api/plans` on load
  and renders the pricing cards (with a static fallback if the fetch fails). `ComingSoon` → greyed card +
  "Coming soon" badge, no signup CTA; disabled-but-Visible → shown, CTA hidden. Auto-updates on any plan
  edit — no rebuild.

## Signup + upgrade
- **Signup (workspace/register-admin only, NOT stakeholders):** add optional `planSlug`. Free → existing
  flow. Paid → create the tenant + `Subscription(Plan, Status=None)` "pending activation" (no payment) →
  super-admin activates. Dashboard signup UI fetches `/api/plans` for the selector.
- **Upgrade/downgrade:** `PATCH /api/admin/tenants/{id}/plan {planId}` (super-admin) sets the tenant's
  Subscription.PlanId + `Status=Active` via the billing seam; entitlements apply immediately (grandfathered).
  Tenants admin UI gets a plan dropdown per tenant.

## Payment-READY seam (no gateway calls)
- `IBillingProvider { Task<BillingResult> ActivateAsync(sub, plan); Task CancelAsync(sub); }` +
  `NoopBillingProvider` (marks `Status=Active`/`Canceled` locally, no HTTP) — Scrutor-registered. All
  activation/upgrade routes go through it, so Stripe = a new adapter + a webhook controller later, no
  schema churn. Subscription already carries the external ids/status/period fields.
- Global settings (super-admin, DB): `defaultSignupPlan` (slug), `trialDays`, `currency`. Provider API
  keys env-only + masked (like the email key) when a provider is added.

## Dashboards (×3)
- New super-admin **Plans** page: table + create/edit dialog (name, price, currency, interval, state
  toggles, feature bullets, and a form for each entitlement value from the catalog), enable/disable, delete.
- **Tenants** page: per-tenant plan dropdown (upgrade/downgrade) + current usage vs limits hint.
- **Signup** page: plan selector (from `/api/plans`).
- Optional: usage badges ("3/5 projects"). i18n en+ar for all.

## Isolation
Plan = global catalog (super-admin CRUD; publicly readable for display). Subscription = tenant-scoped
(strict-own filter). Enforcement only ever reads the caller's own tenant's subscription. Public `/api/plans`
exposes only marketing fields (never tenant/subscription data).

## Migration & rollout
Migrations: `plans`, `subscriptions` (+ default plan seed + Legacy backfill for existing tenants). Then
API deploy → publish clients (Plans/Subscription hooks) → dashboards build/deploy → landing pull → full
E2E before trusting deploy.

## E2E (before deploy)
Super-admin creates/edits a plan → appears on landing (and ComingSoon/Hidden render right) · signup with a
plan links a Subscription · enforcement blocks at each limit (create 4th project on Free-3 → upgrade
message; extension disabled on Free) · grandfather: downgrade a tenant over-limit → existing kept, new
blocked · super-admin upgrade lifts the limit immediately · `GET /api/plans` hides Hidden · tenant
isolation intact (a tenant can't read/modify plans or another tenant's subscription).

## Phasing (ALL built now; sequencing only)
P1 = model + registry + seeding/Legacy + enforcement of the 7 + super-admin CRUD + public endpoint +
landing + signup selector + upgrade + payment-ready seam + dashboards. Display-only levers ship as
landing bullets (not enforced yet, by design). Real payment integration is explicitly OUT (seam only).

## Risks / open points (for cross-review)
1. Legacy-plan backfill vs. seeding Free onto existing tenants — I chose Legacy-unlimited to avoid
   lockouts; confirm.
2. `MaxSeats`/`MaxExtensionSites` counting: what exactly is a "seat" (all non-super tenant members incl.
   stakeholders?) and how to track distinct extension domains without heavy plumbing.
3. Comments/month = calendar month UTC (simplest) vs rolling 30d.
4. Typed-VO-JSON entitlements vs. typed columns — I chose JSON VO (no per-key migration, still
   compile-safe); confirm that's better than columns for CRUD/enforcement.
5. Plan delete when tenants are on it → require reassign (like roles) vs. soft-hide only.
