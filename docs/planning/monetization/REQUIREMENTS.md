# Monetization foundations — REQUIREMENTS (shared source of truth)

The agreed scope from brainstorming. Two independent implementation plans are written against THIS doc
(PLAN-CLAUDE.md, PLAN-GLM.md), then cross-reviewed and merged. **No payment gateway integration now**,
but the infrastructure must be **payment-ready** (adding Stripe/Paddle later = an adapter + webhook, no
schema churn).

## Product context
Pointer = multi-tenant element-feedback SaaS. .NET 8 Clean Architecture, EF Core + Postgres, Result<T>,
Scrutor DI, EF global query filters for tenant isolation, JWT auth. A **tenant** = a self-owning
Workspace-Admin user (`OwnerId == PublicId`). Stakeholders = non-admin tenant members. 3 dashboards
(Angular/React/Vue) consume a generated orval client. A bilingual static landing page. Today the
per-tenant caps (demo/comment/email) live as global `AppSetting`s.

## What we're building
Foundations of subscription **plans + entitlements**, super-admin managed, shown on the landing, chosen
at workspace signup, upgradable from the admin dashboard, with enforcement — and payment-ready hooks.

### 1. Plan entity (super-admin managed)
Fields: `Name`, `PriceMonthly`, `Currency`, `Interval` (monthly/yearly), `SortOrder`,
`IsActive` (can be subscribed to / enforced; off = no new signups), `DisplayState`
(`Visible` | `ComingSoon` | `Hidden` — controls landing rendering, independent of IsActive),
`Entitlements` (typed, FIXED keys — see below), optional `FeatureBullets` (display-only marketing list).
Super-admin can **add/update/delete/disable** plans (rows), edit values/price/state — but the **set of
entitlement keys is fixed in code** (they edit values, never invent keys the app can't enforce/display).

### 2. Entitlement keys (FIXED in code). Mark ENFORCED vs DISPLAY-ONLY for P1.
ENFORCED in P1 (≈7 — the ceiling for P1):
- `maxProjects` (int, -1 = unlimited)
- `maxSeats` (tenant members/stakeholders)
- `maxCommentsPerMonth`
- `extensionEnabled` (bool — is the browser extension usable on this plan)
- `maxExtensionSites` (distinct domains the extension can activate on)
- `maxPredefinedActionsPerProject`
- `maxTenantWidePredefinedActions`
DISPLAY-ONLY in P1 (shown on landing, not yet enforced — enforce later): `retentionDays`,
`maxEnvironments`, `maxActiveInvites`, `emailsPerMonth`, `extensionCommentsPerMonth`,
`maxPendingSuggestions`, `exportImportEnabled`, `promptSuggestionsEnabled`, `customStatusesEnabled`,
`prioritySupport`.
Convention: `-1` = unlimited; booleans for feature gates.

### 3. Tenant → Plan link + seeding
Tenant gets a `PlanId` (default = Free). The **current global AppSetting caps seed the Free plan's
entitlement values** on migration. Free plan is the baseline for existing tenants.

### 4. Enforcement layer (the real work — not just CRUD)
Every limited action checks the tenant's plan entitlement BEFORE succeeding, returns a clear
"you've reached your plan's limit — upgrade" result. Enforcement points (P1):
- create project → `maxProjects`
- add/invite a member → `maxSeats`
- post a comment → `maxCommentsPerMonth` (rolling/calendar month)
- widget/extension gating → `extensionEnabled`, `maxExtensionSites`
- create predefined action (per project / tenant-wide) → the two prompt caps
**Grandfather-on-downgrade (global rule):** count only ACTIVE rows; on downgrade never delete existing
over-limit data — just block ADDING new until under the new limit, with an upgrade message.

### 5. Landing page (auto-updating pricing)
Public `GET /api/plans` returns only public fields (name, price, currency, interval, feature bullets,
displayState, sortOrder) for `DisplayState != Hidden`. The landing fetches this client-side and renders
the pricing section → auto-updates on plan changes, no rebuild. Render `ComingSoon`/disabled states
(greyed, "Coming soon", no signup CTA).

### 6. Signup linkage (workspace signup only — NOT stakeholders)
The workspace signup (`register-admin`) gets a plan selector (default Free). Free → immediate (as today,
pending super-admin approval per existing flow). Paid plan chosen → tenant created but plan is
"pending activation" (no payment now) — super-admin activates.

### 7. Upgrade/downgrade from the admin dashboard
Super-admin changes a tenant's `PlanId` in the Tenants admin UI (entitlements apply immediately;
grandfathering per §4).

### 8. Payment-READY infrastructure (NO gateway calls now)
Design so Stripe/Paddle later is an adapter, not a rewrite:
- A `Subscription`/billing shape (on the tenant or a linked entity): nullable `BillingProvider`,
  `ExternalCustomerId`, `ExternalSubscriptionId`, `SubscriptionStatus` enum
  (`None|Trialing|Active|PastDue|Canceled`), `CurrentPeriodEnd`, `TrialEndsAt`.
- An `IBillingProvider` seam with a `NoopBillingProvider` (default) — activation/upgrade goes through it
  so a real provider drops in later. No external HTTP now.
- Global settings for later: `defaultSignupPlan`, `trialDays`, `currency`; billing keys env-only+masked
  (like the email key) when a provider is added.

## Constraints / non-negotiables
- No payment gateway integration or external billing HTTP in this work.
- Entitlement keys fixed in code; super-admin edits values/price/state/rows only.
- Grandfather on downgrade (never delete data).
- Tenant isolation preserved (Plan is global/super-admin-owned; Subscription is tenant-scoped).
- Enforcement + "upgrade" messaging on every enforced lever; landing pricing dynamic.
- Follow codebase conventions (Result<T>, Scrutor, EF query filters, MessageKeys, orval tags,
  validators auto-run, migrations additive). Full E2E before deploy. Implement ALL agreed phases
  autonomously (no user review); phases are for sequencing, all get built.
