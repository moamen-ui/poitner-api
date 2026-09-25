# DB-20 (BILL-1) — Billing v1: manual / cash payments, complimentary plans, reference (discount) codes

Owner request (2026-09-25, relayed by the orchestrator): no payment gateway. (1) A Workspace Admin chooses a paid plan (optionally with a reference
code) → the workspace is **pending activation** with a quoted price; a super admin **marks it as paid** (amount, currency, paid-at, method, note,
recorded-by) → it is active for one billing period, and later payments extend it. (2) A super admin can put any workspace on any plan **for free**
(complimentary / VIP), including through a new-workspace invite. (3) Super-admin-managed **reference codes** (percent or fixed amount) discount the quote;
every use is recorded with a price snapshot; the admin list shows per-code usage and the workspaces that used it. The `IBillingProvider` seam stays.

Rules: **R1** (every column nullable or defaulted; four new tables; plain indexes/FKs/checks), **R4** (plain mode — `subscriptions` and `invites` are tiny;
the new tables start empty), R5, R6, **R7** (the trigger migration is `[ContractMigration]` → **contract deploy**), **R8** (two new owner-carrying tables,
strict-own filter, cross-tenant test; **new R8.9**: they survive workspace deletion), R9 (partial unique indexes), R10 (enum ints, audit actions, JSON-free
column names, route names frozen once shipped), R11, R13, **R14** (every `*_by` is a `public_id` content reference, no FK, no PII), **R16**
(`TryRequireOwner` / `ICurrentUser.Id`), **R17** (billing_payments is **append-only at three layers**, the second such table; every mutation audited;
workspace views redact the operator), R18 (billing data is metadata, not content — operator reads need no impersonation), **R19** (new mutating
actions are freeze-gated by default), **new R20 (money)**.
**Class: Additive + one R4-constraint trigger migration** (nothing dropped, renamed or narrowed; no data backfill). **Status: written 2026-09-25,
not implemented.** Owner decisions F-B1…F-B14 (§3.12) proceed on the recommended defaults unless the owner says otherwise.

**Dependencies.** DB-03 (`workspaces` + FKs), DB-11a (memberships), DB-12 (audit + append-only precedent), DB-18 (lifecycle guard, freeze filter).
Planned against `main` @ `4da0091`, **83 migrations**, newest `20260924051230_ClearUsersRoleIdForMembers`. Independent of DB-19 (WS-NEW); if DB-19
ships first, its empty migration simply precedes these three.

## 1. Goal

Let Pointer take money by hand (cash, bank transfer) without a gateway, while every figure a customer was shown and every payment an operator typed is
kept as an immutable record: workspaces can request a paid plan and see what they owe; the operator records payments that activate or renew the plan;
VIP and sales-led workspaces run on any plan at $0 without ever expiring by accident; partner/marketer reference codes lower the quote and their use is
countable per code. User-visible reason: the product can charge its first customers now, and switching to Stripe/Paddle later is an
`IBillingProvider` implementation, not a schema rewrite.

## 2. Prerequisites (verified facts, 2026-09-25 @ `4da0091`)

**Plans and subscriptions**
- `Domain/Entity/Plan.cs:13-35` (`BaseEntity`; `PriceMonthly decimal`, `Currency string = "USD"`, `Interval BillingInterval`, `IsActive`, `DisplayState`).
  `Infrastructure/Mappings/PlanMapping.cs:34-37` — `price_monthly numeric(12,2)`, `currency varchar(8) NOT NULL`. `PlanWriteDtoValidator.cs:22-23` —
  currency only `NotEmpty().MaximumLength(8)`, price `>= 0`.
- **Price semantics (verified):** `PriceMonthly` is the price **per billing interval**, despite its name — `landing/index.html:966-975` prints
  `priceMonthly` followed by "/yr" when `interval === 1` (Yearly). `BillingInterval { Monthly = 0, Yearly = 1 }` (`Domain/Enums/BillingInterval.cs:3`).
  `PlanDisplayState { Visible = 0, ComingSoon = 1, Hidden = 2 }`.
- Free plan = slug `free` (`EntitlementService.cs:131-145`, `AdminSeeder.cs:268-299`); Legacy = slug `legacy`, price 0, inactive, hidden (`AdminSeeder.cs:302-321`);
  pre-monetization workspaces got `Subscription(Legacy, Active)` once (`:323-374`).
- `Domain/Entity/Subscription.cs:11-27`: `OwnerId Guid` (NOT NULL), `PlanId`, `Status`, `BillingProvider`, `ExternalCustomerId`, `ExternalSubscriptionId`,
  `CurrentPeriodEnd`, `TrialEndsAt`. `SubscriptionMapping.cs:23-48`: FK `fk_subscriptions_workspaces_owner_id` (Restrict), unique
  `ux_subscriptions_owner_live (owner_id) WHERE deleted_at IS NULL`, FK to plans (Restrict).
- `SubscriptionStatus { None = 0, PendingActivation = 1, Trialing = 2, Active = 3, PastDue = 4, Canceled = 5 }` (`Domain/Enums/SubscriptionStatus.cs:9-17`);
  its summary (`:3-8`): **"The effective plan is still resolved from the row's PlanId regardless of status"** — `EntitlementService.ResolveAsync :97-129`
  reads only `PlanId` (missing row ⇒ Free). **This doc keeps that invariant** (`PlanId` = the granted plan) and puts a request in separate columns.
- Writers of `subscriptions` today:
  - `AuthService.RegisterAdminAsync :1543-1574` — paid, active, non-hidden plan ⇒ `Subscription { PlanId = <paid>, Status = PendingActivation }` (so the paid
    entitlements are granted while pending).
  - `InviteService.AcceptCreateNewWorkspaceAsync :1005-1036` — invited paid plan ⇒ `Status = Active` with no payment; the comment `:992-1003` says
    "When real billing lands, invited paid workspaces need a comped marker." Plan filter there: `DeletedAt == null && IsActive && DisplayState != Hidden`.
  - `TenantService.ChangePlanAsync :459-526` (`PATCH /api/admin/tenants/{workspaceId}/plan`, `TenantsController.cs:202-214`, body
    `ChangeTenantPlanRequest { int PlanId }` `:256-259`) — upsert, `None/Canceled → Active`, calls `_billing.ChangePlanAsync`, audits `tenant.plan_changed`
    with `plan_id` before/after. Requires `plan.IsActive` (`:476`).
  - `AdminSeeder` Legacy backfill (above). `TenantService.HardDeleteAsync` deletes the row (`:665`, `HardDeleteOrder :807`).
  - Nobody sets `CurrentPeriodEnd`, `TrialEndsAt`, `External*` (grep). **Production values of `current_period_end` are expected all NULL — verified only by
    the §9 census.**
- `Application/Abstractions/IBillingProvider.cs:11-21` (`ActivateAsync`, `ChangePlanAsync`, `CancelAsync`); `Infrastructure/Billing/NoopBillingProvider.cs:12-33`
  (`ActivateAsync` flips `PendingActivation|None → Active` only; `CancelAsync` sets `Canceled`). `ActivateAsync`/`CancelAsync` have **no caller** today.
- `TenantService.ListAsync :117-154` exposes `PlanName`, `SubscriptionStatus` per workspace (`TenantResponse`).
- `PlanService.cs:185-189` counts subscriptions by `PlanId` for the "plan in use" delete guard.

**Invites**
- `Domain/Entity/Invite.cs:16-64` (`OwnerId` null ⇔ new-workspace invite, `PlanId`, `DisplayName`); `InviteMapping.cs:44-46`.
  `CreateTenantInviteRequest` (`Application/DTOs/Tenant/CreateTenantInviteRequest.cs`) → `TenantInviteService.CreateAsync :28-45` → `InviteService.CreateAsync`
  (`PlanId` stored only for `CreateNewWorkspace`, `:246`). No plan validation at invite time; validation happens at accept.

**Tenancy, append-only, audit precedents**
- `Domain/Entity/AuditEvent.cs:11-…` (non-`BaseEntity`, `long Id`, `init` properties); `AuditEventMapping.cs:11-26` (`UseIdentityByDefaultColumn`, FK
  `SetNull`); filter `AppDbContext.cs:322-327`; SaveChanges guard `AppDbContext.cs:390-401`, called at `:483`, `:492`, `:499`; trigger migration
  `20260923003835_AddAuditEventsAppendOnlyTrigger.cs` (marker line + `[ContractMigration("DB-12")]`, function + two triggers, `Down()` drops them).
- `Tests/WorkspaceTests.cs:344-367` `OperatorTableExclusions` + the pin test; `:370-388` `HardDeleteOrder_CoversEveryOwnerCarryingEntity` scans every class
  in the Domain assembly with an `OwnerId` property.
- `IUnitOfWork` (`Application/Abstractions/IUnitOfWork.cs:10-15`) exposes non-`BaseEntity` sets as `DbSet<T>` properties (`AuditEvents`, `UsageDaily`, …);
  implementation `Infrastructure/Repository/UnitOfWork.cs`.
- Row locks: `WorkspaceLifecycleService.cs:206-209` `SELECT id FROM workspaces WHERE id = {0} FOR UPDATE` inside `ExecuteInTransactionAsync`.
- `AuditActions.cs:96-116` workspace/tenant actions; `AuditTargets.cs:9-28`; `AuditFields.cs:13-44` whitelist (has `plan_id`, `status`, `kind`, `source`,
  `reason`, `count`; **not** `code`, which the doc-comment `:48-50` forbids).
- `WorkspaceLifecycleGuard.CanManageAsync` (`Application/Common/WorkspaceLifecycleGuard.cs:19-56`) — the "Workspace Admin of this workspace, not
  API key / quick access / super admin / impersonation / live demo" rule reused for the workspace-side billing endpoints.
- Hosted jobs: `API/Hosted/*.cs`, registered `API/Program.cs:118-132`. Settings: `ISettingsService.GetIntAsync(key, fallback)` (`ISettingsService.cs:59`);
  `ISettingsService.Currency` (`:49`, default "USD", unread).
- Postgres-gated test precedent: `Tests/UsageRollupPostgresTests.cs:10-18` (`POINTER_TEST_PG=1`).
- `orval.config.ts:6` tag filter (no `Billing`, no `DiscountCodes` yet). `Tests/MonetizationSignupTests.cs:213` `Signup_WithPaidPlan_CreatesPendingActivationSubscription`
  asserts today's register-admin shape (changes in task 17).

**Tooling:** `just migrate name="<PascalCase>"`, `just test`, `just fmt`; contract deploy
`POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db20 bash scripts/deploy-api.sh` (R7).

## 3. Design

### 3.1 Subscription semantics after BILL-1 (one row per workspace, unchanged cardinality)

| Field | Meaning after BILL-1 |
|---|---|
| `plan_id` | The **granted** plan — the only input to entitlements (unchanged). |
| `status` | `None` = on Free / nothing billed. `PendingActivation` = a paid plan is **requested**, nothing paid is granted yet (`plan_id` = Free), **or** the pre-BILL-1 signup shape (`plan_id` = paid, no request) which stays valid. `Active` = granted plan is paid for, complimentary, or operator-assigned. `PastDue` = paid period ended, inside the grace window (entitlements kept). `Trialing`, `Canceled` = not written by v1 code; existing rows untouched. |
| `current_period_end` | Non-null **only** for a manually paid period. `NULL` = never expires (Legacy, complimentary, operator-assigned, every pre-BILL-1 row). The period job never touches a row whose value is NULL. |
| `requested_plan_id`, `requested_at`, `requested_by`, `quoted_price`, `quoted_currency` | The pending request and the price the workspace was shown (after discount). All five null together (check). |
| `is_complimentary`, `comped_at`, `comped_by`, `comp_reason`, `comp_ends_at` | Complimentary marker. Comp ⇒ `status = Active`, never billed, never period-expired; `comp_ends_at` (optional) hands the row to the normal period machinery (§3.6 h1). |
| `renewal_reminder_sent_at` | Idempotency stamp for the T-N days renewal e-mail; cleared whenever a period is extended. |
| `billing_provider` | Set to `"manual"` by the first recorded payment (existing column). |

Status transitions written by v1 code (every other transition is refused by the service):

| From | Event | To | `plan_id` |
|---|---|---|---|
| (no row) / `None` | admin requests paid plan P | `PendingActivation` | Free (row created with Free's id when missing) |
| `Active` / `PastDue` (paid, not comp) | admin requests another paid plan P | unchanged | unchanged |
| `PendingActivation` | admin cancels / operator rejects request | `None` if `plan_id` = Free, else unchanged | unchanged |
| any non-comp with a target paid plan | operator records payment | `Active` | requested plan, else current plan |
| `Active` (paid, not comp), `current_period_end ≤ now` | job | `PastDue` | unchanged |
| `PastDue`, `current_period_end + grace ≤ now` | job | `None` | Free; `current_period_end` → NULL |
| comp with `comp_ends_at ≤ now` (job) or operator "end comp" | — | `PastDue`, comp columns cleared, `current_period_end` = the comp end | unchanged |
| any | operator `PATCH …/plan` with a paid plan | `Active`, comp stamped, request cleared, `current_period_end` → NULL | that plan |
| any | operator `PATCH …/plan` with Free | existing logic (`None/Canceled → Active`), comp + request cleared, `current_period_end` → NULL | Free |
| any | operator voids the latest payment | restored from the payment's `previous_*` snapshot (`PendingActivation` on Free → `None`) | restored |

### 3.2 Money rules (new DB-RULES R20; cited everywhere below)

- C# `decimal`; Postgres `numeric(12,2)` — same as `plans.price_monthly` (`PlanMapping.cs:34-36`). Never `float`/`double`/`real`/`money`.
- Currency: ISO 4217 alpha-3, upper case, `varchar(3)` + `CHECK (col ~ '^[A-Z]{3}$')` on every **new** column. `plans.currency` stays `varchar(8)` (R1: no
  narrowing); the plan validator is tightened in code (task 18) and the quote path upper-cases and validates it, failing closed.
- Every stored amount has its currency in the same row. Rounding: `Math.Round(x, 2, MidpointRounding.AwayFromZero)`.
- **Period price** = `plan.PriceMonthly` (the per-interval price, §2). Period length: Monthly → `start.AddMonths(1)`, Yearly → `start.AddYears(1)`, UTC.
- Discount: Percent → `discount = Round(price * value / 100)`; Fixed → `discount = Min(value, price)`, applicable **only** when `code.currency == plan currency`.
  `final = price - discount` (≥ 0). A price shown to a customer is **snapshotted** in the row that relied on it (`subscriptions.quoted_price`,
  `discount_redemptions.*_snapshot/original/discount/final`, `billing_payments.quoted_amount`) and never recomputed from an editable catalog row.
- Every `DateTime` written is `DateTimeKind.Utc` (Npgsql refuses `Unspecified` for `timestamptz` — the DB-15 lesson, `UsageRollupPostgresTests.cs:4-8`);
  request DTO timestamps are converted with `DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)`.

### 3.3 Schema — Migration 1 `AddBillingV1SubscriptionAndInviteColumns` (R1; AddColumn / AddForeignKey / CreateIndex / AddCheckConstraint only)

**`subscriptions`** (tiny table, plain mode R4) — add, in this order:

| Column | Type | Null / default | Notes |
|---|---|---|---|
| `requested_plan_id` | `integer` | NULL | FK `fk_subscriptions_plans_requested_plan_id` → `plans(id)` **Restrict**; EF's FK index `IX_subscriptions_requested_plan_id` |
| `requested_at` | `timestamptz` | NULL | |
| `requested_by` | `uuid` | NULL | requester `public_id` — R14 content reference, no FK |
| `quoted_price` | `numeric(12,2)` | NULL | after discount |
| `quoted_currency` | `varchar(3)` | NULL | |
| `is_complimentary` | `boolean` | NOT NULL DEFAULT `false` | |
| `comped_at` | `timestamptz` | NULL | |
| `comped_by` | `uuid` | NULL | operator `public_id` (R14), no FK |
| `comp_reason` | `varchar(200)` | NULL | operator-typed; no personal data (R8.9) |
| `comp_ends_at` | `timestamptz` | NULL | |
| `renewal_reminder_sent_at` | `timestamptz` | NULL | |

Check constraints (all hold for every existing row, whose new columns are NULL/false):
- `ck_subscriptions_request_consistent`: `(requested_plan_id IS NULL) = (requested_at IS NULL) AND (requested_plan_id IS NULL) = (requested_by IS NULL) AND (requested_plan_id IS NULL) = (quoted_price IS NULL) AND (requested_plan_id IS NULL) = (quoted_currency IS NULL)`
- `ck_subscriptions_quote_valid`: `quoted_price IS NULL OR (quoted_price >= 0 AND quoted_currency ~ '^[A-Z]{3}$')`
- `ck_subscriptions_comp_consistent`: `(is_complimentary = (comped_at IS NOT NULL)) AND ((comped_at IS NULL) = (comped_by IS NULL)) AND (is_complimentary OR (comp_reason IS NULL AND comp_ends_at IS NULL))`
- `ck_subscriptions_comp_no_request`: `NOT (is_complimentary AND requested_plan_id IS NOT NULL)`

Partial indexes (the job's predicates; DB-17/18 precedent):
- `ix_subscriptions_current_period_end (current_period_end) WHERE current_period_end IS NOT NULL`
- `ix_subscriptions_comp_ends_at (comp_ends_at) WHERE comp_ends_at IS NOT NULL`

**`invites`** — add `is_complimentary boolean NOT NULL DEFAULT false`, `comp_reason varchar(200) NULL`, `comp_ends_at timestamptz NULL`, and
`ck_invites_comp_consistent`: `(is_complimentary OR (comp_reason IS NULL AND comp_ends_at IS NULL)) AND (NOT is_complimentary OR (owner_id IS NULL AND plan_id IS NOT NULL))`.
Every existing invite: `false`/NULL/NULL → passes. **Behaviour change for in-flight invites**: an unaccepted new-workspace invite with a paid plan, created
before this deploy, will accept as *pending payment* instead of today's free Active — §9 step 1c counts them; the operator re-issues each as
complimentary after the deploy (no data migration, F-B10).

Expected migration operations, in order: 11× `AddColumn` on `subscriptions`, 3× `AddColumn` on `invites`, `CreateIndex IX_subscriptions_requested_plan_id`,
2× `CreateIndex` (partial), `AddForeignKey fk_subscriptions_plans_requested_plan_id`, 5× `AddCheckConstraint`. Nothing else.

### 3.4 Schema — Migration 2 `AddBillingLedgerAndDiscountCodes` (R1; four `CreateTable`)

**`discount_codes`** — `DiscountCode : BaseEntity` (global catalog like `plans`: **no `owner_id`, no query filter**, super-admin endpoints only).

| Column | Type | Null / default | Notes |
|---|---|---|---|
| `id` + the six `BaseEntity` columns | as `plans` | | |
| `code` | `varchar(32)` | NOT NULL | stored upper-case (service normalises `Trim().ToUpperInvariant()`); **immutable** after create |
| `label` | `varchar(120)` | NULL | partner / marketer label (display only) |
| `note` | `varchar(500)` | NULL | operator note |
| `kind` | `integer` | NOT NULL | `DiscountKind { Percent = 1, FixedAmount = 2 }` |
| `value` | `numeric(12,2)` | NOT NULL | percent (0 < v ≤ 100) or amount |
| `currency` | `varchar(3)` | NULL | required iff `FixedAmount` |
| `duration` | `integer` | NOT NULL DEFAULT `1` | `DiscountDuration { Once = 1, Forever = 2 }` (F-B6) |
| `valid_from`, `valid_until` | `timestamptz` | NULL | half-open window `[from, until)` |
| `max_redemptions` | `integer` | NULL | NULL = unlimited |
| `is_active` | `boolean` | NOT NULL DEFAULT `true` | deactivate instead of delete (F-B8) |

Indexes/checks: `ux_discount_codes_code_live UNIQUE (code) WHERE deleted_at IS NULL` (R9; case-insensitivity comes from the normaliser **and**
`ck_discount_codes_code_format`: `code ~ '^[A-Z0-9][A-Z0-9_-]{2,31}$'` — so no `lower()` expression index is needed); `ck_discount_codes_value`:
`(kind = 1 AND value > 0 AND value <= 100 AND currency IS NULL) OR (kind = 2 AND value > 0 AND currency IS NOT NULL AND currency ~ '^[A-Z]{3}$')`;
`ck_discount_codes_window`: `valid_from IS NULL OR valid_until IS NULL OR valid_until > valid_from`; `ck_discount_codes_max_redemptions`:
`max_redemptions IS NULL OR max_redemptions > 0`; `ck_discount_codes_duration`: `duration IN (1, 2)`.

**`discount_code_plans`** — `DiscountCodePlan` (not `BaseEntity`; join row; navigation `DiscountCode.PlanScopes`). Columns `discount_code_id integer NOT NULL`
FK → `discount_codes` **Cascade** (`fk_discount_code_plans_discount_codes_discount_code_id`), `plan_id integer NOT NULL` FK → `plans` **Restrict**
(`fk_discount_code_plans_plans_plan_id`); PK `pk_discount_code_plans (discount_code_id, plan_id)`; EF index on `plan_id`. **No rows = valid for every plan.**

**`discount_redemptions`** — `DiscountRedemption` (not `BaseEntity`, `long Id`; owner-carrying; R8.9).

| Column | Type | Null / default | Notes |
|---|---|---|---|
| `id` | `bigint` identity | NOT NULL | `UseIdentityByDefaultColumn()` |
| `owner_id` | `uuid` | NULL | FK `fk_discount_redemptions_workspaces_owner_id` → `workspaces` **SetNull** |
| `discount_code_id` | `integer` | NOT NULL | FK → `discount_codes` **Restrict** |
| `plan_id` | `integer` | NOT NULL | FK → `plans` **Restrict** |
| `status` | `integer` | NOT NULL | `DiscountRedemptionStatus { Pending = 1, Applied = 2, Released = 3 }` |
| `code_snapshot` | `varchar(32)` | NOT NULL | |
| `kind_snapshot`, `duration_snapshot` | `integer` | NOT NULL | |
| `value_snapshot` | `numeric(12,2)` | NOT NULL | |
| `currency_snapshot` | `varchar(3)` | NULL | |
| `original_price`, `discount_amount`, `final_price` | `numeric(12,2)` | NOT NULL | |
| `price_currency` | `varchar(3)` | NOT NULL | |
| `created_at` | `timestamptz` | NOT NULL | "at" |
| `created_by` | `uuid` | NOT NULL | requesting admin `public_id` (R14) |
| `applied_at`, `released_at` | `timestamptz` | NULL | |
| `release_reason` | `integer` | NULL | `RedemptionReleaseReason { Replaced = 1, CancelledByWorkspace = 2, RejectedByOperator = 3, PaymentVoided = 4, OperatorPlanOverride = 5, WorkspaceDeleted = 6 }` |

Indexes: `ux_discount_redemptions_code_owner_open UNIQUE (discount_code_id, owner_id) WHERE status IN (1, 2)` (one live use of a code per workspace;
detached NULL owners are distinct); `ux_discount_redemptions_owner_pending UNIQUE (owner_id) WHERE status = 1` (one open quote per workspace);
`ix_discount_redemptions_code_status (discount_code_id, status)` (counts); EF FK index on `plan_id`.
Checks: `ck_discount_redemptions_amounts`: `original_price >= 0 AND discount_amount >= 0 AND discount_amount <= original_price AND final_price = original_price - discount_amount AND price_currency ~ '^[A-Z]{3}$'`;
`ck_discount_redemptions_status_shape`: `(status = 1 AND applied_at IS NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 2 AND applied_at IS NOT NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 3 AND released_at IS NOT NULL AND release_reason IS NOT NULL)`.
Snapshot properties are `init`-only in C#; only `Status`, `AppliedAt`, `ReleasedAt`, `ReleaseReason` have setters. Not trigger-protected (statuses move).

**`billing_payments`** — `BillingPayment` (not `BaseEntity`, `long Id`, **all properties `init`**; owner-carrying; append-only; R8.9, R17).

| Column | Type | Null / default | Notes |
|---|---|---|---|
| `id` | `bigint` identity | NOT NULL | |
| `owner_id` | `uuid` | NULL | FK `fk_billing_payments_workspaces_owner_id` → `workspaces` **SetNull** |
| `plan_id` | `integer` | NOT NULL | FK `fk_billing_payments_plans_plan_id` → `plans` Restrict (plan paid for) |
| `kind` | `integer` | NOT NULL | `BillingPaymentKind { Payment = 1, Void = 2 }` |
| `amount` | `numeric(12,2)` | NOT NULL | received (Void: the voided amount, positive) |
| `currency` | `varchar(3)` | NOT NULL | |
| `quoted_amount` | `numeric(12,2)` | NULL | what the system quoted when recorded |
| `method` | `integer` | NULL | `PaymentMethod { Cash = 1, BankTransfer = 2, Other = 3 }`; required for Payment |
| `reference` | `varchar(128)` | NULL | receipt / transfer reference |
| `note` | `varchar(500)` | NULL | operator note; Void: the void reason (required by the service) |
| `paid_at` | `timestamptz` | NULL | operator-entered; required for Payment |
| `period_start`, `period_end` | `timestamptz` | NULL | granted period; required for Payment |
| `previous_plan_id` | `integer` | NULL | FK `fk_billing_payments_plans_previous_plan_id` → `plans` Restrict |
| `previous_status` | `integer` | NULL | `SubscriptionStatus` before this row |
| `previous_period_end` | `timestamptz` | NULL | |
| `discount_redemption_id` | `bigint` | NULL | FK `fk_billing_payments_discount_redemptions_discount_redemption_id` → `discount_redemptions` Restrict |
| `discount_first_applied` | `boolean` | NOT NULL DEFAULT `false` | this payment moved the redemption Pending → Applied |
| `voids_payment_id` | `bigint` | NULL | self FK `fk_billing_payments_billing_payments_voids_payment_id` Restrict |
| `recorded_at` | `timestamptz` | NOT NULL | |
| `recorded_by` | `uuid` | NOT NULL | operator `public_id` (R14) |

Indexes: `ix_billing_payments_owner_recorded (owner_id, recorded_at DESC)`; `ux_billing_payments_voids_payment_id UNIQUE (voids_payment_id) WHERE voids_payment_id IS NOT NULL`;
EF FK indexes on `plan_id`, `previous_plan_id`, `discount_redemption_id`.
Checks: `ck_billing_payments_kind_shape`:
`(kind = 1 AND method IN (1, 2, 3) AND paid_at IS NOT NULL AND period_start IS NOT NULL AND period_end IS NOT NULL AND period_end > period_start AND previous_plan_id IS NOT NULL AND previous_status IS NOT NULL AND voids_payment_id IS NULL) OR (kind = 2 AND voids_payment_id IS NOT NULL AND method IS NULL AND paid_at IS NULL AND period_start IS NULL AND period_end IS NULL AND discount_redemption_id IS NULL AND NOT discount_first_applied)`;
`ck_billing_payments_amounts`: `amount >= 0 AND (quoted_amount IS NULL OR quoted_amount >= 0)`; `ck_billing_payments_currency`: `currency ~ '^[A-Z]{3}$'`;
`ck_billing_payments_first_applied`: `NOT discount_first_applied OR discount_redemption_id IS NOT NULL`.

Expected Migration 2 operations: 4× `CreateTable` (order `discount_codes`, `discount_code_plans`, `discount_redemptions`, `billing_payments` — EF orders by FK
dependency; any order EF picks that satisfies FKs is fine), their `CreateIndex`es and check constraints (EF emits checks inside `CreateTable`). Nothing
touching an existing table.

### 3.5 Schema — Migration 3 `AddBillingPaymentsAppendOnlyTrigger` (R17 layer 3; `[ContractMigration("DB-20")]`; nothing else in it)

Copy `20260923003835_AddAuditEventsAppendOnlyTrigger.cs` verbatim, replacing `audit_events` → `billing_payments`, function `audit_events_append_only` →
`billing_payments_append_only`, triggers `trg_billing_payments_append_only` (BEFORE UPDATE OR DELETE, FOR EACH ROW) and `trg_billing_payments_no_truncate`
(BEFORE TRUNCATE, FOR EACH STATEMENT), the exception text `'billing_payments is append-only (DB-20): % is not allowed'`, and `fk_audit_events_workspaces_owner_id`
→ `fk_billing_payments_workspaces_owner_id` in the comment. The single permitted UPDATE shape stays: `owner_id` non-null → NULL, every other column
byte-identical (the FK's `ON DELETE SET NULL`). Marker on the line above `Up()`:
`// DB-RULES: R4 constraint approved <yyyy-mm-dd> by Moamen (owner; instruction "Billing v1 — immutable payments ledger", relayed by the orchestrator; docs/db/execution/DB-20-billing-v1-manual-payments-comp-codes.md)`
(fill the date the orchestrator relays F-B3; if the owner rejects F-B3, drop Migration 3 and task 9, and the deploy becomes ordinary).

### 3.6 Behaviour (services)

New `IBillingService` / `BillingService` (Application; Scrutor-registered by name) owns every write below; `IBillingPeriodService` /
`BillingPeriodService` owns the job; `IDiscountCodeService` / `DiscountCodeService` owns code CRUD + listings. All subscription loads use
`IgnoreQueryFilters()` + explicit `OwnerId == W && DeletedAt == null` (R19 last bullet). Free plan id: the same lookup as `EntitlementService.FreePlanIdAsync`
(extract it to `IEntitlementService.GetFreePlanIdAsync()`).

**Lock order (deadlock-free, every write path):** `SELECT id FROM workspaces WHERE id = {0} FOR UPDATE` first, then (only when a code is involved)
`SELECT id FROM discount_codes WHERE id = {0} FOR UPDATE`, all inside one `ExecuteInTransactionAsync`; the audit row is written inside the block after the
last `SaveChangesAsync` (R17). The unique indexes are the backstop; a `DbUpdateException` on them maps to `Conflict(MessageKeys.Billing.StateChanged)`.

**Quote(plan, code?) — pure, no writes** (used by preview, request, and the summary):
1. Plan must be `DeletedAt == null && IsActive && DisplayState != Hidden && PriceMonthly > 0`; `currency = plan.Currency.Trim().ToUpperInvariant()` must match
   `^[A-Z]{3}$`, else `Failure(Billing.PlanMisconfigured)`.
2. No code → `{ price, discount = 0, final = price }`.
3. Code: normalise; load live row (`DeletedAt == null`). Invalid ⇒ one message `Billing.CodeInvalid` when: missing, `!IsActive`, `now < ValidFrom`,
   `now >= ValidUntil`, or `MaxRedemptions != null && count(status IN (Pending, Applied)) >= MaxRedemptions`. `Billing.CodeNotForPlan` when scope rows exist
   and none is this plan, or Fixed with a different currency. `Billing.CodeAlreadyUsed` when this workspace has a Pending/Applied redemption of it
   (a Pending one being **replaced** by this same request does not count).
4. Discount per §3.2.

**(a) Request a paid plan — `RequestPlanAsync(planId, code?)`** (Workspace Admin of the current workspace, `WorkspaceLifecycleGuard.CanManageAsync`):
lock workspace → load subscription (tracked) → refuse `Conflict(Billing.Complimentary)` if comp; refuse `Conflict(Billing.AlreadyOnPlan)` if `sub.PlanId == planId`
and status ∈ {Active, PastDue, PendingActivation} (renewals need no request) → release the workspace's Pending redemption, if any (`Replaced`), and
**`SaveChangesAsync()` right away** (EF does not know the partial unique indexes' filters, so the release must hit the database before the new insert) → Quote (lock the
code row before its count) → insert a Pending redemption with every snapshot when a code applied → create the row if missing (`PlanId` = Free id, `Status` =
PendingActivation) or set `None → PendingActivation` (other statuses unchanged) → set the five `requested_*`/`quoted_*` columns → save → audit
`billing.plan_requested` `{ plan_id, amount = final, currency, discount_code_id? }`.

**(b) Cancel / reject the request** — workspace: `CancelRequestAsync()`; operator: `RejectRequestAsync(W)`. Requires `RequestedPlanId != null`, else `NotFound`.
Release the Pending redemption (`CancelledByWorkspace` / `RejectedByOperator`), null the five columns, `PendingActivation → None` iff `PlanId` = Free.
Audit `billing.request_cancelled` / `billing.request_rejected` `{ plan_id }`.

**(c) Record a payment — `RecordPaymentAsync(W, amount, currency?, paidAt, method, reference?, note?)`** (super admin):
1. Lock W; subscription must exist (`NotFound(Billing.NothingToPay)`); refuse comp (`Conflict(Billing.Complimentary)`).
2. `target = sub.RequestedPlanId ?? sub.PlanId`; load it with `DeletedAt == null` only (it was already chosen); `PriceMonthly <= 0` ⇒ `Conflict(Billing.NothingToPay)`.
3. `quoted` = `sub.QuotedPrice` when a request exists, else the renewal quote: period price, minus a `Forever` Applied redemption whose `plan_id == target`
   (recomputed from its snapshot kind/value on today's price; Fixed only if currencies match).
4. Validate: `0 <= amount <= 9_999_999_999.99` with ≤ 2 decimals; `currency` defaults to the quote currency, must match `^[A-Z]{3}$`; `method` defined;
   `now - 366 d <= paidAt <= now + 5 min`; `reference ≤ 128`, `note ≤ 500`. `amount != quoted` is allowed and recorded (F-B5: no partial-payment model).
5. Period: `isRenewal = sub.RequestedPlanId == null && sub.Status ∈ {Active, PastDue} && sub.CurrentPeriodEnd != null`.
   `start = isRenewal ? sub.CurrentPeriodEnd.Value : now` (contiguous renewals, early renewals stack; a first payment or a plan change starts **now**, not at
   `paidAt` — F-B4); `end = start + interval(target)`.
6. Insert `BillingPayment { Kind = Payment, PlanId = target, Amount, Currency, QuotedAmount = quoted, Method, Reference, Note, PaidAt, PeriodStart = start,
   PeriodEnd = end, PreviousPlanId = sub.PlanId, PreviousStatus = sub.Status, PreviousPeriodEnd = sub.CurrentPeriodEnd, DiscountRedemptionId, DiscountFirstApplied,
   RecordedAt = now, RecordedBy = operator public_id, OwnerId = W }` where the redemption is the Pending one of this request (→ `Applied`, `AppliedAt = now`,
   `DiscountFirstApplied = true`) or the Forever one used in step 3 (`DiscountFirstApplied = false`).
7. `sub.PlanId = target; Status = Active; CurrentPeriodEnd = end; RenewalReminderSentAt = null; BillingProvider = "manual";` null the request columns.
   If the plan changed `await _billing.ChangePlanAsync(sub, target)`; then `await _billing.ActivateAsync(sub)` (Noop: no-op on Active).
8. Save; audit `billing.payment_recorded` `{ payment_id, plan_id, amount, currency, method, period_end }` (+ `discount_code_id`).

**(d) Void — `VoidPaymentAsync(W, paymentId, reason)`** (super admin; `reason` 1–500 chars). Lock W. The payment must be `OwnerId == W`, `Kind = Payment`, not
yet voided, and the **latest** non-voided Payment of W (`ORDER BY recorded_at DESC, id DESC`) — else `Conflict(Billing.VoidOnlyLatest)`. Insert
`BillingPayment { Kind = Void, VoidsPaymentId, PlanId = p.PlanId, Amount = p.Amount, Currency = p.Currency, Note = reason, PreviousPlanId = sub.PlanId,
PreviousStatus = sub.Status, PreviousPeriodEnd = sub.CurrentPeriodEnd, … }`; restore `sub.PlanId/Status/CurrentPeriodEnd` from `p.Previous*`
(`PendingActivation` on Free → `None`); if `p.DiscountFirstApplied` release that redemption (`PaymentVoided`); the request columns are **not** restored (the
admin or operator re-requests; F-B7). Audit `billing.payment_voided` `{ payment_id }` (the reason is free text — not audited).

**(e) Complimentary**
- `PATCH /api/admin/tenants/{W}/plan` (`TenantService.ChangePlanAsync`) — body gains `CompReason?` (≤ 200) and `CompEndsAt?` (must be > now). Plan lookup relaxes from
  `IsActive` to `DeletedAt == null` (any plan, incl. hidden/inactive — F-B11). Paid plan ⇒ `IsComplimentary = true, CompedAt = now, CompedBy = operator, CompReason =
  request ?? "Assigned by operator", CompEndsAt`, `Status = Active`, `CurrentPeriodEnd = null`, `RenewalReminderSentAt = null`, request columns nulled with the
  Pending redemption released (`OperatorPlanOverride`). Price-0 plan (Free, Legacy, …) ⇒ comp columns and request nulled (same release), `CurrentPeriodEnd = null`,
  status per the existing lines `:502-503`. Audit unchanged action `tenant.plan_changed`, `After` gains `["kind"] = "complimentary" | "free" | "assigned"` (price 0 non-free plans such as
  Legacy are `assigned`, not comp).
- `DELETE /api/admin/tenants/{W}/comp` — ends comp now: the h1 transition below with `CurrentPeriodEnd = now` (the workspace gets the grace window to pay).
  Audit `billing.comp_ended` `{ source = "operator" }`.
- New-workspace invites — `CreateTenantInviteRequest` gains `Complimentary` (bool, default false), `CompReason?`, `CompEndsAt?`; passed through
  `CreateInviteRequest` to the three new `Invite` columns. Create-time validation: `Complimentary` requires `PlanId` of a live plan with `PriceMonthly > 0`.
  Accept (`AcceptCreateNewWorkspaceAsync :1005-1036`):
  - comp invite ⇒ plan lookup `DeletedAt == null` only; `Subscription { PlanId = plan, Status = Active, IsComplimentary = true, CompedAt = now,
    CompedBy = invite.CreatedBy, CompReason = invite.CompReason ?? "Invited as complimentary", CompEndsAt = invite.CompEndsAt }`;
  - non-comp paid invite ⇒ existing plan filter; `Subscription { PlanId = Free id, Status = PendingActivation, RequestedPlanId = plan, RequestedAt = now,
    RequestedBy = identity.PublicId, QuotedPrice = plan.PriceMonthly, QuotedCurrency = upper(plan.Currency) }` (list price, no code);
  - Free / missing Free row ⇒ no subscription row (today's zero-write path).
  Replace the comment `:992-1003` with one describing these three cases.
- `AuthService.RegisterAdminAsync :1559-1573` (paid plan chosen at signup) ⇒ the same non-comp shape as the invite (Free + request at list price). Existing
  rows of the old shape are untouched and remain payable (§3.1).

**(f) Period job — `BillingPeriodService.RunOnceAsync(DateTime now)`**, hosted `API/Hosted/BillingPeriodJob.cs`, every `Billing:IntervalMinutes` (default 60).
Settings `billing_grace_days` (default 7, F-B2) and `billing_reminder_days` (default 3). Each candidate row is processed in its own transaction: lock its
workspace, reload the row, re-check the predicate, then act. Order per pass:
- **h1 comp end** — `IsComplimentary && CompEndsAt <= now` → comp columns cleared, `CurrentPeriodEnd = <old CompEndsAt>`, `Status = PastDue`,
  `RenewalReminderSentAt = null`. Audit `billing.comp_ended` `{ source = "system" }`, `ActorKindOverride: System`.
- **h2 reminder** — `Status == Active && !IsComplimentary && CurrentPeriodEnd != null && now >= CurrentPeriodEnd - reminderDays && now < CurrentPeriodEnd &&
  RenewalReminderSentAt == null` → e-mail every live Workspace Admin (R8.7 membership query) the renewal quote; set `RenewalReminderSentAt = now`. No audit row
  (a notification stamp, not tenant state — same as DB-18's `deletion_reminder_sent_at`).
- **h3 past due** — `Status == Active && !IsComplimentary && CurrentPeriodEnd != null && CurrentPeriodEnd <= now` → `PastDue`; e-mail; audit `billing.past_due`.
- **h4 downgrade** — `Status == PastDue && CurrentPeriodEnd != null && CurrentPeriodEnd + graceDays <= now` → `PlanId = Free`, `Status = None`,
  `CurrentPeriodEnd = null`, `RenewalReminderSentAt = null` (request columns untouched); `await _billing.ChangePlanAsync(sub, freeId)`; e-mail; audit
  `billing.downgraded` `{ plan_id }` (before = old plan). Nothing else changes: data over the Free limits is kept (grandfather-safe creation checks).
Rows with `current_period_end IS NULL` and no due `comp_ends_at` are never selected. Existing production rows all qualify (§9 census).

**(g) Hard delete of a workspace** — `TenantService.HardDeleteAsync`: immediately before `DeleteOwnedAsync<Subscription>` (`:665`), set every `Pending` redemption of
W to `Released` / `WorkspaceDeleted`. `billing_payments` and `discount_redemptions` rows survive with `owner_id` → NULL (FK SetNull, R8.9); they are **not** added to
`HardDeleteOrder`.

**(h) Discount codes (super admin)** — create (code normalised; immutable), update every field except `code` (edits never touch redemptions — snapshots, F-B9);
no delete endpoint (deactivate, F-B8). List: `GET ?sort=most_used|newest|code&active=` returns each code with `appliedCount`, `pendingCount`
(one `GROUP BY discount_code_id, status` query), `most_used` sorts by `appliedCount + pendingCount DESC, id DESC`. Drill-down lists redemptions with workspace id,
`workspaces.name` (left join; NULL owner → "Deleted workspace"), plan name, snapshots, status, dates.

### 3.7 Tenancy, deletion, erase, operator boundary

- `billing_payments`, `discount_redemptions`: `Guid? OwnerId`, FK SetNull, **strict-own filter copied from `AuditEvent` (`AppDbContext.cs:322-327`)**,
  stamped explicitly with the workspace id (every writer knows W; never `TenantStamp.OwnerFor` — operators own nothing). Listed in
  `OperatorTableExclusions` with comments; the pin test is updated (R8.9).
- `discount_codes`, `discount_code_plans`: global catalog, no `owner_id`, no filter (like `plans`, `Plan.cs:6-12`).
- Workspace-facing payment DTOs **omit `note` and `recorded_by`** (R17 operator redaction); they show amount, currency, method, reference, paid date, period,
  kind. The operator view shows everything.
- R14: `requested_by`, `comped_by`, `created_by`, `recorded_by` are `public_id` content references; no column stores an e-mail or a person's name. The
  operator-typed text columns (`note`, `reference`, `comp_reason`, `label`) must not contain personal data — UI helper text says so; DB-11c §3.4 gains a row
  "billing text columns — kept by design (operator-authored, no PII by rule R8.9)".
- R18: billing rows are metadata; no content-read table edits.

### 3.8 Audit (R17) — new constants (frozen, R10) and whitelist keys

`AuditActions`: `BillingPlanRequested = "billing.plan_requested"`, `BillingRequestCancelled = "billing.request_cancelled"`,
`BillingRequestRejected = "billing.request_rejected"`, `BillingPaymentRecorded = "billing.payment_recorded"`, `BillingPaymentVoided = "billing.payment_voided"`,
`BillingCompEnded = "billing.comp_ended"`, `BillingPastDue = "billing.past_due"`, `BillingDowngraded = "billing.downgraded"`,
`DiscountCodeCreated = "discount_code.created"`, `DiscountCodeUpdated = "discount_code.updated"`.
`AuditTargets`: `DiscountCode = "discount_code"` (billing rows target `Workspace`).
`AuditFields.Allowed` additions (numbers, ids, enum names, timestamps only — reviewer: the orchestrator): `amount`, `currency`, `method`, `period_end`,
`payment_id`, `discount_code_id`. (`label`, `note`, `reference`, `code` stay out.)

### 3.9 Endpoints

| Method + route | Controller / tag | Auth | Attrs | Response (inner type) |
|---|---|---|---|---|
| `GET /api/admin/billing` | new `Admin/BillingController` / **`Billing`** | `Policies.Admin` + guard | — | `BillingSummaryResponse` (plan, status, period end, comp flag + end, request + quote, renewal quote, next due) |
| `GET /api/admin/billing/payments` | `Billing` | same | — | `List<WorkspacePaymentResponse>` |
| `POST /api/admin/billing/quote` `{planId, referenceCode?}` | `Billing` | same | `[EnableRateLimiting("danger")]`, `[NoAudit("price preview, no state change")]` | `BillingQuoteResponse` |
| `POST /api/admin/billing/request` `{planId, referenceCode?}` | `Billing` | same | `danger`, `[Audited(BillingPlanRequested)]` | `BillingSummaryResponse` |
| `DELETE /api/admin/billing/request` | `Billing` | same | `[Audited(BillingRequestCancelled)]` | `Result` |
| `GET /api/admin/tenants/{workspaceId}/billing` | `TenantsController` / `Tenants` | SuperAdmin | — | `OperatorBillingResponse` (summary + payments with note/recorded_by + redemptions) |
| `POST /api/admin/tenants/{workspaceId}/payments` | `Tenants` | SuperAdmin | `[Audited(BillingPaymentRecorded)]` | `OperatorPaymentResponse` |
| `POST /api/admin/tenants/{workspaceId}/payments/{paymentId:long}/void` `{reason}` | `Tenants` | SuperAdmin | `[Audited(BillingPaymentVoided)]` | `Result` |
| `DELETE /api/admin/tenants/{workspaceId}/billing/request` | `Tenants` | SuperAdmin | `[Audited(BillingRequestRejected)]` | `Result` |
| `DELETE /api/admin/tenants/{workspaceId}/comp` | `Tenants` | SuperAdmin | `[Audited(BillingCompEnded)]` | `Result` |
| `PATCH /api/admin/tenants/{workspaceId}/plan` (existing) | `Tenants` | SuperAdmin | unchanged | body `+ compReason?, compEndsAt?` |
| `GET /api/admin/discount-codes?sort=&active=` | new `Admin/DiscountCodesController` / **`DiscountCodes`** | SuperAdmin | — | `List<DiscountCodeResponse>` |
| `GET /api/admin/discount-codes/{id:int}` | `DiscountCodes` | SuperAdmin | — | `DiscountCodeResponse` |
| `POST /api/admin/discount-codes` | `DiscountCodes` | SuperAdmin | `[Audited(DiscountCodeCreated)]` | `DiscountCodeResponse` |
| `PATCH /api/admin/discount-codes/{id:int}` | `DiscountCodes` | SuperAdmin | `[Audited(DiscountCodeUpdated)]` | `DiscountCodeResponse` |
| `GET /api/admin/discount-codes/{id:int}/redemptions` | `DiscountCodes` | SuperAdmin | — | `List<DiscountRedemptionResponse>` |
| `POST /api/admin/tenants/invites` (existing) | `Tenants` | SuperAdmin | unchanged | body `+ complimentary, compReason?, compEndsAt?` |

Freeze (R19): the workspace-side POST/DELETE are freeze-gated by default (no `[AllowWhenWorkspacePaused]`); operator routes carry no tenant claim. No change to
`WorkspaceFreezeCoverageTests`. `TenantResponse` gains `RequestedPlanName`, `QuotedPrice`, `QuotedCurrency`, `CurrentPeriodEnd`, `IsComplimentary`, `CompEndsAt`
(`TenantService.ListAsync` projection `:124-130`).

### 3.10 IBillingProvider seam

Interface unchanged. Manual payments are provider-independent ledger writes; the service calls `ChangePlanAsync` on every plan switch (payment, downgrade)
and `ActivateAsync` after a payment, so a future gateway adapter observes the same events. A gateway later writes `External*` ids and its own payment rows
(`method` gains a value; enum append-only, R10).

### 3.11 What happens to every existing row

- `subscriptions`: 11 new columns NULL / `false`; `plan_id`, `status` untouched; entitlements unchanged; the job ignores them (NULL `current_period_end`,
  not comp). Old-shape `PendingActivation` rows (paid `plan_id`) keep their paid entitlements and are payable through (c) (period starts at payment).
- `invites`: 3 new columns `false`/NULL; §3.3 behaviour note for in-flight paid invites.
- `plans`: untouched (no column, no JSON key).
- New tables: empty.

### 3.12 Owner decisions (defaults apply)

| Id | Question | **Recommended default** |
|---|---|---|
| F-B1 | Grant paid entitlements before payment? | **No** — request stays on Free until "Mark as paid" (old-shape rows grandfathered). |
| F-B2 | Period end | `PastDue` with **7-day** grace (entitlements kept), then **downgrade to Free**; data kept; reminder 3 days before end. |
| F-B3 | Payments ledger immutable? | **Yes, append-only at three layers** (R17); corrections are `Void` rows. Makes this a contract deploy. |
| F-B4 | Period start | First payment / plan change: **at recording time**; renewal: **contiguous from the old period end** (late payers pay for the grace days; early renewals stack). |
| F-B5 | Amount ≠ quote | Allowed and recorded; no partial-payment model; the period is granted in full. |
| F-B6 | Code duration | Per code: **`Once` (first paid period) is the default**; `Forever` available (every renewal of the same plan). |
| F-B7 | Void scope | Latest payment only; restores the pre-payment plan/status/period; releases a redemption it applied; does not restore the request. |
| F-B8 | Code delete | **Deactivate only**, no delete; code string immutable (create a new code to "rename"). |
| F-B9 | Editing a code | Allowed for every field but `code`; redemptions keep their snapshots; open quotes are honoured at the quoted price. |
| F-B10 | In-flight paid invites at deploy | Operator re-issues them as complimentary (census §9 step 1c); no data migration. |
| F-B11 | Comp plan choice | **Any non-deleted plan**, including hidden/inactive (e.g. a private enterprise plan). |
| F-B12 | Financial rows after workspace deletion | **Kept** with `owner_id = NULL` (bookkeeping), never swept. |
| F-B13 | Codes at signup | **In-app only** in v1 (billing page); signup/invite accept take no code. |
| F-B14 | Proration on upgrade | **None** in v1: the new plan's full period starts at payment; remaining days of the old plan are not credited (operator may lower the amount). |

**Open questions (unverified facts):** Q20.1 production census of `subscriptions` by status/plan/period (§9 step 1a); Q20.2 in-flight paid invites (§9 1c);
Q20.3 whether any plan's `currency` is not a 3-letter code (§9 1b) — if so, fix the plan through the plan editor before the deploy.

## 4. Safety classification

**Additive (Migrations 1–2, R1) + one R4-constraint trigger (Migration 3, `[ContractMigration("DB-20")]`).** No drop/rename/narrow; no data write in any
migration. Because Migration 3 is flagged, the release is a **contract deploy** (R7): `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db20 bash scripts/deploy-api.sh`.
Migrations 1–2 need no marker (the DB-02 regex flags none of their operations). Single-doc deploy (R7.1 not needed).

## 5. File-level tasks

Enums (`Domain/Enums/`, one file each, ints exactly as §3.4 — append-only R10):
1. `DiscountKind.cs`, `DiscountDuration.cs`, `DiscountRedemptionStatus.cs`, `RedemptionReleaseReason.cs`, `BillingPaymentKind.cs`, `PaymentMethod.cs`.

Entities:
2. **`Domain/Entity/Subscription.cs`** — after `TrialEndsAt` add the 11 properties of §3.3 (`int? RequestedPlanId`, `Plan? RequestedPlan` navigation is **not** added —
   FK configured without navigation; `DateTime? RequestedAt`, `Guid? RequestedBy`, `decimal? QuotedPrice`, `string? QuotedCurrency`, `bool IsComplimentary`,
   `DateTime? CompedAt`, `Guid? CompedBy`, `string? CompReason`, `DateTime? CompEndsAt`, `DateTime? RenewalReminderSentAt`) with `///` comments from §3.1. Update the
   class summary: "PlanId = granted plan; a request lives in Requested*; DB-20".
3. **`Domain/Entity/Invite.cs`** — after `DisplayName` add `bool IsComplimentary`, `string? CompReason`, `DateTime? CompEndsAt` (summary: "new-workspace invites only; DB-20").
4. **`Domain/Entity/DiscountCode.cs`** (`: BaseEntity`), **`DiscountCodePlan.cs`** (`int DiscountCodeId`, `int PlanId`), **`DiscountRedemption.cs`**, **`BillingPayment.cs`** —
   properties exactly §3.4; `BillingPayment` all `init`; `DiscountRedemption` snapshots `init`, the four status fields `set`. `DiscountCode` has
   `List<DiscountCodePlan> PlanScopes { get; set; } = new();`. Doc-comment on the two owner-carrying ones cites **R8.9**.

Mappings (`Infrastructure/Mappings/`):
5. **`SubscriptionMapping.cs`** — change `b.ToTable("subscriptions")` to `b.ToTable("subscriptions", t => { …4 HasCheckConstraint… })` with the exact names/SQL of §3.3;
   add the 11 `Property(...).HasColumnName(...)` lines (`HasColumnType("numeric(12,2)")` for `QuotedPrice`, `HasMaxLength(3)` for `QuotedCurrency`, `HasMaxLength(200)`
   for `CompReason`, `.HasDefaultValue(false)` for `IsComplimentary`); `b.HasOne<Plan>().WithMany().HasForeignKey(x => x.RequestedPlanId).OnDelete(DeleteBehavior.Restrict)
   .HasConstraintName("fk_subscriptions_plans_requested_plan_id");`; the two partial indexes with `.HasFilter(...)` and `.HasDatabaseName(...)` (copy
   `WorkspaceMapping.cs:59-61`).
6. **`InviteMapping.cs`** — `ToTable("invites", t => t.HasCheckConstraint("ck_invites_comp_consistent", "<§3.3 SQL>"))`; three properties
   (`is_complimentary` `.HasDefaultValue(false)`, `comp_reason` `HasMaxLength(200)`, `comp_ends_at`).
7. **`DiscountCodeMapping.cs`**, **`DiscountCodePlanMapping.cs`** (`HasKey(x => new { x.DiscountCodeId, x.PlanId }).HasName("pk_discount_code_plans")`; FK to
   `DiscountCode` via `.HasOne<DiscountCode>().WithMany(c => c.PlanScopes)` Cascade), **`DiscountRedemptionMapping.cs`**, **`BillingPaymentMapping.cs`** — every
   column/index/check/FK name from §3.4; `UseIdentityByDefaultColumn()` for the two `long` ids (copy `AuditEventMapping.cs:13-26`); the descending index via
   `.IsDescending(false, true)` (copy `AuditEventMapping.cs:52-55`); unique partial indexes with `.IsUnique().HasFilter("status IN (1, 2)")` etc.; enum
   columns stored as int (default). Header comment on the two owner-carrying mappings: "R8.9 financial ledger table: FK SET NULL, survives the workspace".
8. **`Infrastructure/AppDbContext.cs`** — add `DbSet<DiscountCode> DiscountCodes`, `DbSet<DiscountCodePlan> DiscountCodePlans`,
   `DbSet<DiscountRedemption> DiscountRedemptions`, `DbSet<BillingPayment> BillingPayments` after `:77`; query filters for `BillingPayment` and
   `DiscountRedemption` copied from the `AuditEvent` block (`:322-327`) with a comment "DB-20 R8.9"; no filter for the two catalog types.
9. **`Infrastructure/AppDbContext.cs`** — add `EnforceBillingPaymentsAppendOnly()` beside `EnforceAuditEventsAppendOnly` (`:390-401`, same body, entity
   `BillingPayment`, message `"billing_payments is append-only (DB-20): update/delete is not allowed."`) and call it at the three sites `:483`, `:492`, `:499`.
10. **`Application/Abstractions/IUnitOfWork.cs`** + **`Infrastructure/Repository/UnitOfWork.cs`** — expose `DbSet<BillingPayment> BillingPayments` and
    `DbSet<DiscountRedemption> DiscountRedemptions` (non-`BaseEntity`, like `AuditEvents`); `DiscountCode` uses `Repository<DiscountCode>()`;
    `DiscountCodePlan` through the `PlanScopes` navigation only.

Migrations (R13 — after each, open the file and the snapshot diff and compare to §3.3/§3.4/§3.5; **stop and report** on any difference, e.g. an operation on a
table not named here, a missing check, or a renamed FK):
11. `just migrate name="AddBillingV1SubscriptionAndInviteColumns"` after tasks 2, 3, 5, 6 **only** (do tasks 4, 7–10 after this migration exists) → expected
    operations §3.3 last paragraph.
12. `just migrate name="AddBillingLedgerAndDiscountCodes"` after tasks 4, 7, 8, 10 → §3.4 last paragraph.
13. `just migrate name="AddBillingPaymentsAppendOnlyTrigger"` → EF generates an empty migration; replace its body per §3.5 (marker + `[ContractMigration("DB-20")]`
    + `Up()` `migrationBuilder.Sql(...)` + `Down()` dropping both triggers and the function). The snapshot must be unchanged by this one.

Application:
14. **`Application/Common/AuditActions.cs`**, **`AuditTargets.cs`**, **`AuditFields.cs`** — §3.8.
15. **`Application/Resources/MessageKeys.cs`** — new `public static class Billing` with `PlanMisconfigured`, `CodeInvalid`, `CodeNotForPlan`, `CodeAlreadyUsed`,
    `Complimentary`, `AlreadyOnPlan`, `NothingToPay`, `VoidOnlyLatest`, `StateChanged`, `RequestNotFound`, `InvalidAmount`, `InvalidPaidAt` (English literals, one sentence each)
    and `DiscountCode` with `NotFound`, `CodeTaken`, `CodeFormat`, `InvalidValue`, `InvalidWindow`.
16. **`Application/Services/{Interfaces,Implementation}/`** — `IBillingService`/`BillingService` (§3.6 a–e, summary/quote reads), `IBillingPeriodService`/`BillingPeriodService`
    (§3.6 f), `IDiscountCodeService`/`DiscountCodeService` (§3.6 h), static class `Application/Common/BillingMath.cs` (`PeriodPrice`, `PeriodEnd`, `Discount`, `Round`) so
    the arithmetic has one implementation (R20); `IEntitlementService.GetFreePlanIdAsync()`; DTOs under `Application/DTOs/Billing/` and `Application/DTOs/DiscountCode/`; FluentValidation
    validators for the three write bodies (code regex, value ranges, lengths, timestamps).
17. **Existing writers** — `AuthService.RegisterAdminAsync :1559-1573`, `InviteService.AcceptCreateNewWorkspaceAsync :1005-1036` (+ create-time validation), `TenantInviteService.CreateAsync
    :34-42` (pass the three fields), `CreateInviteRequest`/`CreateTenantInviteRequest` (three properties), `TenantService.ChangePlanAsync :459-526` and `ChangeTenantPlanRequest`
    (§3.6 e), `TenantService.HardDeleteAsync` (§3.6 g), `TenantService.ListAsync` (§3.9 last sentence), `PlanService.cs:185-189` in-use count also counts
    `RequestedPlanId == planId`, and `discount_code_plans` rows **do not** block a plan delete (soft delete; scope rows stay).
18. **`Application/Validators/Plan/PlanWriteDtoValidator.cs:22`** — `RuleFor(x => x.Currency).NotEmpty().Matches("^[A-Za-z]{3}$")`; `PlanService` stores `ToUpperInvariant()`.

API:
19. **`API/Controllers/Admin/BillingController.cs`** (new, `[Route("api/admin/billing")]`, `[Authorize(Policy = Policies.Admin)]`, `[Tags("Billing")]`, `[Produces("application/json")]`),
    **`API/Controllers/Admin/DiscountCodesController.cs`** (new, SuperAdmin, `[Tags("DiscountCodes")]`), five actions added to **`TenantsController.cs`** — attributes exactly §3.9;
    `[ProducesResponseType(typeof(<Inner>), 200)]` with the inner type.
20. **`API/Hosted/BillingPeriodJob.cs`** (copy the loop/scope pattern of `WorkspaceDeletionService.cs`) + `builder.Services.AddHostedService<BillingPeriodJob>();` after
    `Program.cs:132`; e-mail templates (en + ar, branding-aware, copy DB-18 §3.7's template pattern) for reminder / past-due / downgraded.
21. **`orval.config.ts:6`** — append `'Billing', 'DiscountCodes'` to `filters.tags`.

Docs:
22. `docs/db/DB-RULES.md` R8.9 + R20 (already written with this doc — verify the text matches what shipped); DB-11c §3.4 erase-inventory row (§3.7).

## 6. Tests

Copy fixtures from `Tests/MonetizationSignupTests.cs` (plans + subscriptions), `Tests/PlanEnforcementTests.cs` (`SeedPlanFor`), `Tests/TenantQueryFilterTests.cs`
(tenant A/B contexts), `Tests/AuditAppendOnlyGuardTests.cs` (SaveChanges guard), `Tests/UsageRollupPostgresTests.cs` (Postgres gate). New `Tests/Db20BillingTests.cs`,
`Tests/Db20DiscountCodeTests.cs`, `Tests/Db20BillingPeriodTests.cs`, `Tests/Db20BillingPostgresTests.cs`:

1. `BillingMath` table tests: percent rounding (`19.99 × 15 %` → `3.00`, final `16.99`), 100 % → final 0, fixed > price → final 0, fixed currency mismatch → not applicable,
   Monthly `Jan 31 + 1 month` → `Feb 28/29`, Yearly.
2. Request from Free: row created with Free `PlanId`, `PendingActivation`, quote = list price; **entitlements still Free** (`EntitlementService.GetForTenantAsync`).
3. Request with code: Pending redemption with every snapshot; a second request replaces it (`Released`/`Replaced`); `CodeAlreadyUsed` after an Applied use; max
   redemptions reached → `CodeInvalid`; outside window → `CodeInvalid`; wrong plan → `CodeNotForPlan`; lower-case input matches.
4. Record payment: first payment → `Active`, requested plan granted, period `[now, now+interval)`, redemption `Applied`, request columns null, `billing_provider = "manual"`;
   renewal on Active → period starts at old end; renewal on PastDue → contiguous; comp → `Complimentary` conflict; Free-only row without request → `NothingToPay`.
5. Void: latest only; restores `PlanId/Status/CurrentPeriodEnd`; `PendingActivation` on Free → `None`; releases a first-applied redemption; a second void of the same
   payment → conflict (unique index on Sqlite/InMemory via service check).
6. Append-only: modifying or deleting a tracked `BillingPayment` throws on `SaveChangesAsync` (copy `AuditAppendOnlyGuardTests`).
7. Comp via `PATCH plan`: paid plan → comp stamped, `CurrentPeriodEnd` null, pending request cleared + redemption `OperatorPlanOverride`; hidden plan allowed; Free clears comp.
   Invite: comp invite → Active comp row with `CompedBy = invite.CreatedBy`; non-comp paid invite → Free + request at list price; Free invite → no row.
8. Register-admin with a paid plan → Free + request (update `MonetizationSignupTests.Signup_WithPaidPlan_CreatesPendingActivationSubscription` to the new shape).
9. Period job (fixed `now`): h1–h4 each; a row with NULL `current_period_end` (the Legacy/pre-BILL-1 shape) is untouched after 100 simulated days; downgrade keeps
   projects above the Free cap; idempotent second pass changes nothing; reminder sent once.
10. **Existing data survives (seed → migrate → assert)**: in `Db20BillingPostgresTests` (gated), apply migrations up to `ClearUsersRoleIdForMembers`
    (`db.GetService<IMigrator>().MigrateAsync("20260924051230_ClearUsersRoleIdForMembers")`), insert with raw SQL a
    workspace + `subscriptions(Legacy, Active)`, `subscriptions(paid, PendingActivation)` and an invite with a paid plan, apply the three DB-20 migrations, assert the rows
    are unchanged and the new columns are `false`/NULL, and the job pass leaves them untouched.
11. **Tenancy (R8.5)**: tenant B's context reads **no** `BillingPayment`/`DiscountRedemption` of A; super admin reads both.
12. Hard delete (`Tests/WorkspaceTests.cs`): add `typeof(BillingPayment)`, `typeof(DiscountRedemption)` to `OperatorTableExclusions` with R8.9 comments and to the pinned
    array; new test: hard-deleting W keeps its payments/redemptions with `OwnerId == null` and turns its Pending redemption into `Released/WorkspaceDeleted`.
13. Postgres-gated: trigger refuses `UPDATE billing_payments SET amount = 1` and `DELETE`, admits the SET-NULL detach; two parallel requests with the last
    available code slot → exactly one Pending redemption; two parallel payments on the same workspace → two contiguous periods (no overlap).
14. Workspace payment DTO serialisation contains no `note`/`recordedBy` (R17 redaction test shape).
15. `Tests/AuditCoverageTests` passes (every new non-GET action attributed); `Tests/MigrationSafetyTests` passes (marker + attribute agree on Migration 3 only).

## 7. Acceptance criteria

1. `just test` green; `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` → none; migration count **86** (83 + 3; 87 if DB-19 shipped first).
2. Migrations 1–2 contain only the operations listed in §3.3/§3.4; only Migration 3 carries `[ContractMigration("DB-20")]` and the marker.
3. `grep -rn "float\|double" Domain/Entity/BillingPayment.cs Domain/Entity/DiscountRedemption.cs Domain/Entity/DiscountCode.cs Application/Common/BillingMath.cs` → nothing.
4. `grep -rn "Status = SubscriptionStatus.PendingActivation" Application` shows only rows whose `PlanId` is the Free id (register-admin, invite accept, request).
5. `EntitlementService.cs` is unchanged by this doc (`git diff --stat` shows no change to it except the extracted `GetFreePlanIdAsync`).
6. On the R11 rehearsal: `\d billing_payments` lists both triggers; `SELECT conname FROM pg_constraint WHERE NOT convalidated;` → empty; `UPDATE billing_payments SET amount = amount;`
   on an inserted test row raises `billing_payments is append-only`.
7. `POST /api/admin/billing/request` → `POST /api/admin/tenants/{W}/payments` → `GET /api/admin/billing` shows `Active`, the plan, a period end one interval out.

## 8. Rollback

- Migration 3 `Down()` drops both triggers and the function (lossless). Migration 2 `Down()` drops the four tables — **destroys any payment/redemption/code rows recorded
  since the deploy**; before rolling it back, take `bash scripts/backup-db.sh pre-db20-rollback`. Migration 1 `Down()` drops 14 columns (loses comp/request state since
  the deploy) — same dump requirement.
- Preferred rollback on the day of the deploy (nothing of value recorded yet): restore the `pre-db20` dump (`DEPLOY.md` § Restore) and `git checkout` the commit before
  the first DB-20 commit, then `up -d --build api` (R7.1 point 6 shape). After real payments exist, **do not** roll back the schema: revert the code only (the tables are
  inert without it) and fix forward.

## 9. Release steps

1. **Prod pre-checks** (on the VM, read-only psql), paste outputs into the PR:
   a. `SELECT s.status, p.slug, p.price_monthly, (s.current_period_end IS NULL) AS no_period, count(*) FROM subscriptions s JOIN plans p ON p.id = s.plan_id WHERE s.deleted_at IS NULL GROUP BY 1,2,3,4 ORDER BY 1,2;`
      **Every row must show `no_period = true`**; otherwise stop and report (the job would act on it).
   b. `SELECT id, slug, currency FROM plans WHERE deleted_at IS NULL AND currency !~ '^[A-Za-z]{3}$';` → must be empty (fix via the plan editor otherwise).
   c. `SELECT id, plan_id, expires_at FROM invites WHERE owner_id IS NULL AND plan_id IS NOT NULL AND deleted_at IS NULL AND revoked_at IS NULL AND expires_at > now() AND (max_uses IS NULL OR uses < max_uses);`
      → list for the operator (F-B10).
2. R11 rehearsal on a same-day prod dump: apply, run 1a–1c again (unchanged), run acceptance 6, boot the API and run one job pass (`Billing:IntervalMinutes=1`) — assert
   `SELECT count(*) FROM subscriptions WHERE updated_at > now() - interval '5 minutes';` = 0.
3. `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db20 bash scripts/deploy-api.sh` (stops the API, labelled dump `pre-db20`, boots with the gate open — R6/R7).
4. Verify on prod: `SELECT tgname FROM pg_trigger WHERE tgrelid = 'billing_payments'::regclass AND NOT tgisinternal;` → both triggers; `SELECT conname FROM pg_constraint WHERE NOT convalidated;`
   → empty; `docker compose -f docker-compose.prod.yml logs --since 10m api | grep -iE "migrat|error|AUDIT GAP|billing"`; repeat 1a (unchanged).
5. Operator follow-ups: re-issue 1c invites as complimentary; set `billing_grace_days` / `billing_reminder_days` if not the defaults.
6. Regenerate `docs/db/SCHEMA.md` (four new tables; new `subscriptions` / `invites` columns; R8.9 / R20).

## 10. Out of scope

Any payment gateway, webhooks, invoices/PDF receipts, taxes/VAT, proration, refunds (a Void is a correction, not a refund), multi-currency conversion, trials
(`Trialing`/`TrialEndsAt` stay unused), seat-based pricing, reference codes at signup (F-B13), `EntitlementService` semantics, DB-19, the widget, the CLI, `e2e/`,
workspace export of billing data.

## 11. Non-schema checklist (API / service / dashboard)

- [ ] Services: `BillingService`, `BillingPeriodService`, `DiscountCodeService`, `BillingMath`; extracted `GetFreePlanIdAsync`; hosted `BillingPeriodJob` + three e-mail templates (en, ar).
- [ ] Controllers: `BillingController` (`Billing`), `DiscountCodesController` (`DiscountCodes`), five `TenantsController` actions; inner-type `ProducesResponseType`;
      `orval.config.ts` tags added (a missing tag silently generates nothing).
- [ ] Existing flows changed: register-admin paid plan, invite accept (comp / non-comp), tenant invite create (comp fields), `PATCH …/plan` (comp semantics,
      any plan), hard delete (release pending), plan delete in-use count, plan currency validator, `TenantResponse` fields.
- [ ] Settings: `billing_grace_days` (7), `billing_reminder_days` (3) constants in `ISettingsService`; config `Billing:IntervalMinutes` (60).
- [ ] Audit: 10 actions, 1 target, 6 whitelist keys; `[NoAudit]` on quote.
- [ ] DB-11c §3.4 erase-inventory row; DB-13 §3.4 unchanged (no content table).
- [ ] **Dashboard tasks** (dashboard-agent, once per phase): workspace **Billing** page (current plan/status/period, comp badge, pending request with quote and
      "cancel request", plan picker with reference-code field and live quote, payment history without operator notes, past-due banner with grace end); super-admin
      **tenant billing drawer** (summary, "Mark as paid" form: amount prefilled with the quote, currency, paid date, method, reference, note; payment list with "Void" on the
      latest; reject request; end comp); **plan assignment dialog** gains comp reason + optional end date; **tenant invite form** gains "Complimentary" + reason + end date;
      **Reference codes** page (list sortable by most used/newest/code, active filter, create/edit form — code immutable after create, kind/value/currency, window, max uses,
      plan scope multi-select, label, note; drill-down of redemptions with workspace, plan, prices, status). Tenants list shows requested plan / quote / period end / comp.
      All strings via i18n in every locale; helper text on note/reference/label: "Do not enter personal data". The quote call is rate-limited (`danger`,
      10 / 10 min): call it on an explicit "Apply code" click and on plan change, never per keystroke.
- [ ] Rebranding agent: new tables/columns/routes/settings keys are brand-neutral; e-mail templates are branding-aware — invoke the rebranding-agent when the templates land
      (they are customer-visible).
