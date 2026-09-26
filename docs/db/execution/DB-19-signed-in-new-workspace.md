# DB-19 (WS-NEW) — Signed-in "+ New workspace", governed by two new plan levers

Owner request (2026-09-25, relayed by the orchestrator): an identity that is already signed in can create another workspace
without re-entering a password, through the same creation core as self-signup; approval and a per-identity cap are **plan-driven**
via two new entitlement levers, governed by the plan of the caller's **current** workspace.

Rules: **R1** (no column added; one snapshot-only migration), R3 (the Legacy lever values are an idempotent `AdminSeeder` step, not a data
migration), **R7** (ordinary deploy — the DB-02 guard flags nothing), **R8.7** (membership queries only; "owned" is a membership count),
R10 (the two JSON keys, the audit `source` value and the rate-limit policy name are frozen once shipped), R11, **R12** (the `plans.entitlements`
jsonb bag gains two optional keys — add-only versioning), R13, R14 (no new person-bearing column), **R16** (session workspace from the JWT;
`ICurrentUser.Id` is the identity), **R17** (one audit row per creation, whitelisted keys only), **R19** (the action lives on a class-level
`[AllowWhenWorkspacePaused]` controller — justified in §3.6).
**Class: Additive** (no table/column change; the migration has empty `Up()`/`Down()`). **Status: written
2026-09-25; implemented 2026-09-26 (backend only — `dotnet build`/`dotnet test` green, 1560 tests, Postgres
concurrency test passing, `has-pending-model-changes` clean; not yet deployed, not yet synced to the
dashboard).**
Owner decisions D19.1–D19.9 (§3.8) proceed on the recommended defaults unless the owner says otherwise.

**Dependencies.** DB-11a (memberships, D13 multi-workspace admins), DB-11b (switch-workspace), DB-12 (`IAuditWriter`), DB-14 (verification),
DB-17/18 (demo + lifecycle guard). Planned against `main` @ `4da0091`, **83 migrations**, newest `20260924051230_ClearUsersRoleIdForMembers`.
Independent of DB-20 (BILL-1); either may ship first.

## 1. Goal

Today a person who already administers a workspace can get a second one only by signing up again anonymously
(`POST /api/auth/register-admin` with the same e-mail and password, which parks the workspace for super-admin approval) or by receiving a
super-admin "new workspace" invite. This doc adds `POST /api/me/workspaces {name}` for a signed-in Workspace Admin: it creates a workspace
with the caller as its Workspace Admin, **active immediately or pending approval** depending on the current plan's
`NewWorkspaceRequiresApproval`, and refuses when the caller already owns `MaxOwnedWorkspaces` workspaces. User-visible reason: agencies and
freelancers can open a workspace per client from the dashboard's workspace switcher without a second signup, while the operator keeps
control through the plan catalogue.

## 2. Prerequisites (verified facts, 2026-09-25 @ `4da0091`)

**Entitlement bag**
- `Domain/ValueObjects/PlanEntitlements.cs:15-37` — 17 nullable properties; doc-comment `:8-13`: nullable int → catalog default, `-1` = unlimited,
  bools nullable; "MUST stay in sync with `EntitlementCatalog.All`; a unit test asserts this".
- `Infrastructure/Mappings/PlanMapping.cs:48` — `b.OwnsOne(x => x.Entitlements, e => e.ToJson("entitlements"));` → column `plans.entitlements jsonb NOT NULL`
  (`20260702014430_AddPlansAndSubscriptions.cs:57`). JSON keys are the C# property names (EF `ToJson` default), e.g. `"MaxProjects"`.
  A key missing from stored JSON materialises as `null`.
- The snapshot lists each owned JSON property (`Infrastructure/Migrations/AppDbContextModelSnapshot.cs:2958-3020`), so adding a VO property changes the
  snapshot and needs a migration with empty `Up()`/`Down()` or DB-10's `has-pending-model-changes` fails. Precedent for an empty migration:
  `Infrastructure/Migrations/20260624130359_AddElementScreenshotUrl.cs` (both bodies empty; it is in the DB-02 baseline).
- `Application/Common/EntitlementCatalog.cs:28-47` key constants; `:49-53` `Int(...)`/`Bool(...)` helpers; `:60-82` `All` (7 enforced, 10 display-only);
  `:92-105` `ResolveInt`/`ResolveBool` (stored ?? default).
- `Tests/EntitlementCatalogTests.cs:15-21` key-set equality test; `:48-60` `SevenEnforcedLevers_AreFlagged` asserts `Count == 7`.
- `Application/DTOs/Plan/PlanEntitlementsDto.cs:7-27` mirrors the VO; `Application/Services/Implementation/PlanService.cs:193` `MapEntitlements(PlanEntitlementsDto)`
  and `:214` `MapEntitlementsDto(PlanEntitlements)` copy each property by name; `Application/Validators/Plan/PlanWriteDtoValidator.cs:30-46` checks every
  DTO property is a known catalog key and ints are `>= -1`.
- `Application/Services/Implementation/EntitlementService.cs:30-34` `GetForTenantAsync(tenantId)` (resolves the subscription's `PlanId`, missing row ⇒
  Free, `:97-129`); `:91-95` every `CheckCountAsync`/`EnforceFlagAsync` returns Success while AppSetting `enforcement_enabled` is false (fallback false,
  `Application/Services/Interfaces/ISettingsService.cs:42`). **Its production value is not known to this doc** (open question Q19.1).
- `API/Seed/AdminSeeder.cs:302-321` seeds the hidden, inactive Legacy plan with `BuildUnlimitedEntitlements()` (`:392-405`: every int `-1`, **every bool
  `true`**); `:328` the one-time `LegacyBackfillCompleted` guard is the precedent for a guarded, run-once seeder step. `:381-390` `BuildFreeEntitlements()`
  seeds the Free plan from catalog defaults (only when Free does not exist yet).

**Creation core to reuse**
- `Application/Services/Implementation/AuthService.cs:1453-1602` `RegisterAdminAsync`: resolves the global `Workspace Admin` role with
  `IgnoreQueryFilters()` (`:1466-1476`); `workspaceId = Guid.NewGuid()` (`:1482`); adds the `Workspace` row with `Name = Workspace.PlaceholderName`,
  `CreatedAt = UtcNow`, `CreatedBy = workspaceId` and **saves it first** (`:1498-1507`, FK order); `_memberships.JoinAsync(identity, workspaceId, role,
  ApprovalStatus.Pending, isActive: false, inviteId: null)` (`:1525-1533`); free/no plan ⇒ **no subscription row** (`:1540-1574`, missing row ⇒ Free);
  audit `AuthRegisterAdmin` + `WorkspaceCreated` with `After { source = "self_serve" }` (`:1576-1599`). No transaction wraps it.
- `Application/Services/Interfaces/IMembershipService.cs:22` `GetMembershipAsync`, `:42-49` `JoinAsync` (stages only; throws for a super-admin role),
  `:19` `FindIdentityByPublicIdAsync`.
- `ISettingsService.DefaultSignupPlan` (`ISettingsService.cs:47`, "default free") is **declared but read nowhere** (grep: no reader outside the
  interface). "The default signup plan" is therefore: no subscription row ⇒ Free.
- Pending approval is a **membership** state: `ApprovalStatus.Pending` + `IsActive = false`; the super admin approves through
  `PATCH /api/admin/tenants/{workspaceId}/status {action:"approve"}` → `TenantService.SetStatusAsync` (`TenantService.cs:319-324`).
  `ApprovalStatus { Approved = 1, Pending = 2, Rejected = 3 }` (`Domain/Enums/ApprovalStatus.cs:3`).
- `Application/Common/WorkspaceLifecycleGuard.cs:19-56` `CanManageAsync`: refuses API-key sessions (`KeyScopes != null`), quick-access, super admin,
  impersonation, `TenantId != workspace.Id`; requires a live, active, approved membership whose role name is exactly `"Workspace Admin"` (Deputy excluded);
  refuses a live unconverted demo (`DemoExpiresAt != null && DemoConvertedAt == null`). This doc reuses exactly that rule for the caller.
- Workspace name rules: `WorkspaceService.cs:84-90` (trim; empty → `MessageKeys.Workspace.NameRequired`; `> 120` (`:21`) → `NameTooLong`; any control
  char → `NameInvalid`); DB check `ck_workspaces_name_not_blank` (`WorkspaceMapping.cs:15`).
- `Domain/Entity/User.cs:45` `IsDemo`; `User.EmailVerifiedAt` (DB-14).

**Concurrency, audit, rate limit, freeze**
- `IUnitOfWork.ExecuteInTransactionAsync` (`Application/Abstractions/IUnitOfWork.cs:22`) and `ExecuteSqlRawAsync` (`:60`, no-op on InMemory).
  Row-lock precedent: `UserService.cs:80-84` `SELECT id FROM workspace_memberships WHERE owner_id = {0} FOR UPDATE`; `WorkspaceLifecycleService.cs:206-209`.
- `Application/Common/AuditActions.cs:100` `WorkspaceCreated = "workspace.created"`; `AuditFields.cs:13-44` whitelist contains `source`, `approval_status`,
  `count` (no additions needed).
- `API/Extensions/RateLimitingExtensions.cs:93-102` `"danger"` policy; `:267` `DangerPartitionKey(ctx)` (sub claim + IP when authenticated) — the
  partition function to reuse.
- `API/Controllers/MeController.cs:15-22` — `[Route("api/me")]`, `[Authorize]`, class-level `[AllowWhenWorkspacePaused]` (`:21`), no `[Tags]` → Swagger
  tag `Me`, already in `orval.config.ts:6`. `Tests/WorkspaceFreezeCoverageTests.cs:33` pins the class-level list
  `{ AuthController, MeController, MfaController }` — a new action on `MeController` needs **no** change there.
- `POST /api/auth/switch-workspace` accepts a full session token (`AuthController.cs:85-89`, `AuthService.SwitchWorkspaceAsync :1055`) — the dashboard
  uses it to enter the new workspace.

**Migration tooling** (`justfile`): `just migrate name="<PascalCase>"`, `just test`, `just fmt`.

## 3. Design

### 3.1 Two new levers (JSON keys in `plans.entitlements`; no column)

| Property (= JSON key, frozen R10) | Type | Catalog label | Enforced flag | Catalog default | Meaning |
|---|---|---|---|---|---|
| `MaxOwnedWorkspaces` | `int?` | `"Owned workspaces"` | `true` | **1** (D19.1) | Max workspaces the caller may *own* (§3.2) **before** a new one is created. `-1` = unlimited. `0` = the endpoint is disabled for callers governed by this plan. |
| `NewWorkspaceRequiresApproval` | `bool?` | `"New workspaces need approval"` | `true` | **true** (D19.1) | `true` ⇒ the new workspace's admin membership is `Pending`/inactive (the self-signup state); `false` ⇒ `Approved`/active immediately. |

Polarity note (read this, implementer): `NewWorkspaceRequiresApproval` is the first bool where `true` is the **restrictive** value. The generic
"unlimited plan = every bool `true`" loop in `AdminSeeder.BuildUnlimitedEntitlements` would therefore make a fresh Legacy plan *more* restricted. Task 7
special-cases it; no other generic loop over bools exists (grep `EntitlementCatalog.All.Values` → only `AdminSeeder.cs:381,396`).

Every existing `plans` row: its stored JSON lacks both keys → both resolve to the catalog defaults (Free and every paid plan ⇒ cap 1, approval required)
until a super admin edits the plan. The Legacy plan gets `-1` / `false` once, via task 7 (D19.2).

**Versioning (R12):** optional keys only; a later change of meaning is a new key.

### 3.2 "Owned" — exact definition

`owned(identity)` = the number of rows satisfying **all** of:

```
workspace_memberships m JOIN roles r ON r.id = m.role_id JOIN workspaces w ON w.id = m.owner_id
WHERE m.user_id = <identity users.id>
  AND m.left_at IS NULL AND m.deleted_at IS NULL          -- live membership (ended rows never count)
  AND r.name = 'Workspace Admin'                           -- the owner role; Deputy and every other role excluded
  AND m.approval_status IN (1, 2)                          -- Approved or Pending; Rejected (3) excluded
  AND w.deleted_at IS NULL                                  -- the workspace row is live
```

Included on purpose: **disabled** admin memberships (`is_active = false`: a super-admin suspension is recoverable, the workspace is still theirs);
**paused** workspaces (reversible); workspaces **scheduled for deletion** (DB-18 grace is cancellable — excluding them would let
schedule → create → cancel exceed the cap; cost: the owner waits out the 7-day grace before the slot frees); **demo** workspaces (a live demo admin
cannot call the endpoint anyway, §3.3). Included **Pending**: otherwise a caller under an approval-required plan could queue unlimited pending
workspaces. Excluded **Rejected**: the operator said no; that workspace grants nothing and the rate limit (§3.5) bounds retries.

C# (in the new service, §3.4) — membership query per R8.7, never `users.owner_id`:

```csharp
var liveWorkspaceIds = _unitOfWork.Workspaces.IgnoreQueryFilters().Where(w => w.DeletedAt == null).Select(w => w.Id);
var owned = await _unitOfWork.Repository<WorkspaceMembership>().Query().IgnoreQueryFilters()
    .CountAsync(m => m.UserId == identity.Id && m.LeftAt == null && m.DeletedAt == null
        && m.Role.Name == WorkspaceAdminRoleName
        && (m.ApprovalStatus == ApprovalStatus.Approved || m.ApprovalStatus == ApprovalStatus.Pending)
        && liveWorkspaceIds.Contains(m.OwnerId));
```

Existing multi-workspace admins keep every workspace they have: the cap is checked only on this endpoint, only before creating, and never removes,
disables or hides anything (grandfather-safe, same principle as `EntitlementService.cs:53-54`).

### 3.3 Who may call

Refuse with `Forbidden(MessageKeys.Common.Forbidden)` unless **all** hold (the `WorkspaceLifecycleGuard.CanManageAsync` rule applied to the caller's
current workspace, plus two identity checks):

1. `ICurrentUser.Id` is a `Guid`, `TenantId` is non-null, not `IsSuperAdmin`, not `IsImpersonating`, not `IsQuickAccess`, `KeyScopes == null`.
2. The current workspace row exists, `DeletedAt == null`, and is **not** a live unconverted demo.
3. The caller's membership there is live, `IsActive`, `Approved`, role name exactly `"Workspace Admin"` (D19.3).
4. The identity is live, `IsActive`, `!IsDemo`, and `EmailVerifiedAt != null` — else `Forbidden(MessageKeys.Auth.EmailNotVerified)`
   (existing DB-14 key, `Application/Resources/MessageKeys.cs:36`) (D19.6).

The current workspace being **paused or scheduled for deletion does not block** (§3.6).

### 3.4 Algorithm — `IWorkspaceCreationService.CreateForCurrentIdentityAsync(CreateWorkspaceRequest)`

1. Validate the name with the rename rules (§2): extract a static `WorkspaceNameRules.Validate(string? raw, out string trimmed) → string? messageKey`
   used by both `WorkspaceService.RenameAsync` and this service (no behaviour change for rename).
2. Run §3.3; load `identity` via `_memberships.FindIdentityByPublicIdAsync(_currentUser.Id)`.
3. `var ent = await _entitlements.GetForTenantAsync(currentTenantId);`
   `var max = EntitlementCatalog.ResolveInt(ent, EntitlementCatalog.MaxOwnedWorkspaces);`
   `var requiresApproval = EntitlementCatalog.ResolveBool(ent, EntitlementCatalog.NewWorkspaceRequiresApproval);`
   **Do not** call `CheckCountAsync` — it is a no-op while `enforcement_enabled` is off, and these two levers are abuse controls that must hold regardless
   (D19.5).
4. Resolve the global `Workspace Admin` role exactly as `AuthService.cs:1466-1476`.
5. `await _unitOfWork.ExecuteInTransactionAsync(async () => { ... })` containing, in order:
   a. `await _unitOfWork.ExecuteSqlRawAsync("SELECT id FROM users WHERE id = {0} FOR UPDATE", identity.Id);` — serialises concurrent creates by the same
      identity (READ COMMITTED: the count in (b) is a new statement and sees a committed concurrent insert). No advisory lock, no SERIALIZABLE: a row lock
      on the one row every create for this identity shares is the repo's existing pattern (§2) and needs no retry loop.
   b. `owned` per §3.2. If `max != -1 && owned >= max` → throw a private sentinel exception caught outside the transaction and returned as
      `Result.LimitReached(MessageKeys.Workspace.OwnedLimitReached, new PlanLimit(EntitlementCatalog.MaxOwnedWorkspaces, owned, max, currentPlanId))`
      (`currentPlanId`: read it from the same subscription lookup; add `Task<int> GetPlanIdForTenantAsync(Guid)` to `IEntitlementService` returning the
      already-cached `ResolveAsync(...).PlanId`).
   c. `workspaceId = Guid.NewGuid()`; `AddAsync(new Workspace { Id = workspaceId, Name = trimmed, CreatedAt = DateTime.UtcNow, CreatedBy = identity.PublicId })`;
      `SaveChangesAsync()` (FK order, as `AuthService.cs:1498-1507`). `CreatedBy` is the caller's `public_id` (R14 content reference) — not the workspace id
      as register-admin does, because here the creator is known.
   d. `_memberships.JoinAsync(identity, workspaceId, role, requiresApproval ? ApprovalStatus.Pending : ApprovalStatus.Approved, isActive: !requiresApproval, inviteId: null)`;
      `SaveChangesAsync()`.
   e. **No subscription row** (Free) — same as register-admin without a plan.
   f. Audit (inside the block, after the saves — R17): `new AuditEntry(AuditActions.WorkspaceCreated, AuditTargets.Workspace, workspaceId.ToString(), workspaceId,
      After: new() { ["source"] = "signed_in", ["approval_status"] = requiresApproval ? "Pending" : "Approved", ["count"] = (owned + 1).ToString() })`
      with default actor (the session user). `owner_id` of the audit row = the **new** workspace.
6. Return `Result<CreateWorkspaceResponse>.Success(new() { WorkspaceId = workspaceId, Name = trimmed, Status = requiresApproval ? "pending_approval" : "active" })`.

Nothing is written to the current workspace, to `users`, or to any stamp (no session is revoked — R16).

### 3.5 Endpoints

| Method + route | Controller | Auth | Rate limit | Audit | Returns |
|---|---|---|---|---|---|
| `POST /api/me/workspaces` body `{ "name": string }` | `MeController` (tag `Me`) | `[Authorize]` | `[EnableRateLimiting("workspace-create")]` | `[Audited(AuditActions.WorkspaceCreated)]` | `[ProducesResponseType(typeof(CreateWorkspaceResponse), 200)]`; 400 (validation / `IsLimitReached`), 403 |
| `GET /api/me/workspaces/allowance` | `MeController` | `[Authorize]` | none | GET (no attribute) | `[ProducesResponseType(typeof(WorkspaceAllowanceResponse), 200)]` `{ owned, max, requiresApproval, canCreate }` — `canCreate` = §3.3 passes **and** (`max == -1 || owned < max`) |

New rate-limit policy `"workspace-create"` in `RateLimitingExtensions.cs` next to `"danger"`: fixed window, `PermitLimit = 5` (config
`Security:RateLimits:WorkspaceCreatePerHour`), `Window = 1 h`, `QueueLimit = 0`, partition `DangerPartitionKey(ctx)` (D19.8).

### 3.6 Freeze (R19)

`MeController` is class-level `[AllowWhenWorkspacePaused]`. That is correct for this action: it writes nothing into the (possibly frozen) current
workspace; it creates a different, unfrozen one. The one-line reason goes in the action's doc-comment. No change to `WorkspaceFreezeCoverageTests`.

### 3.7 Existing rows

No table or column changes. Every `workspaces`, `workspace_memberships`, `subscriptions` and `plans` row is untouched by the migration. The Legacy plan
row's JSON gains `"MaxOwnedWorkspaces": -1, "NewWorkspaceRequiresApproval": false` once, through task 7, only if both keys are absent (null).

### 3.8 Owner decisions (defaults apply)

| Id | Question | **Recommended default** | Why |
|---|---|---|---|
| D19.1 | Catalog defaults | `MaxOwnedWorkspaces = 1`, `NewWorkspaceRequiresApproval = true` | Conservative (owner's suggestion). Existing admins lose nothing; the operator raises the value per plan in the plan editor. Consequence: on Free and on every paid plan the button is disabled for anyone who already owns one workspace until the plan is edited. |
| D19.2 | Legacy plan values | `-1` / `false`, once, via a guarded seeder step | Legacy's contract is "unlimited everything" (`AdminSeeder.cs:261-263`). |
| D19.3 | Who may call | Workspace Admin (not Deputy, not members) of the current workspace | The governing plan belongs to that workspace; letting a Client/Tester member spend the agency's plan allowance on their own workspaces is a loophole. |
| D19.4 | "Owned" | §3.2 (Pending/disabled/paused/scheduled/demo count; Rejected/ended do not) | Closes the schedule→create→cancel and pending-queue bypasses. |
| D19.5 | Kill-switch | Levers are enforced even when `enforcement_enabled` is false | Otherwise, with the switch off, any admin mints unlimited active workspaces. |
| D19.6 | Verified e-mail | Required | Unverified accounts must not multiply tenants. |
| D19.7 | `register-admin` for an existing identity | Unchanged (uncapped; always pending super-admin approval) | Anonymous path has no "current workspace"; the super-admin approval is its gate. Revisit if abused. |
| D19.8 | Rate limit | 5 / hour / identity+IP | Human-scale; the cap is the real limit. |
| D19.9 | Starting plan | Free (no subscription row), `default_signup_plan` stays unread | Mirrors register-admin exactly. Honouring the setting is a separate change for both paths. |
| (alt to D19.1) | Rename to positive polarity (`NewWorkspaceAutoApproved`, default `false`) | **Keep the owner's name** + task 7 special case | Rename only if the owner prefers it **before** ship; afterwards the key is frozen (R10). |

**Open questions (facts this doc could not verify):** Q19.1 production value of `app_settings.enforcement_enabled` (irrelevant to this doc per D19.5,
relevant to how the dashboard words the limit). Q19.2 how many identities own ≥ 2 workspaces today (census in §9 step 1).

## 4. Safety classification

**Additive.** One migration with empty `Up()`/`Down()` (snapshot sync only); the DB-02 guard flags nothing → no marker, no `[ContractMigration]`,
ordinary `bash scripts/deploy-api.sh` (R6 dump is taken by the script). The seeder step writes one row once, is idempotent and guarded (R3).

## 5. File-level tasks

1. **`Domain/ValueObjects/PlanEntitlements.cs`** — after `public int? MaxTenantWidePredefinedActions { get; set; }` (`:24`) add
   `public int? MaxOwnedWorkspaces { get; set; }` and `public bool? NewWorkspaceRequiresApproval { get; set; }`, each with a `///` summary copied from §3.1
   (include: "Governed by the plan of the caller's CURRENT workspace; DB-19"). Add to the class summary: "Add optional keys only (R12)."
2. **`Application/Common/EntitlementCatalog.cs`** — after `:35` add
   `public const string MaxOwnedWorkspaces = nameof(PlanEntitlements.MaxOwnedWorkspaces);` and
   `public const string NewWorkspaceRequiresApproval = nameof(PlanEntitlements.NewWorkspaceRequiresApproval);`. In `All`, after the
   `MaxTenantWidePredefinedActions` entry (`:70`) add `Int(MaxOwnedWorkspaces, "Owned workspaces", enforced: true, def: 1),` and
   `Bool(NewWorkspaceRequiresApproval, "New workspaces need approval", enforced: true, def: true),`.
3. **`Application/DTOs/Plan/PlanEntitlementsDto.cs`** — add the same two properties after `MaxTenantWidePredefinedActions` (`:15`).
4. **`Application/Services/Implementation/PlanService.cs`** — in `MapEntitlements` (`:193`) and `MapEntitlementsDto` (`:214`) add
   `MaxOwnedWorkspaces = d.MaxOwnedWorkspaces, NewWorkspaceRequiresApproval = d.NewWorkspaceRequiresApproval,` (resp. `e.`).
5. **Migration** — run `just migrate name="AddPlanEntitlementsWorkspaceLevers"`. Expected file `Infrastructure/Migrations/<ts>_AddPlanEntitlementsWorkspaceLevers.cs`
   with **empty** `Up()` and `Down()`; the snapshot diff is exactly two added `b1.Property<...>` lines inside the `Plan.Entitlements` owned block
   (`int?` `MaxOwnedWorkspaces`, `bool?` `NewWorkspaceRequiresApproval`, both `HasColumnType` integer/boolean). **Stop and report** if `Up()` contains any
   operation or the snapshot changes anywhere else (R13).
6. **`Application/Common/WorkspaceNameRules.cs`** (new, static) — `public const int MaxLength = 120;`
   `public static string? Validate(string? raw, out string trimmed)` returning `MessageKeys.Workspace.NameRequired | NameTooLong | NameInvalid | null`
   with exactly the logic of `WorkspaceService.cs:84-90`. Then in **`WorkspaceService.cs`** replace `:84-90` with a call to it (keep `MaxNameLength`
   const or point it at `WorkspaceNameRules.MaxLength`).
7. **`API/Seed/AdminSeeder.cs`** —
   a. in `BuildUnlimitedEntitlements` (`:392-405`), after the loop: `e.NewWorkspaceRequiresApproval = false; // restrictive-polarity bool (DB-19 §3.1)`.
   b. in `SeedPlansAsync`, immediately **before** the `LegacyBackfillCompleted` guard (`:328`), add a guarded step:
      if `!await settings.GetBoolAsync(ISettingsService.LegacyWorkspaceLeversBackfilled, false)`: load `legacy` (already in scope); if
      `legacy.Entitlements.MaxOwnedWorkspaces == null` set `-1`; if `legacy.Entitlements.NewWorkspaceRequiresApproval == null` set `false`; `SaveChangesAsync()`;
      then `await settings.SetBoolAsync(ISettingsService.LegacyWorkspaceLeversBackfilled, true)`. It must run even when `LegacyBackfillCompleted` is already true
      (which is the production case) — hence its position before that `return`.
8. **`Application/Services/Interfaces/ISettingsService.cs`** — add `public const string LegacyWorkspaceLeversBackfilled = "legacy_workspace_levers_backfilled";`
   with a one-line summary (DB-19 task 7).
9. **`Application/Services/Interfaces/IEntitlementService.cs` + `EntitlementService.cs`** — add `Task<int> GetPlanIdForTenantAsync(Guid tenantId)` returning
   `(await ResolveAsync(tenantId)).PlanId`.
10. **`Application/Resources/MessageKeys.cs`** — in `Workspace` (next to `NameTooLong`, `:219`) add
    `public const string OwnedLimitReached = "You already own the maximum number of workspaces your plan allows.";` (API message keys are English literals —
    there are no API-side locale files; the dashboard translates by the `IsLimitReached` flag + `Limit.Lever`).
11. **`Application/DTOs/Workspace/CreateWorkspaceRequest.cs`** (new) `{ string Name }`; **`CreateWorkspaceResponse.cs`** `{ Guid WorkspaceId; string Name; string Status }`;
    **`WorkspaceAllowanceResponse.cs`** `{ int Owned; int Max; bool RequiresApproval; bool CanCreate }`.
12. **`Application/Services/Interfaces/IWorkspaceCreationService.cs`** + **`Application/Services/Implementation/WorkspaceCreationService.cs`** (new) — §3.3/§3.4
    exactly; `GetAllowanceAsync()` for the GET. Dependencies: `IUnitOfWork`, `ICurrentUser`, `IMembershipService`, `IEntitlementService`, `IAuditWriter`.
    Registration: automatic — `Application/DependencyInjection.cs:11-13` Scrutor-registers every class whose name ends in `Service` as scoped.
13. **`API/Controllers/MeController.cs`** — inject `IWorkspaceCreationService`; add the two actions of §3.5 (map `IsForbidden` → 403, `IsLimitReached`/failure → 400,
    success → 200 `Ok(result)`). Doc-comment on the POST: "Allowed while the current workspace is paused: writes only to the new workspace (R19, DB-19 §3.6)."
14. **`API/Extensions/RateLimitingExtensions.cs`** — add policy `"workspace-create"` (§3.5) after `"danger"`.

## 6. Tests

Copy fixtures from `Tests/MonetizationSignupTests.cs` (plan + subscription seeding, InMemory context) and `Tests/WorkspaceMembershipTests.cs`
(two workspaces, one identity, membership helpers). New file `Tests/Db19NewWorkspaceTests.cs`:

1. `Create_UnderCap_ApprovalFalse_IsActiveImmediately` — plan `{ MaxOwnedWorkspaces = 3, NewWorkspaceRequiresApproval = false }`; caller owns 1 → new
   workspace row with the given name, membership `Approved`/active, **no** subscription row, one `workspace.created` audit row with `source=signed_in`.
2. `Create_ApprovalRequired_IsPending` — default plan (keys absent) but cap raised to 2 → membership `Pending`, `IsActive == false`.
3. `Create_AtCap_ReturnsLimitReached_AndWritesNothing` — keys absent (cap 1), caller owns 1 → `IsLimitReached`, `Limit.Lever == "MaxOwnedWorkspaces"`,
   workspace count unchanged.
4. `Owned_Counts_PendingDisabledPausedScheduled_NotRejectedOrEnded` — seed one membership per state; assert the count equals §3.2.
5. `Unlimited_MinusOne_NeverBlocks` and `Zero_BlocksEvenFirstExtra`.
6. `GoverningPlan_IsCurrentWorkspacePlan` — identity admins A (plan cap 1) and B (plan cap 5), owns 2; session on A → blocked; session on B → allowed.
7. `Refuses_Deputy_Member_SuperAdmin_Impersonation_ApiKey_Demo_Unverified` — one assertion each (`Forbidden`).
8. `EnforcementSwitchOff_StillEnforced` — `enforcement_enabled=false` in the fake settings; cap still blocks.
9. `ExistingWorkspaces_Untouched_WhenOverCap` (existing-data survival) — identity owns 3 under a cap-1 plan; call fails; all 3 memberships unchanged
   (role, approval, active, stamps).
10. Tenancy (R8.5 shape, copy `Tests/TenantQueryFilterTests.cs`): after creation, a session on the **new** workspace sees no project/membership of the
    caller's other workspace, and a session of an unrelated tenant sees neither.
11. `Tests/EntitlementCatalogTests.cs` — rename `SevenEnforcedLevers_AreFlagged` → `NineEnforcedLevers_AreFlagged`, `Assert.Equal(9, …)`, add two
    `Assert.Contains`; add `NewLevers_Defaults` asserting `ResolveInt(empty, MaxOwnedWorkspaces) == 1` and `ResolveBool(empty, NewWorkspaceRequiresApproval) == true`.
12. `Tests/PlanSeederTests.cs` — add: Legacy gets `-1`/`false` on first seed; a second seed run with the flag set does not overwrite an operator-edited value.
13. Postgres-gated concurrency test (copy the `POINTER_TEST_PG` gate of `Tests/UsageRollupPostgresTests.cs`): two parallel creates by an identity at
    `owned = max - 1` → exactly one succeeds.

## 7. Acceptance criteria

1. `just test` green; `EntitlementCatalogTests.CatalogKeys_Equal_VoPropertyNames` passes with 19 keys.
2. The new migration's `Up()` and `Down()` are empty; `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` reports none.
3. `grep -rn "CheckCountAsync" Application/Services/Implementation/WorkspaceCreationService.cs` → no match (D19.5).
4. `grep -rn "owner_id\|\.OwnerId ==" Application/Services/Implementation/WorkspaceCreationService.cs` shows only membership/workspace predicates (R8.7).
5. `GET /api/admin/plans` returns both new keys (null for Free/paid plans, `-1`/`false` for Legacy after first boot).
6. `POST /api/me/workspaces` as a verified Workspace Admin on a plan with `MaxOwnedWorkspaces=-1, NewWorkspaceRequiresApproval=false` → 200, `status=active`;
   `POST /api/auth/switch-workspace` into it → 200.
7. `Tests/AuditCoverageTests` and `Tests/WorkspaceFreezeCoverageTests` green without edits to their pinned lists.

## 8. Rollback

`Down()` is empty (nothing to undo in the schema). Rolling back the code (`git revert` + ordinary deploy) leaves two JSON keys on the Legacy row that
the older build has no member for. Whether EF 8's `ToJson` reader skips unknown keys is **not verified by this doc** — the R11 rehearsal (§9 step 2)
therefore also boots the **previous** commit against the rehearsed database and calls `GET /api/admin/plans`. If that fails, the rollback includes
`UPDATE plans SET entitlements = entitlements - 'MaxOwnedWorkspaces' - 'NewWorkspaceRequiresApproval' WHERE slug = 'legacy';` (plain psql, after the
code revert, before the API restarts). Workspaces created through the endpoint stay (they are ordinary workspaces). No dump beyond the script's
`pre-deploy` one.

## 9. Release steps

1. Census (informational, on the VM or the R11 rehearsal copy):
   `SELECT n AS owned, count(*) AS identities FROM (SELECT m.user_id, count(*) n FROM workspace_memberships m JOIN roles r ON r.id = m.role_id JOIN workspaces w ON w.id = m.owner_id WHERE r.name = 'Workspace Admin' AND m.left_at IS NULL AND m.deleted_at IS NULL AND m.approval_status IN (1,2) AND w.deleted_at IS NULL GROUP BY m.user_id) t GROUP BY n ORDER BY n;`
   Paste into the PR. Anyone with `owned >= 1` is blocked on Free until D19.1 values are raised — tell the owner.
2. R11 rehearsal on a fresh prod dump; after `database update`, boot the API once against it and run
   `SELECT slug, entitlements->'MaxOwnedWorkspaces', entitlements->'NewWorkspaceRequiresApproval' FROM plans WHERE deleted_at IS NULL;`
   → Legacy `-1`/`false`; every other plan `null`/`null`.
3. `bash scripts/deploy-api.sh` (ordinary; takes `pre-deploy` dump, R6).
4. Verify: the step-2 query on prod; `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|error|AUDIT GAP"` is clean.
5. Regenerate the `plans` row note in `docs/db/SCHEMA.md` (two new entitlement keys).

## 10. Out of scope

`register-admin`, invites, `TenantService`, billing (DB-20), the widget, the CLI, `e2e/`, any other entitlement key, `default_signup_plan`. Do not touch
`EntitlementService.CheckCountAsync`/`EnforceFlagAsync` semantics.

## 11. Non-schema checklist (API / service / dashboard)

- [ ] API: two `MeController` actions (tag `Me`, already in `orval.config.ts`); inner types annotated (`CreateWorkspaceResponse`, `WorkspaceAllowanceResponse`).
- [ ] Rate-limit policy `workspace-create`; config key `Security:RateLimits:WorkspaceCreatePerHour` documented in `.env.prod.example` only if other
      `Security:RateLimits:*` keys are listed there.
- [ ] Message key `Workspace.OwnedLimitReached` (English literal, API side); dashboard strings via its i18n in every locale.
- [ ] **Dashboard tasks** (for the dashboard-agent, once per phase): workspace switcher gets "+ New workspace" (enabled from `GET /api/me/workspaces/allowance`
      `.canCreate`; when false show `owned/max` and an upgrade hint); modal with name (1–120 chars); on `status=active` call switch-workspace and reload;
      on `pending_approval` show "awaiting approval" and list it greyed; `IsLimitReached` → the existing plan-limit UI. Plan editor: two new fields
      ("Owned workspaces" int with -1 = unlimited; "New workspaces need approval" checkbox, tri-state default = catalog). All strings via i18n, every locale.
- [ ] Rebranding agent: no brand-carrying surface (JSON keys and route are brand-neutral) — no invocation needed.
- [ ] Super-admin tenants list already shows pending approvals (`TenantResponse.ApprovalStatus`) — no change.
