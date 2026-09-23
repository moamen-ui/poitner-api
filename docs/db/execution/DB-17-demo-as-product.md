# DB-17 — Demo as a product (F4): the demo is a workspace with a TTL, convertible in place, cleaned up by a job

Roadmap: **R5.7** (`docs/roadmap/DX-UX-CX-PLAN.md:70,169` — "A demo is a real workspace with a 24 h TTL, a visible 'convert to your workspace'
action that keeps its data, and cleanup by the retention job"), founder decision **F4** and foundations report §3 step 7
(`docs/roadmap/meetings/2026-09-22-foundations/07-final-report.md`), DB-15 §10 ("R5.7 keeps `UpgradeAsync` as the convert primitive").
Rules: R1 (six nullable columns, one partial index, one check constraint on a 2-row table), **R2** (column move `users` → `workspaces`: this doc is
the *expand + read-switch + dual-write* release; the *contract* is DB-11e), **R3** (one guarded backfill migration, marked, `[ContractMigration("DB-17")]`),
R5, R6, **R7** (the backfill makes this a contract deploy: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db17`), R8 (workspace-scoped reads;
memberships never `users.owner_id` — R8.7), R10 (`auth.demo.*`, `tenant.demo_*`, `demo_started`, `workspace_converted` strings are frozen; new
strings appended), R13, **R14** (the demo throttle key must use `EmailNormalizer` + a pseudonym, not a raw address), R16 (convert rotates the
identity stamp — unchanged), **R17** (new endpoint → `[Audited]`; the expiry job's delete already writes `tenant.hard_deleted` as `System`).
**Class: Expand** (additive columns + one R3 backfill; nothing dropped — `users.expires_at/DemoExtended/DemoCommentCapOverride/DemoTtlHoursOverride`
keep being written and are dropped by **DB-11e** after one release with zero readers). **Status: written 2026-09-23, not implemented.**
Owner decisions D17.1–D17.9 have defaults (§3.9); **D17.5 (convert without waiting for verification) is the one to look at consciously.**

**Dependencies.** DB-11a (memberships, workspace id ≠ admin public_id), DB-12 (`IAuditWriter`), DB-14 (verification rail; `UpgradeAsync` already
resets `EmailVerifiedAt`), DB-15 (`demo_started`/`workspace_converted` emission sites) — all in production. Independent of DB-16 (either order);
the expiry path reuses `TenantService.HardDeleteAsync`, whose screenshot folder delete DB-16 verified and whose leftovers DB-16's orphan sweep catches.

## 1. Goal

A visitor clicks "Try the demo" (`landing/index.html:375,387,644` → `demo.pointer.moamen.work`, the dashboard build's demo entry,
`LoginPage.tsx:198-240`), gets a seeded workspace with a synthetic login, and has 24 hours to reach a first applied comment (F4's funnel). Today that
mostly exists (`DemoService.ProvisionAsync`/`UpgradeAsync`, `DemoCleanupService`), but the demo is modelled as **flags on the admin's `users` row**
(`users.is_demo/expires_at/DemoExtended/…`, `User.cs:50-64`) — not on the workspace — so: the cleanup job reads the legacy `users.owner_id`
(`DemoCleanupService.cs:43-54`), which DB-11a declared never-read (`User.cs:12-15`, R8.7); an expired demo can still log in and keep a 12 h JWT
(`AuthService.LoginAsync :626` has no expiry check; `JwtTokenService.cs:110`); the only extension is a super-admin action keyed by `users.id`
(`TenantsController.cs:118-121`); the requester's real e-mail is written **raw** into `app_settings.key` as a throttle counter that nothing ever
deletes (`DemoService.cs:78-79,231-240`; `SCHEMA.md` `app_settings`: "no delete path today"); and the dashboard's countdown lives in
`sessionStorage` from the provision response, so it is gone after a reload in another tab (`DemoPanel.tsx:43-60`). This doc puts the TTL on the
workspace, gives the demo admin one self-service extension and a real "keep this workspace" conversion that preserves everything, makes expiry
enforceable (login refused, sweep every 15 min, warning e-mail two hours before), and closes the PII leak. User-visible: countdown that survives
reloads, "Extend once" and "Keep this workspace" in the banner, a reminder e-mail, and a converted workspace that is simply the same workspace with
a real admin.

## 2. Prerequisites (verified facts, 2026-09-23 @ `78823ce`)

**Where demo state lives today (all on the demo admin's identity row)**
- `Domain/Entity/User.cs:50-64`: `OwnerId Guid?` (legacy, "never read after DB-11a", `:12-15`), `IsDemo`, `ExpiresAt`, `DemoExtended` (one-time
  extension used), `DemoCommentCapOverride`, `DemoTtlHoursOverride`, `RecipientEmail` (the real human address; cleared on upgrade). Physical names:
  `is_demo`, `expires_at` + index `IX_users_expires_at` (`UserMapping.cs:57-59`; `20260629155415_AddDemoColumns`), and **PascalCase**
  `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"` (no `HasColumnName`; `20260701121004_AddDemoTenantConfig.cs:13-30`,
  snapshot `AppDbContextModelSnapshot.cs` ~`:2151-2160`) — quote them in SQL.
- `Domain/Entity/Workspace.cs:8-25` (`Id, Name, CreatedAt/By, UpdatedAt/By, DeletedAt/By`; not a `BaseEntity`), `Infrastructure/Mappings/WorkspaceMapping.cs:11-29`
  (`ck_workspaces_name_not_blank`, `name varchar(120)`), filter `AppDbContext.cs:321-325` (strict-own on `Id`), `IUnitOfWork.Workspaces` (`IUnitOfWork.cs:12`).
  Production: 2 rows (`SCHEMA.md`).
- Readers of the **identity** fact `users.is_demo` (stay as they are — a synthetic `demo-<slug>@demo.pointer` login has no real address):
  `EmailVerificationService.cs:60-64` (`IsExempt`), `:123` (resend refused), `API/Auth/RequireVerifiedEmailFilter.cs:92-94` (gate exempt),
  `Application/Common/UserMapper.cs:23` (`EmailVerified`), `AuthService.cs:150` (password reset excludes demos).
- Readers of the **workspace** facts (TTL / caps / extension) that this doc moves: `API/Hosted/DemoCleanupService.cs:43-54` (expired query on
  `users`, `u.OwnerId!.Value`), `:80` (`HardDeleteAsync(pid, reason: "demo_expired")`), `:17,20` (30 s delay, hourly);
  `CommentService.cs:165-190` (comment cap via `CurrentAdminAsync(owner)?.User.IsDemo`, `DemoCommentCapOverride`, setting default 10);
  `ExportImportService.cs:592-598` (import cap, same shape); `TenantService.cs:140-160` (`TenantResponse.IsDemo/ExpiresAt/DemoExtended/…` from
  `admin?.User`), `:343-386` `ExtendDemoAsync(int id)` (one-time, anchor = later of now/expiry, `DemoTtlHoursOverride ?? DemoTtlHours`, audit
  `TenantDemoExtended` with `expires_at`), `:388-430` `SetDemoConfigAsync(int id, cap?, ttl?)`; `DemoService.UpgradeAsync :352-357` (`!IsDemo → Forbidden`,
  `ExpiresAt < now → DemoExpired`), `:368-372` (clears the flags), `:416,433,442` (`user.OwnerId` for the workspace id — legacy read);
  `Application/DTOs/Tenant/TenantResponse.cs:42-46`; `ActivationStatsService.cs:247` (`IsDemo` derived from `demo_started` facts — untouched).
- `DemoService.ProvisionAsync` (`DemoService.cs:54-325`): settings `:60-68` (`ISettingsService.DemoMaxActive/DemoTtlHours/DemoPerEmailPerDay`,
  `ISettingsService.cs:17-20`, defaults 100/24/3, `DemoCommentCap` default 10), e-mail validation `:70-75` (`Trim()` + `MailAddress`), **throttle key
  `demo_email_{recipientEmail.ToLowerInvariant()}_{yyyyMMdd}` in `app_settings`** `:78-88,231-240` (raw address; `.ToLowerInvariant()` is the
  R14-forbidden normaliser), active cap on `users` `:91-100`, mint `:116-138` (`workspaceId = Guid.NewGuid()`, `ExpiresAt = now + ttlHours` `:136`,
  `RecipientEmail` `:137`), `Workspace { Name = "Demo Workspace" }` `:123,140-148`, project `:152-160`, three seeded comments `:163-215`, membership
  `:218-226`, `demo_started` `:244-265` (with `ClearChangeTracker` on failure), token `:269-271`, credentials e-mail `:276-291` (`BuildDemoEmailHtml :470-501`,
  `_branding.BuildResponseAsync("", …)` `:277`), audit `auth.demo.provisioned` + `workspace.created` `:293-310`. Constructor `:31-52`
  (`IUnitOfWork, IPasswordHasher, ITokenService, IEmailService, ISettingsService, IBrandingService, IMembershipService, IAuditWriter?, IEmailVerificationService?`).
- `DemoService.UpgradeAsync` (`:327-456`): validator `UpgradeDemoValidator` (`Application/Validators/UpgradeDemoValidator.cs:11-16`, e-mail +
  `StrongPassword`), `EmailNormalizer.NormalizeRequired` `:337`, conflict on existing identity `:362-364` (D7), in-place mutation `:368-386`
  (`EmailVerifiedAt = null` `:376` — DB-14 §3.2 "this is exactly where the address becomes real (F4)"; stamp rotation `:384`), 23505 → Conflict `:388-395`,
  `InvalidateGate` `:401`, `SendAsync` verification `:404`, `workspace_converted` `:409-429`, token `:433-436`, audit `auth.demo.upgraded` `:438-445`,
  response `UpgradeDemoResponse { Token, User = MeResponse }` `:448-455`. `IDemoService.cs:12,19`. DTOs `Application/DTOs/Demo/*.cs`
  (`DemoRequest { Email }`, `DemoSessionResponse { Token, Email, Password, ProjectKey, ExpiresAt, ServerUrl, EmailSent }`, `UpgradeDemoRequest { Email, Password, DisplayName? }`).
- `API/Controllers/DemoController.cs`: `[Route("api/demo")]`, `[Tags("Demo")]` (in `orval.config.ts:6`), `Create` `[AllowAnonymous] [Audited(AuthDemoProvisioned)] [EnableRateLimiting("demo")]`
  (`:18-38`; 429 mapping by message text `:32-35`), `Upgrade` `[Authorize] [Audited(AuthDemoUpgraded)]` (`:40-55`, `User.GetId()`).
  Rate policy `"demo"` = 3 per hour per IP (`API/Extensions/RateLimitingExtensions.cs:78-86`).
- Login/session: `AuthService.LoginAsync` (`:626`) — identity `:644`, password `:649`, memberships `var memberships = await _memberships.ListForIdentityAsync(user.Id);` `:726`,
  `Status = "no-workspace"` `:733`; **no demo-expiry check anywhere** (`grep -n "ExpiresAt\|DemoExpired" AuthService.cs` → only `:1387`, quick-access
  links). `JwtTokenService.Issue` expiry `now + LifetimeHours (12)` (`:110`). Per-request revocation = stamp validator (`AuthenticationExtensions.cs:92-`,
  cached ~60 s) — a membership removed by `HardDeleteAsync` (`TenantService.cs:580`) kills the token within 60 s.
- `MeResponse` (`Application/DTOs/Auth/MeResponse.cs:3-36`) has no demo fields; `UserMapper.ToMeResponse(user, role, tenantName)` (`UserMapper.cs:17-43`).
- Audit: `AuditActions.cs:37-38` (`auth.demo.provisioned`, `auth.demo.upgraded`), `:75` (`workspace.created`), `:78-79` (`tenant.demo_extended`,
  `tenant.demo_config_changed`), `:81` (`tenant.hard_deleted`); `AuditFields.Allowed` (`AuditFields.cs:13-43`) includes `reason, count, expires_at, source, minutes, kind`.
  `AuditActorKind.System` (`Domain/Enums/AuditActorKind.cs`). `TenantService.HardDeleteAsync :511-549` writes `tenant.hard_deleted` with
  `ActorKindOverride: reason == "demo_expired" ? System : null` (`:544`) before deleting the owner folder (`:549`) and the rows.
- Routes keyed by workspace id (DB-11a F9 precedent): `TenantsController.cs:104` (`{workspaceId:guid}/status`), `:145` (`/plan`), `:162` (`DELETE {workspaceId:guid}`);
  still keyed by `users.id`: `:118` (`{id:int}/extend`), `:129` (`{id:int}/demo-config`); `ITenantService.cs` `ExtendDemoAsync(int)`, `SetDemoConfigAsync(int, …)`,
  `HardDeleteAsync(Guid, reason)` (`:18`). No test references `ExtendDemoAsync`/`SetDemoConfigAsync` (`grep -rn … Tests/` → 0).
- Pseudonymisation: `Application/Common/PseudonymHasher.EmailHash(normalisedEmail)` (`PseudonymHasher.cs:17`, 16 hex — R17). `EmailNormalizer.NormalizeRequired`.
  `AppSetting { Key, Value } : BaseEntity` (`Domain/Entity/AppSetting.cs:3-7`; `key` required, no length cap, `AppSettingMapping.cs:23`).
- Migration marker form to copy: `Infrastructure/Migrations/20260923062150_BackfillUsersEmailVerifiedAt.cs:8-10,22` (`// DB-RULES: R3 backfill approved <date> by Moamen (owner; … relayed by the orchestrator; docs/db/execution/DB-17-demo-as-product.md)` + `[ContractMigration("DB-14")]`);
  `Tests/MigrationSafetyTests.cs:80-82` is the regex that accepts it. Membership columns for the backfill join: `workspace_memberships.user_id`, `owner_id`,
  `left_at`, `deleted_at` (`WorkspaceMembershipMapping.cs:19-29,48`).
- Config/compose: `docker-compose.prod.yml:41-46` (`Retention__*` block — the demo sweep needs no new env; its cadence is a constant), `API/Program.cs:106-107`
  (`DemoCleanupService`, `RetentionService` registered).
- Tests to copy: `Tests/DemoUpgradeTests.cs:76-100` (`Build(dbName)` harness — InMemory, real `UnitOfWork`/`MembershipService`, `RecordingTokenService`,
  `RecordingEmailVerification`), `Tests/DemoSessionEmailTests.cs:153-162` (`SpyEmailService`), `Tests/DemoServiceAnalyticsFailureTests.cs` (interceptor-forced
  failure), `Tests/AuditWrittenByServicesTests.cs:873-900` (`HardDeleteAsync` reason `demo_expired` → System actor), `Tests/WorkspaceMembershipTests.cs:608,780`
  (`HardDeleteAsync(workspaceA)`), `Tests/RetentionServiceTests.cs:44-70` (Sqlite `TestDb`), `Tests/TenantQueryFilterTests.cs`, `Tests/AuditCoverageTests.cs`
  (fails the build for an unattributed non-GET on `DemoController` — R17).
- Dashboard: `../pointer-dashboard/react/src/components/DemoPanel.tsx` (session from `sessionStorage` `:43-60`, countdown `:165-175`, "keep workspace"
  button `:221-227`, upgrade dialog state `:24-49`, `usePostApiDemoUpgrade` `:22,95,133`), `features/login/LoginPage.tsx:198-240` (demo form → `POST /api/demo`),
  `features/tenants/TenantsPage.tsx:520,536-547` (`extendMut.mutate({ id: tenant.id! })`, demo-config dialog). Landing CTA `landing/index.html:375,387,644`.
  Privacy `landing/privacy.html:244-266` (retention table; no demo row).

## 3. Design

### 3.1 Schema — six nullable columns on `workspaces`, one partial index, one check constraint (R1)

| property | column | type | null | meaning |
|---|---|---|---|---|
| `DemoExpiresAt` | `demo_expires_at` | `timestamptz` | yes | **non-null ⇔ this workspace is a live demo with a TTL.** Set at provision (`now + ttl`), moved by extension, **nulled by conversion**, read by the sweep |
| `DemoExtendedAt` | `demo_extended_at` | `timestamptz` | yes | the one extension was used (by whom is the audit row). Replaces `users."DemoExtended"` bool |
| `DemoConvertedAt` | `demo_converted_at` | `timestamptz` | yes | conversion happened (F4 funnel fact in row form; `workspace_converted` usage event stays the analytics record) |
| `DemoExpiryWarnedAt` | `demo_expiry_warned_at` | `timestamptz` | yes | the T-2 h reminder was attempted (idempotency for §3.5) |
| `DemoCommentCapOverride` | `demo_comment_cap_override` | `int` | yes | per-demo cap (operator); replaces `users."DemoCommentCapOverride"` |
| `DemoTtlHoursOverride` | `demo_ttl_hours_override` | `int` | yes | per-demo TTL used by extensions; replaces `users."DemoTtlHoursOverride"` |

Index `ix_workspaces_demo_expires_at (demo_expires_at) WHERE demo_expires_at IS NOT NULL` (`b.HasIndex(x => x.DemoExpiresAt).HasFilter("demo_expires_at IS NOT NULL").HasDatabaseName("ix_workspaces_demo_expires_at")`)
— the sweep's predicate; plain `CreateIndex` (R4: 2 rows).
Check constraint `ck_workspaces_demo_ttl_or_converted`: `demo_expires_at IS NULL OR demo_converted_at IS NULL` — a converted workspace has no TTL
(added in the `ToTable(…, t => …)` lambda next to `ck_workspaces_name_not_blank`, `WorkspaceMapping.cs:11-14`). R1 allows `AddCheckConstraint`; the
backfill (§3.2) never sets `demo_converted_at`, so it cannot trip.

Doc-comment block on `Workspace` (verbatim, above `DemoExpiresAt`):
```csharp
/// <summary>
/// DB-17 (F4). Demo state lives on the WORKSPACE: non-null DemoExpiresAt = a live demo that the sweep hard-deletes at expiry;
/// DemoConvertedAt = it was kept ("convert to your workspace") and the TTL was cleared (ck_workspaces_demo_ttl_or_converted).
/// The admin identity's users.is_demo remains the separate fact "synthetic demo login without a real address" (exempt from e-mail
/// verification, excluded from password reset). users.expires_at / "DemoExtended" / "DemoCommentCapOverride" / "DemoTtlHoursOverride"
/// are dual-written until DB-11e drops them (DB-RULES R2).
/// </summary>
```

**Migration 1 `AddWorkspacesDemoColumns`** (scaffolded): six `AddColumn` on `workspaces`, one `CreateIndex` (with filter), one `AddCheckConstraint`.
Nothing else (an operation on any other table → stop and report). No marker. `Down()` = generated (drop constraint, index, columns).
**Every existing row:** untouched (`NULL` everywhere; both production workspaces are real, not demos).

### 3.2 Migration 2 `BackfillWorkspacesDemoState` — R3 backfill, marked, `[ContractMigration("DB-17")]`

Copies the TTL/extension/overrides of every **live** demo from its admin identity to the workspace, joining through the membership (never
`users.owner_id`, R8.7). Hand-written `migrationBuilder.Sql(...)` in a migration with **nothing else** in it (scaffold with no model change: `just migrate name="BackfillWorkspacesDemoState"`
produces empty `Up`/`Down`; fill them). Guarded so a second run is a no-op:

```sql
UPDATE workspaces w
SET demo_expires_at           = u.expires_at,
    demo_extended_at          = CASE WHEN u."DemoExtended" THEN COALESCE(u.updated_at, u.created_at) END,
    demo_comment_cap_override = u."DemoCommentCapOverride",
    demo_ttl_hours_override   = u."DemoTtlHoursOverride"
FROM users u
JOIN workspace_memberships m ON m.user_id = u.id AND m.owner_id = w.id AND m.left_at IS NULL AND m.deleted_at IS NULL
WHERE u.is_demo = true
  AND u.deleted_at IS NULL
  AND u.expires_at IS NOT NULL
  AND w.deleted_at IS NULL
  AND w.demo_expires_at IS NULL
  AND w.demo_converted_at IS NULL;
```
`Down()`: `UPDATE workspaces SET demo_expires_at = NULL, demo_extended_at = NULL, demo_comment_cap_override = NULL, demo_ttl_hours_override = NULL WHERE demo_converted_at IS NULL;`
(reversible: the `users` columns still hold the source — that is what dual-write buys). Marker line above `Up()` (copy `20260923062150:8-9` verbatim
with `DB-17` and this file's path) + `[ContractMigration("DB-17")]` on the class. Row count expected in production: **0–3** (demos live at deploy
time; `DemoMaxActive` caps at 100). Not batched (R3: far below 100 000).

**Pre-check (prod, read-only, pasted into the PR — §9 step 1):**
`SELECT count(*) FROM users WHERE is_demo AND deleted_at IS NULL AND expires_at > now();` and
`SELECT count(*) FROM users u WHERE u.is_demo AND u.deleted_at IS NULL AND u.expires_at IS NOT NULL AND NOT EXISTS (SELECT 1 FROM workspace_memberships m WHERE m.user_id = u.id AND m.left_at IS NULL AND m.deleted_at IS NULL);`
— the second must be **0** (a demo identity without a live membership would be skipped by the backfill and never expire; if non-zero, list them and
let `HardDeleteAsync` them by hand before deploying, or accept and note).

### 3.3 Read-switch and dual-write (R2 steps 1–2 in one release; rollback-safe because both shapes stay written)

| Site | Today | After |
|---|---|---|
| `DemoService.ProvisionAsync :136-137,140-148` | `ExpiresAt` on the user; `Workspace { Id, Name, CreatedAt, CreatedBy }` | **both**: keep `demoUser.ExpiresAt`; add `DemoExpiresAt = DateTime.UtcNow.AddHours(ttlHours)` to the `Workspace` initializer (same instant — compute `var expiresAt` once and use it for both and for the response) |
| `DemoService.UpgradeAsync :352-357` guards | `!user.IsDemo → Forbidden`; `user.ExpiresAt < now → DemoExpired` | load `workspace = Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == membership.OwnerId)` where `membership` = the caller's live **Workspace Admin** membership (`_memberships.ListForIdentityAsync(user.Id)` → the one whose workspace has `DemoExpiresAt != null`; none → `Forbidden(Demo.NotDemoUser)`); `workspace.DemoExpiresAt < now → Failure(Demo.DemoExpired)`. Keep `!user.IsDemo → Forbidden` as well (identity guard) |
| `DemoService.UpgradeAsync :368-372` mutation | clears user flags | **both**: keep the user lines; add `workspace.DemoConvertedAt = now; workspace.DemoExpiresAt = null; workspace.DemoExtendedAt` **kept** (history); `DemoCommentCapOverride = DemoTtlHoursOverride = null`; `workspace.Name = string.IsNullOrWhiteSpace(request.WorkspaceName) ? Workspace.PlaceholderName : request.WorkspaceName.Trim()` (D17.3); `workspace.UpdatedAt = now; UpdatedBy = callerPublicId`; `Workspaces.Update(workspace)` — in the **same** `SaveChangesAsync` as the user (`:390`) |
| `DemoService.UpgradeAsync :416,433,442` (`user.OwnerId`) | legacy read | `workspace.Id` |
| `DemoCleanupService.cs:43-54` | expired users → `u.OwnerId` | `db/uow.Workspaces.IgnoreQueryFilters().AsNoTracking().Where(w => w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt < now).Select(w => w.Id)`; `:20` `FromHours(1)` → **`FromMinutes(15)`** (D17.8); plus the warning pass (§3.5) |
| `CommentService.cs:165-190` cap | `CurrentAdminAsync(owner)?.User.IsDemo/…Override` | `var demoWs = await _unitOfWork.Workspaces.IgnoreQueryFilters().Where(w => w.Id == owner && w.DemoExpiresAt != null).Select(w => new { w.DemoCommentCapOverride }).FirstOrDefaultAsync(); if (demoWs != null) { cap = demoWs.DemoCommentCapOverride ?? setting; … }` (same message) |
| `ExportImportService.cs:592-598` import cap | same shape | same replacement |
| `TenantService.ListAsync :153-157` | from `admin?.User` | `IsDemo = w.DemoExpiresAt != null, ExpiresAt = w.DemoExpiresAt, DemoExtended = w.DemoExtendedAt != null, DemoCommentCapOverride = w.DemoCommentCapOverride, DemoTtlHoursOverride = w.DemoTtlHoursOverride` (DTO names unchanged — no dashboard churn) |
| `TenantService.ExtendDemoAsync(int id) :343-386` | user row | `ExtendDemoAsync(Guid workspaceId)`: workspace live & `DemoExpiresAt != null` else `NotFound("Demo tenant not found.")`; `DemoExtendedAt != null → Failure("This demo has already been extended once.")`; `ttl = w.DemoTtlHoursOverride ?? setting`; `anchor = w.DemoExpiresAt > now ? w.DemoExpiresAt : now`; `w.DemoExpiresAt = anchor + ttl; w.DemoExtendedAt = now`; **dual-write** the admin identity (`CurrentAdminAsync(workspaceId)?.User`: `ExpiresAt`, `DemoExtended = true`); audit unchanged (`TenantDemoExtended`, `expires_at`) |
| `TenantService.SetDemoConfigAsync(int id, …) :388-430` | user row | `SetDemoConfigAsync(Guid workspaceId, …)`: validation unchanged; write the two overrides on the workspace **and** the admin identity; audit unchanged |
| `TenantsController.cs:118-140` | `{id:int}` | `[HttpPost("{workspaceId:guid}/extend")]`, `[HttpPatch("{workspaceId:guid}/demo-config")]` (F9 precedent `:104,145,162`); `ITenantService` signatures follow |
| `AuthService.LoginAsync :726-733` | no expiry check | directly after `:726`: `if (user.IsDemo) { var expiredIds = await _unitOfWork.Workspaces.IgnoreQueryFilters().Where(w => memberships.Select(m => m.OwnerId).Contains(w.Id) && w.DemoExpiresAt != null && w.DemoExpiresAt < DateTime.UtcNow).Select(w => w.Id).ToListAsync(); memberships = memberships.Where(m => !expiredIds.Contains(m.OwnerId)).ToList(); if (memberships.Count == 0) return Result<LoginResponse>.Failure(MessageKeys.Demo.DemoExpired); }` (materialise the id list first — `Contains` over a list translates) |
| `UserMapper.ToMeResponse` | no demo fields | new optional parameter `Workspace? workspace = null` → `DemoExpiresAt = workspace?.DemoExpiresAt`, `DemoCanExtend = workspace?.DemoExpiresAt != null && workspace.DemoExtendedAt == null`; callers that hold the current workspace (login `ok`, `/me`, switch, upgrade) pass it; others pass nothing |

Everything that reads `users.is_demo` as an identity fact (§2) is **unchanged**. `DemoExtended`/`ExpiresAt`/overrides on `users` are written by the
sites above but **read by nobody** after this release → DB-11e drops them (its scope line: "also `users.expires_at` + `IX_users_expires_at`,
`"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"`; **keep** `is_demo` and `recipient_email`").

### 3.4 Convert = `UpgradeAsync`, generalised (b)

Unchanged contract, three additions: (1) `UpgradeDemoRequest.WorkspaceName` (`string?`, ≤ 120, validator `.MaximumLength(120)` + "not whitespace-only
when provided" — same rule as DB-03b's rename); (2) the workspace mutation of §3.3 in the same transaction; (3) `UpgradeDemoResponse` unchanged but
`User` (MeResponse) now carries `DemoExpiresAt = null`, `DemoCanExtend = false`. Kept as today: all data (projects, comments incl. the three seeded
samples — D17.4, memberships, API keys, settings), the `Workspace Admin` membership, `demo_started` + `workspace_converted` facts, audit
`auth.demo.upgraded` (R10) with `After: { ["source"] = "demo" }` added, `EmailVerifiedAt = null` + verification mail (DB-14), identity stamp rotation
(R16), D7 conflict. **"Require verified e-mail + password" (task wording) is satisfied as:** password = `StrongPassword` policy at the request
(`UpgradeDemoValidator.cs:15-16`; DB-14 re-validation `PasswordPolicy.Validate`); verified e-mail = the converted admin is gated from every
non-GET `/api/admin/*` until the link is clicked (`RequireVerifiedEmailFilter`, DB-14) — conversion itself does **not** wait for the click (D17.5).
The `Demo.UpgradeSuccess` message stays.

### 3.5 Expiry, warning, cleanup (c) — `DemoCleanupService` (15-min cadence) + `IDemoService` additions

`IDemoService` gains:
```csharp
/// <summary>DB-17 §3.5. Sends the one T-2h reminder to each live demo whose expiry is within warnWindow and not yet warned; stamps
/// workspaces.demo_expiry_warned_at whether or not the send succeeded (one attempt, D17.6). Returns the number of workspaces stamped.</summary>
Task<int> WarnExpiringAsync(DateTime nowUtc, TimeSpan warnWindow);
/// <summary>DB-17 §3.6. Self-service one-time extension by the demo's Workspace Admin (POST /api/demo/extend).</summary>
Task<Result<DemoStatusResponse>> ExtendAsync(Guid callerPublicId);
/// <summary>DB-17 §3.7. Deletes demo_email_* throttle rows older than 2 days (the raw-address legacy rows included).</summary>
Task<int> SweepThrottleRowsAsync(DateTime nowUtc);
```
`WarnExpiringAsync`: `Workspaces.IgnoreQueryFilters().Where(w => w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt > now && w.DemoExpiresAt <= now + warnWindow && w.DemoExpiryWarnedAt == null).ToListAsync()`;
per workspace: `admin = await _memberships.CurrentAdminAsync(w.Id)`; `to = admin?.User.RecipientEmail`; if `to` non-blank → `_emailService.SendAsync(to, $"Your {productName} demo expires in about two hours", BuildExpiryWarningHtml(...))`
inside `try/catch` (best-effort, `IEmailService` is capped per day); `w.DemoExpiryWarnedAt = now; Workspaces.Update(w); SaveChangesAsync()` per row
(a failure on one workspace must not block the others — same per-item isolation as `DemoCleanupService.cs:71-95`). Body (verbatim, HTML-encode the
workspace name; `{app}` = `brand.Urls.App`): "Your demo workspace, **{name}**, expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC. Everything in it — the
project, its comments and screenshots — is deleted then. To keep it, open {app} and choose **Keep this workspace** (you pick your e-mail and a
password; nothing is lost). Need a little more time? **Extend once** adds {ttl} hours. If you did not start this demo, ignore this e-mail."
Log: `DemoCleanupService: warned {Count} expiring demo(s)`. No audit row (D17.9).

`DemoCleanupService.SweepAsync` becomes three isolated steps, each in its own `try/catch` + scope: (1) `IDemoService.WarnExpiringAsync(now, TimeSpan.FromHours(2))`;
(2) the expiry delete (§3.3 row; `HardDeleteAsync(id, reason: "demo_expired")` unchanged → `tenant.hard_deleted` System row, owner folder + rows gone,
`demo_started`/`workspace_converted`/`usage_daily`/audit rows survive with `owner_id = NULL`); (3) `IDemoService.SweepThrottleRowsAsync(now)`.
Interval **15 min** (`:20`), initial delay 30 s unchanged. Between expiry and the next tick (≤ 15 min) a live token keeps working; **no JWT capping**
(D17.8) — enforcement is: login refused (§3.3), sweep deletes the memberships, stamp validator rejects within 60 s.

### 3.6 Public entry, self-service extension, abuse (d)

- `POST /api/demo` — unchanged route, body, response, rate limit (3/h/IP), per-e-mail 3/day, global active cap (now counted on
  `Workspaces.Where(w => w.DeletedAt == null && w.DemoExpiresAt > now)` instead of `users` — `:91-95`). **Throttle key** becomes
  `$"demo_email_{PseudonymHasher.EmailHash(EmailNormalizer.NormalizeRequired(recipientEmail))}_{DateTime.UtcNow:yyyyMMdd}"` (R14: one normaliser;
  R17-style pseudonym) and `SweepThrottleRowsAsync` hard-deletes `AppSettings.IgnoreQueryFilters().Where(s => s.Key.StartsWith("demo_email_") && s.CreatedAt < now.AddDays(-2))`
  (`RemoveRange`; also removes every legacy raw-address row — they are all older than two days by the time this ships). `recipientEmail.Trim()` (`:71`)
  stays for validation; the `.ToLowerInvariant()` at `:79` disappears (acceptance grep 6). PDPL notice (D17.7): **no API change** — the dashboard form
  shows the notice text (§11) and the privacy page gains a "Demo workspaces" retention row (§5 task 12).
- **`POST /api/demo/extend`** (new; `DemoController`): `[Authorize(Policy = Policies.Admin)]`, `[Audited(AuditActions.DemoExtended)]`,
  `[ProducesResponseType(typeof(DemoStatusResponse), 200)]` + 400 + 403; no body. `DemoService.ExtendAsync(callerPublicId)`: identity live (`FindIdentityByPublicIdAsync`);
  its live Workspace Admin membership whose workspace has `DemoExpiresAt != null` (none → `Forbidden(Demo.NotDemoUser)`); `DemoExpiresAt < now → Failure(Demo.DemoExpired)`;
  `DemoExtendedAt != null → Failure(Demo.AlreadyExtended)`; then the same arithmetic as `TenantService.ExtendDemoAsync` (anchor = later of now/expiry, `+ (override ?? DemoTtlHours)`),
  `DemoExtendedAt = now`, dual-write the identity (`ExpiresAt`, `DemoExtended = true`), `SaveChangesAsync`, audit `demo.extended`
  (`AuditTargets.Workspace`, `OwnerId = workspace.Id`, `After: { ["expires_at"] = …:O }`), return `DemoStatusResponse { ExpiresAt, ExtendedAt, CanExtend = false }`.
  `DemoController` is **not** under `Controllers.Admin`, so `RequireVerifiedEmailFilter` does not apply (a demo identity is exempt anyway). Rate limit:
  none beyond `[Authorize]` (one extension per workspace by construction). One extension **total** — operator and self-service share `demo_extended_at`
  (D17.1); the operator can still lengthen a demo via `demo-config` TTL override *before* the extension is used.
- `AuditActions` gains `public const string DemoExtended = "demo.extended";` under "Auth" with the doc-comment "DB-17: POST /api/demo/extend — the demo
  admin's own one-time extension (the operator's is tenant.demo_extended)". `AuditActions.All` picks it up by reflection.
- `MessageKeys.Demo` gains `AlreadyExtended = "This demo has already been extended once."` and `Extended = "Your demo now expires on {0} UTC."`.
- New DTO `Application/DTOs/Demo/DemoStatusResponse.cs { DateTime ExpiresAt; DateTime? ExtendedAt; bool CanExtend; }`.
- `MeResponse` gains `public DateTime? DemoExpiresAt { get; set; }` (doc: "DB-17: non-null = the current workspace is a live demo; the dashboard's
  countdown reads this, not sessionStorage") and `public bool DemoCanExtend { get; set; }`.

### 3.7 What happens to every existing row

`workspaces`: both production rows get six NULLs; the backfill touches only live demos (expected 0–3). `users`: untouched by the migrations; the
demo columns keep being written for one release. `app_settings`: legacy `demo_email_<raw address>_<date>` rows are hard-deleted by the first
`SweepThrottleRowsAsync` pass (30 s after boot) — they are PII and served only a one-day counter; the DB-11c erase inventory (R14) gains the line
"`app_settings.key` demo throttle — hashed since DB-17; rows deleted after 2 days" (task 13). `comments/projects/...` of demos: unchanged; still
deleted with the workspace at expiry.

### 3.8 What is deliberately not changed

`POST /api/demo` shape (landing/dashboard keep working during the deploy), `UpgradeDemoResponse`, `auth.demo.*`/`tenant.demo_*` strings,
`demo_started`/`workspace_converted` facts (no `demo_extended` fact — a later DB-15 amendment if the funnel needs it), the three seeded comments,
`users.is_demo` semantics, `RecipientEmail` location (identity-adjacent PII, already in the erase inventory), JWT lifetime, the `"demo"` rate policy,
`ActivationStatsService`, `HardDeleteAsync`.

### 3.9 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D17.1 | Extension rules | TTL **24 h** (`demo_ttl_hours` setting, unchanged). **One extension total** per demo (+ TTL hours), self-service by the demo admin *or* granted by the operator — whichever first; `demo_extended_at` records it |
| D17.2 | Where demo state lives | **`workspaces`** (TTL, extension, conversion, overrides); `users.is_demo` stays as the synthetic-identity fact; dual-write one release; DB-11e contracts |
| D17.3 | Workspace name after conversion | `request.WorkspaceName` if given, else **`Workspace.PlaceholderName`** so DB-03b's "name your workspace" prompt appears (never keep "Demo Workspace") |
| D17.4 | Seeded sample comments on conversion | **Keep** (the admin deletes them; deleting silently would surprise anyone who annotated them) |
| D17.5 | Must the new address be verified before the workspace becomes permanent? | **No** — convert immediately, clear the TTL; DB-14's gate blocks admin writes until verified. Alternative on first abuse (report §2 "CAPTCHA (first abuse)"): keep `demo_expires_at = now + 72 h` on convert and clear it in `EmailVerificationService.ConfirmAsync :164` / `AuthService :500` — a later amendment, not this doc |
| D17.6 | Expiry warning e-mail | **Yes**, once, when ≤ 2 h remain, to `users.recipient_email` of the current admin; one attempt, stamped regardless |
| D17.7 | PDPL notice on the demo form | **Notice text + privacy link in the form; no consent checkbox; no API change.** The demo processes one address for one purpose (credentials + reminder) and deletes it with the workspace |
| D17.8 | TTL enforcement between expiry and deletion | **15-min sweep + login refusal; no JWT `exp` capping.** ≤ 15 min of grace on an already-issued token is accepted |
| D17.9 | Audit rows for the reminder / throttle sweep | **No** (notifications and bookkeeping; the delete already writes `tenant.hard_deleted`). `demo.extended` **is** audited (state change by a user) |

## 4. Safety classification

**Expand** (R2 step 1 + 2 in one release, rollback-safe because the old shape stays written): Migration 1 additive (R1); Migration 2 an R3 backfill
with `.Sql(` → marker + `[ContractMigration("DB-17")]` → **contract deploy** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db17 bash scripts/deploy-api.sh`
(R7; stop → labelled dump → boot). Nothing is dropped, narrowed or renamed. Code deletes: (a) expired demo workspaces — as today, same
`HardDeleteAsync`, now 15-min cadence; (b) `app_settings` `demo_email_*` rows older than 2 days (PII cleanup). Tenancy: every new read is by explicit
workspace id or through the caller's own membership; §6 tests 7–8 prove B cannot extend, convert or read A's demo state. **Owner approval line
(R7 marker) needed for Migration 2** — fill in: `// DB-RULES: R3 backfill approved 2026-09-__ by Moamen (owner; F4 "demo = convertible workspace with 24 h TTL", relayed by the orchestrator; docs/db/execution/DB-17-demo-as-product.md)`.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — six properties (§3.1 names/types) + the doc-comment block, after `DeletedBy` (`:24`). `Infrastructure/Mappings/WorkspaceMapping.cs`
   — six `HasColumnName`, the partial index, and the check constraint inside the existing `ToTable` lambda (`:11-14`).
2. `just migrate name="AddWorkspacesDemoColumns"` → read against §3.1 (6 columns + 1 index + 1 constraint on `workspaces`, nothing else; snapshot diff only `Workspace`).
3. `just migrate name="BackfillWorkspacesDemoState"` → empty scaffold; paste §3.2 `Up`/`Down` SQL; add the marker comment line and `[ContractMigration("DB-17")]`
   (`using Pointer.Infrastructure.Migrations;` — the attribute is `Infrastructure/Migrations/ContractMigrationAttribute.cs`). `Tests/MigrationSafetyTests.cs` must pass (marker + attribute agree).
4. `Application/Services/Implementation/DemoService.cs` — §3.3 rows (Provision dual-write; active cap on workspaces; throttle key §3.6; Upgrade guards,
   mutation, `workspace.Id` for `user.OwnerId` at `:416,433,442`), `ExtendAsync`, `WarnExpiringAsync` (+ `BuildExpiryWarningHtml`, copy `:470-501`'s shape),
   `SweepThrottleRowsAsync`. `IDemoService.cs` — the three members (§3.5 verbatim). Constructor unchanged (all dependencies already injected).
5. `Application/DTOs/Demo/UpgradeDemoRequest.cs` — `public string? WorkspaceName { get; set; }`; `Application/Validators/UpgradeDemoValidator.cs` — rule (§3.4).
   `Application/DTOs/Demo/DemoStatusResponse.cs` (new). `Application/DTOs/Auth/MeResponse.cs` — two properties (§3.6). `Application/Common/UserMapper.cs:17` —
   `Workspace? workspace = null` parameter + two assignments; update callers in `AuthService` (login ok / me / switch — pass the current workspace, loaded
   `IgnoreQueryFilters` by the tenant id already in hand) and `DemoService.UpgradeAsync :452`.
6. `Application/Common/AuditActions.cs` — `DemoExtended` (§3.6). `Application/Resources/MessageKeys.cs:367-375` — `AlreadyExtended`, `Extended`.
7. `API/Controllers/DemoController.cs` — add after `Upgrade`:
   ```csharp
   /// <summary>DB-17: the demo admin's one-time extension (+TTL hours). Operator extensions live under /api/admin/tenants.</summary>
   [Authorize(Policy = Policies.Admin)]
   [Audited(AuditActions.DemoExtended)]
   [HttpPost("extend")]
   [ProducesResponseType(typeof(DemoStatusResponse), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   public async Task<IActionResult> Extend()
   {
       var result = await demoService.ExtendAsync(User.GetId());
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
8. `Application/Services/Implementation/TenantService.cs` — `ListAsync :153-157` mapping; `ExtendDemoAsync(Guid)`, `SetDemoConfigAsync(Guid, …)` (§3.3, dual-write);
   `ITenantService.cs` signatures. `API/Controllers/Admin/TenantsController.cs:118-140` — routes `{workspaceId:guid}`; pass `workspaceId`.
9. `API/Hosted/DemoCleanupService.cs` — §3.5 (three isolated steps; `PeriodicTimer(TimeSpan.FromMinutes(15))`; workspace query; keep the per-item scopes and log lines).
10. `Application/Services/Implementation/CommentService.cs:165-190` and `ExportImportService.cs:592-598` — workspace-based cap (§3.3). `AuthService.cs:726` — the expiry filter (§3.3).
11. Tests (§6); `just fmt`; `just test`.
12. Docs: `landing/privacy.html:253-260` add `<tr><td>Demo workspaces (not converted) and the e-mail used to request them</td><td>24 hours after the demo starts (one 24-hour extension possible); deleted with the workspace</td></tr>`;
    `docs/db/SCHEMA.md` `workspaces` row (six columns, index, constraint) and `users` row ("demo TTL/extension/overrides moved to `workspaces` by DB-17; columns dual-written until DB-11e");
    `DEPLOY.md` § Backups: one sentence "Expired demo workspaces are hard-deleted every 15 min (`DemoCleanupService`), two hours after a reminder e-mail."
13. `docs/db/execution/DB-11c-deletion-semantics-remove-disable-erase.md` §3.4 erase inventory — add the row from §3.7 (`app_settings.key` demo throttle: hashed, 2-day life).
    `docs/db/DB-RULES.md` R14 — no new rule; the existing "one normaliser" sentence already forbids `:79`.

## 6. Tests

InMemory harness `Tests/DemoUpgradeTests.cs:76-100` (extend `Build` to return the `SpyEmailService` of `DemoSessionEmailTests.cs:153-162` too) unless noted:
1. `Provision_SetsWorkspaceDemoExpiresAt_AndUserExpiresAt_SameInstant` (dual-write; `DemoConvertedAt == null`, `DemoExtendedAt == null`).
2. `Provision_ThrottleKey_IsHashed_NeverContainsAddress` — after provision, `db.AppSettings.IgnoreQueryFilters().Any(s => s.Key.Contains("@"))` is false and one key matches `^demo_email_[0-9a-f]{16}_\d{8}$`;
   `Provision_PerEmailLimit_StillCountsAcrossCaseVariants` (`Foo@X.com` then `foo@x.com` share the counter).
3. `Upgrade_ClearsWorkspaceTtl_SetsConverted_RenamesToPlaceholder_KeepsData` (comments count unchanged, membership live, API key untouched, `Name == Workspace.PlaceholderName`,
   `DemoExpiresAt == null`, `DemoConvertedAt != null`, `MeResponse.DemoExpiresAt == null`); `Upgrade_WithWorkspaceName_SetsIt`; `Upgrade_WorkspaceNameTooLong_Fails`;
   `Upgrade_ExpiredWorkspace_Fails_DemoExpired` (workspace `DemoExpiresAt` in the past while `users.expires_at` is null → still refused: the workspace is the authority);
   existing `DemoUpgradeTests` facts keep passing (D7 conflict, stamp rotation, `EmailVerifiedAt = null`, `InvalidateGate`, `workspace_converted` once).
4. `Extend_Once_MovesExpiry_StampsExtendedAt_AuditsDemoExtended` (`RecordingAuditWriter` from `Tests/AuditWrittenByServicesTests.cs`; `After["expires_at"]` present);
   `Extend_Twice_Fails_AlreadyExtended`; `Extend_AfterExpiry_Fails`; `Extend_NonDemoWorkspace_Forbidden`; `Extend_UsesTtlOverride_WhenSet`;
   `OperatorExtend_ThenSelfExtend_Fails` (shared `demo_extended_at`).
5. `WarnExpiring_SendsOnce_ToRecipientEmail_StampsEvenWhenSendFails` (spy e-mail; second call sends nothing); `WarnExpiring_OutsideWindow_Nothing`; `WarnExpiring_NoRecipientEmail_StampsWithoutSending`.
6. `SweepThrottleRows_DeletesOldHashedAndLegacyRawRows_KeepsToday` (seed `demo_email_<hash>_<today>`, `demo_email_<hash>_<3 days ago>`, `demo_email_someone@x.com_<3 days ago>` → only today's remains).
7. **R8** `Extend_TenantB_Admin_CannotExtendA` (B's admin calls `ExtendAsync` → Forbidden; A's `DemoExpiresAt` unchanged); `Upgrade_TenantB_Identity_CannotConvertA`
   (identity with a membership only in B, `IsDemo = true` set by hand, A is the demo → Forbidden). `TenantQueryFilter`-style: `Workspace` filter unchanged (`Tests/TenantQueryFilterTests.cs` shape) — B's context sees no `DemoExpiresAt` of A.
8. `Login_ExpiredDemo_Refused_DemoExpired` (`AuthService` fixture from `Tests/WorkspaceSwitchTests.cs`; demo identity, workspace `DemoExpiresAt` past → `Failure(Demo.DemoExpired)`);
   `Login_LiveDemo_Ok_MeCarriesDemoExpiresAt_AndCanExtend`; `Login_ConvertedWorkspace_Ok_NoDemoFields`.
9. Sqlite `TestDb` (`RetentionServiceTests.cs:44-70`): `DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers` — seed a workspace with `DemoExpiresAt` past and an admin whose `users.expires_at` is **null** → deleted
   (proves the read-switch); `DemoCleanup_LeavesLiveDemo`; `DemoCleanup_WritesTenantHardDeleted_System` (extend `AuditWrittenByServicesTests.cs:873-900`); drive the static/internal
   sweep method (make `DemoCleanupService.SweepOnceAsync(IUnitOfWork, ITenantService, IDemoService, ILogger, ct)` `internal static`, ImpersonationSweepService precedent).
10. `Tests/AuditCoverageTests.cs` passes with the new action (`[Audited]` present); `Tests/MigrationSafetyTests.cs` passes (marker + attribute on Migration 2 only).
11. `Tests/CommentFieldsTests.cs`/`CommentServiceQuickAccessTests.cs` fixture: `CommentCap_UsesWorkspaceOverride_NotUser` (workspace `DemoCommentCapOverride = 1`, user's is null → second comment refused);
    `ImportCap_UsesWorkspaceOverride` (ExportImport).
12. Existing data survives: rehearsal (§7 criterion 8) — the backfill copies every live demo (count equals the §3.2 pre-check), real workspaces keep six NULLs.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddWorkspacesDemoColumns`, `_BackfillWorkspacesDemoState` (in that order);
   `grep -c "AddColumn" Infrastructure/Migrations/*_AddWorkspacesDemoColumns.cs` → 6; `grep -c "ContractMigration(\"DB-17\")" Infrastructure/Migrations/*_BackfillWorkspacesDemoState.cs` → 1; `grep -c "R3 backfill approved"` on it → 1; `grep -c ContractMigration Infrastructure/Migrations/*_AddWorkspacesDemoColumns.cs` → 0.
2. `grep -c "u.OwnerId\|user.OwnerId" API/Hosted/DemoCleanupService.cs Application/Services/Implementation/DemoService.cs` → 0 each (no legacy `users.owner_id` reads on the demo path).
3. `grep -c "DemoExpiresAt" Application/Services/Implementation/CommentService.cs Application/Services/Implementation/ExportImportService.cs Application/Services/Implementation/TenantService.cs API/Hosted/DemoCleanupService.cs Application/Services/Implementation/AuthService.cs` → ≥ 1 each;
   `grep -c "User.IsDemo\|User.DemoCommentCapOverride" Application/Services/Implementation/CommentService.cs Application/Services/Implementation/ExportImportService.cs Application/Services/Implementation/TenantService.cs` → 0 each.
4. `grep -c "{id:int}/extend\|{id:int}/demo-config" API/Controllers/Admin/TenantsController.cs` → 0; `grep -c "{workspaceId:guid}/extend\|{workspaceId:guid}/demo-config"` → 2.
5. `curl -s …/swagger.json | jq '.paths["/api/demo/extend"].post.tags'` → `["Demo"]`; `jq '.components.schemas.MeResponse.properties | has("demoExpiresAt") and has("demoCanExtend")'` → `true`.
6. **R14/PII:** `grep -c "ToLowerInvariant" Application/Services/Implementation/DemoService.cs` → 0; `grep -c "PseudonymHasher.EmailHash" Application/Services/Implementation/DemoService.cs` → 1;
   after one sweep on the rehearsal DB: `SELECT count(*) FROM app_settings WHERE key LIKE 'demo_email_%' AND key LIKE '%@%';` → 0.
7. `grep -c "FromMinutes(15)" API/Hosted/DemoCleanupService.cs` → 1; `grep -c "WarnExpiringAsync\|SweepThrottleRowsAsync" API/Hosted/DemoCleanupService.cs` → 1 each.
8. Rehearsal (R11) on a same-day dump: `dotnet ef migrations script --idempotent` shows both migrations; after `database update`:
   `SELECT id, name, demo_expires_at, demo_extended_at, demo_converted_at FROM workspaces;` → real workspaces all NULL, every live demo from the pre-check populated;
   `SELECT conname FROM pg_constraint WHERE conname = 'ck_workspaces_demo_ttl_or_converted';` → 1 row; `\d workspaces` shows `ix_workspaces_demo_expires_at … WHERE (demo_expires_at IS NOT NULL)`.
   Then on the rehearsal API: `POST /api/demo` → `MeResponse.demoExpiresAt` set; `POST /api/demo/extend` → 200 once, 400 twice; `POST /api/demo/upgrade { email, password, workspaceName }` → 200,
   `SELECT demo_expires_at, demo_converted_at, name FROM workspaces WHERE id = …` → `NULL, <ts>, <name>`; set `demo_expires_at = now() - interval '1 minute'` on a second demo → within 15 min
   `tenant.hard_deleted` row with `after->>'reason' = 'demo_expired'` and the workspace gone; login with that demo's credentials → `DemoExpired` message before the sweep.
9. `just test` green with the ~30 new facts; DB-10 green (apply from empty + newest `Down()`/`Up()` round-trip — Migration 2's `Down()` is a real statement).

## 8. Rollback

Migration 2 `Down()` nulls the six columns on unconverted workspaces (the `users` columns still hold the source thanks to dual-write); Migration 1
`Down()` drops constraint, index, columns. Code rollback = `git checkout` the commit before this doc + `up -d --build api`: the previous code reads
`users.expires_at/…`, which this release kept writing, so **demos provisioned or extended while DB-17 was live keep their TTL under the old code**.
Conversions performed while live are real conversions (identity has a real e-mail/password; `users.is_demo = false`) — not reversible by rollback,
and that is the feature. **Hard-deleted expired demos are not restorable** except from the `pre-db17` dump + uploads tarball (they are demos; nothing
of value). The labelled dump is taken by the contract deploy itself (R7).

## 9. Release steps

1. **Prod pre-checks (read-only, pasted into the PR):** the two §3.2 queries (second must be 0) and `SELECT count(*) FROM app_settings WHERE key LIKE 'demo_email_%';` (the number of PII rows the first sweep will delete).
2. R11 rehearsal on a same-day dump (§7 criterion 8), including the two hosted-job checks.
3. **Contract deploy:** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db17 bash scripts/deploy-api.sh` (stop → dump `pre-db17` → boot; the gate refuses a plain deploy because of `[ContractMigration("DB-17")]`). R7.1 batching: not needed (one doc; DB-16 is an ordinary deploy and may ride along as code, but ship it separately for attribution).
4. Verify: `docker compose logs --since 5m api | grep -iE "migrat|DemoCleanup|error"` — two `Applying migration` lines, then `DemoCleanupService: … throttle rows deleted N` (N = step 1's count); `SELECT demo_expires_at FROM workspaces WHERE demo_expires_at IS NOT NULL;` matches the pre-check;
   `GET /api/auth/me` as the production admin → `demoExpiresAt: null`, `demoCanExtend: false`; start a demo from `demo.pointer.moamen.work` → banner shows the server countdown; extend once; convert; confirm `tenant.hard_deleted … demo_expired` appears for a demo left to expire (≤ 15 min after its TTL).
5. Watch for: `DemoCleanupService: … failed` (must not appear), `Conflict`/`Forbidden` bursts on `POST /api/demo/extend` (dashboard offering the button after use → §11 task 2), `demo_email_` rows growing without deletion.
6. Dashboard: `dashboard-agent` regenerates the client from production once (adds `postApiDemoExtend`, `DemoStatusResponse`, `MeResponse.demoExpiresAt/demoCanExtend`, `UpgradeDemoRequest.workspaceName`, the `{workspaceId}` tenant routes), then §11.
7. Open **DB-11e** scope amendment (one line in `DB-REVIEW §7`): drop `users.expires_at` + `IX_users_expires_at`, `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"` after one release with `grep -rn "\.ExpiresAt\|DemoExtended\|DemoCommentCapOverride\|DemoTtlHoursOverride" Application API --include='*.cs' | grep -v "DemoExpiresAt\|workspace\." ` → only the dual-write lines.

## 10. Out of scope

Dropping the `users` demo columns (**DB-11e**); a `demo_extended` funnel fact (DB-15 amendment); CAPTCHA and any consent checkbox (report §2 DEFER);
capping JWT `exp` at the demo TTL (D17.8); verification-before-permanence (D17.5 alternative); moving `users.recipient_email`; changing
`POST /api/demo`'s request/response shape; a demo "seeded app" (roadmap §47); operator MFA (R5-61); the widget (it only needs to keep handling 401 after
expiry — it does); the CLI; `clients/`; DB-16's file purge (independent doc).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (after client regen):
1. `components/DemoPanel.tsx`: source of truth for the countdown becomes `me.demoExpiresAt` (from the auth context's `/me`), falling back to the
   `sessionStorage` session only for the credentials block; banner renders whenever `me.demoExpiresAt` is set (survives reloads/other tabs); red state
   when < 2 h remain (mirrors the e-mail); add **"Extend once (+24 h)"** button → `postApiDemoExtend()`; hide it when `!me.demoCanExtend`; on 400 show the
   server message; after success refresh `/me` and toast `envelope.message`.
2. Upgrade dialog (`DemoPanel.tsx:24-49,133`): add an optional **Workspace name** field (≤ 120) → `workspaceName`; after success the banner disappears
   (`demoExpiresAt` null) and the DB-03b "name your workspace" prompt shows when the field was left empty; keep the DB-14 verification banner behaviour.
3. `features/login/LoginPage.tsx` demo form: under the e-mail field add the PDPL notice (en + ar i18n): "We use this address only to send your demo login
   and one reminder before the demo expires. The demo workspace, and this address with it, are deleted 24 hours after it starts unless you keep it. See our
   Privacy Policy." (link to `/privacy.html` on the landing domain).
4. `features/tenants/TenantsPage.tsx:536-547`: `extendMut.mutate({ workspaceId: tenant.workspaceId! })`, demo-config likewise; disable "Extend" when
   `demoExtended` (unchanged field). Show `expiresAt` from the same DTO (unchanged).
5. i18n keys en + ar for the new strings.

**Widget:** none. **CLI:** none.
**Docs/landing (same PR, tasks 12–13):** privacy retention row; SCHEMA.md; DEPLOY.md sentence; DB-11c erase inventory line.
