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
keep being written and are dropped by **DB-11e** after one release with zero readers). **Status: written 2026-09-23, not implemented; cross-reviewed 2026-09-23 (Gemini Pro IMPLEMENT WITH AMENDMENTS — `docs/db/reviews/REVIEW-AGY-DB16-17-2026-09-23.md`; Opus 1 BLOCKER + 1 HIGH + 3 MEDIUM + LOW/nits), amendments folded the same day (§12).**
Owner decisions D17.1–D17.9 have defaults (§3.9); **D17.5 was confirmed by the owner on 2026-09-23 18:05 Riyadh** ("ok" to convert-immediately; §3.9, §9 step 0 done).

**Dependencies.** DB-11a (memberships, workspace id ≠ admin public_id), DB-12 (`IAuditWriter`), DB-14 (verification rail; `UpgradeAsync` already
resets `EmailVerifiedAt`), DB-15 (`demo_started`/`workspace_converted` emission sites) — all in production. Independent of DB-16 (either order);
the expiry path reuses `TenantService.HardDeleteAsync`, whose screenshot folder delete DB-16 verified and whose leftovers DB-16's orphan sweep catches.

## 1. Goal

A visitor clicks "Try the demo" (`landing/index.html:375,387,644` → `demo.pointer.moamen.work`, the dashboard build's demo entry,
`LoginPage.tsx:198-240`), gets a seeded workspace with a synthetic login, and has 24 hours to reach a first applied comment (F4's funnel). Today that
mostly exists (`DemoService.ProvisionAsync`/`UpgradeAsync`, `DemoCleanupService`), but the demo is modelled as **flags on the admin's `users` row**
(`users.is_demo/expires_at/DemoExtended/…`, `User.cs:50-64`) — not on the workspace — so: the cleanup job reads the legacy `users.owner_id`
(`DemoCleanupService.cs:42-54`, its own comment at `:42` acknowledges the DB-11a move), a column DB-11a declared "written once at creation, never
read after DB-11a" (`TenantService.cs:576-579` comment; R8.7); an expired demo can still log in, switch into the workspace, and keep a 12 h JWT
(`AuthService.LoginAsync :673` and `SwitchWorkspaceAsync :992` have no expiry check; `JwtTokenService.cs:110`); the only extension is a super-admin action keyed by `users.id`
(`TenantsController.cs:118-121`); the requester's real e-mail is written **raw** into `app_settings.key` as a throttle counter that nothing ever
deletes (`DemoService.cs:78-79,231-240`; `SCHEMA.md` `app_settings`: "no delete path today"); and the dashboard's countdown lives in
`sessionStorage` from the provision response, so it is gone after a reload in another tab (`DemoPanel.tsx:43-60`). This doc puts the TTL on the
workspace, gives the demo admin one self-service extension and a real "keep this workspace" conversion that preserves everything, makes expiry
enforceable (login refused, sweep every 15 min, warning e-mail two hours before), and closes the PII leak. User-visible: countdown that survives
reloads, "Extend once" and "Keep this workspace" in the banner, a reminder e-mail, and a converted workspace that is simply the same workspace with
a real admin.

## 2. Prerequisites (verified facts, 2026-09-23 @ `78823ce`, re-verified @ `273f31e` after R5-61 merged — 76 migrations, newest `20260923102053_AddOperatorMfa`; `AuthService`, `AuditActions`, `MessageKeys`, `MeResponse`, `AppDbContext` line numbers below are the `273f31e` ones)

**Where demo state lives today (all on the demo admin's identity row)**
- `Domain/Entity/User.cs:50-64`: `OwnerId Guid?` (legacy — "written once at creation, never read after DB-11a", `TenantService.cs:576-579`; the `:12-15` doc-comment is `RoleId`'s), `IsDemo`, `ExpiresAt`, `DemoExtended` (one-time
  extension used), `DemoCommentCapOverride`, `DemoTtlHoursOverride`, `RecipientEmail` (the real human address; cleared on upgrade). Physical names:
  `is_demo`, `expires_at` + index `IX_users_expires_at` (`UserMapping.cs:57-59`; `20260629155415_AddDemoColumns`), and **PascalCase**
  `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"` (no `HasColumnName`; `20260701121004_AddDemoTenantConfig.cs:13-30`,
  snapshot `AppDbContextModelSnapshot.cs` ~`:2151-2160`) — quote them in SQL.
- `Domain/Entity/Workspace.cs:8-25` (`Id, Name, CreatedAt/By, UpdatedAt/By, DeletedAt/By`; not a `BaseEntity`), `Infrastructure/Mappings/WorkspaceMapping.cs:11-29`
  (`ck_workspaces_name_not_blank`, `name varchar(120)`), filter `AppDbContext.cs:328-332` (strict-own on `Id`), `IUnitOfWork.Workspaces` (`IUnitOfWork.cs:12`).
  Production: 2 rows (`SCHEMA.md`; §9 step 1 gives the live number).
- `TenantService.HardDeleteAsync` trusts its caller: no demo check anywhere in `:511-627`; the owner **folder is deleted before the transaction** (`:549`), so a
  wrong call destroys screenshots even if the row delete later fails (Gemini Pro DB-17 #2). `IUnitOfWork.ExecuteSqlRawAsync` (`IUnitOfWork.cs:60`) is the
  `SELECT … FOR UPDATE` precedent (no-op on InMemory). `DemoService.ProvisionAsync :132` sets `OwnerId = workspaceId` on the demo identity — the pre-DB-17
  cleanup (`DemoCleanupService.cs:52-53`) and a rollback of this doc depend on it (Opus DB-17 #5).
- Readers of the **identity** fact `users.is_demo` (stay as they are — a synthetic `demo-<slug>@demo.pointer` login has no real address):
  `EmailVerificationService.cs:60-64` (`IsExempt`), `:123` (resend refused), `API/Auth/RequireVerifiedEmailFilter.cs:92-94` (gate exempt),
  `Application/Common/UserMapper.cs:23` (`EmailVerified`), `AuthService.cs:176` (password reset excludes demos).
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
- `MessageKeys.Demo` (`MessageKeys.cs:413-421`: `NotDemoUser`, `AlreadyUpgraded`, `DemoExpired :418`, `EmailTaken`, `UpgradeSuccess :420`).
- `DemoService.UpgradeAsync` (`:327-456`): validator `UpgradeDemoValidator` (`Application/Validators/UpgradeDemoValidator.cs:11-16`, e-mail +
  `StrongPassword`), `EmailNormalizer.NormalizeRequired` `:337`, conflict on existing identity `:362-364` (D7), in-place mutation `:368-386`
  (`EmailVerifiedAt = null` `:376` — DB-14 §3.2 "this is exactly where the address becomes real (F4)"; stamp rotation `:384`), 23505 → Conflict `:388-395`,
  `InvalidateGate` `:401`, `SendAsync` verification `:404`, `workspace_converted` `:409-429`, token `:433-436`, audit `auth.demo.upgraded` `:438-445`,
  response `UpgradeDemoResponse { Token, User = MeResponse }` `:448-455`. `IDemoService.cs:12,19`. DTOs `Application/DTOs/Demo/*.cs`
  (`DemoRequest { Email }`, `DemoSessionResponse { Token, Email, Password, ProjectKey, ExpiresAt, ServerUrl, EmailSent }`, `UpgradeDemoRequest { Email, Password, DisplayName? }`).
- `API/Controllers/DemoController.cs`: `[Route("api/demo")]`, `[Tags("Demo")]` (in `orval.config.ts:6`), `Create` `[AllowAnonymous] [Audited(AuthDemoProvisioned)] [EnableRateLimiting("demo")]`
  (`:18-38`; 429 mapping by message text `:32-35`), `Upgrade` `[Authorize] [Audited(AuthDemoUpgraded)]` (`:40-55`, `User.GetId()`).
  Rate policy `"demo"` = 3 per hour per IP (`API/Extensions/RateLimitingExtensions.cs:78-86`).
- Login/session: `AuthService.LoginAsync` (`:673`) — identity `:691`, password `:696`, memberships `var memberships = await _memberships.ListForIdentityAsync(user.Id);` `:773`,
  `Status = "no-workspace"` `:780`. `SwitchWorkspaceAsync(Guid workspaceId)` (`:992-1031`): membership `:1012`, `workspaceLive` check `:1023-1027`
  (`w.Id == workspaceId && w.DeletedAt == null`), `Issue` `:1030` — **mints a fresh 12 h token with no demo-expiry check** (Opus DB-17 #2). Quick-access
  login (`:1541-1584`) resolves `link.OwnerId` and can target a demo workspace (a demo admin may create quick-access invites), as can key login.
  **No demo-expiry check anywhere** (`grep -n "DemoExpired" AuthService.cs` → 0). `JwtTokenService.Issue` expiry `now + LifetimeHours (12)` (`:110`).
  Per-request revocation = stamp validator (`AuthenticationExtensions.cs:100-`, cached ~60 s) — a membership removed by `HardDeleteAsync`
  (`TenantService.cs:580`) kills the token within 60 s. `TenantStamp.TryRequireOwner(ICurrentUser u, out Guid owner)` (`Application/Common/TenantStamp.cs:16`)
  is the R16 way to obtain the caller's workspace; `ClaimsPrincipalExtensions.GetId` (`API/Extensions/ClaimsPrincipalExtensions.cs:25`) has no tenant twin.
- `MeResponse` (`Application/DTOs/Auth/MeResponse.cs:3-40`) has no demo fields; `UserMapper.ToMeResponse(user, role, tenantName)` (`UserMapper.cs:17-43`).
- Audit: `AuditActions.cs:51-52` (`auth.demo.provisioned`, `auth.demo.upgraded`), `:89` (`workspace.created`), `:92-93` (`tenant.demo_extended`,
  `tenant.demo_config_changed`), `:95` (`tenant.hard_deleted`); `AuditFields.Allowed` (`AuditFields.cs:13-43`) includes `reason, count, expires_at, source, minutes, kind`.
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
- Tests to copy: `Tests/DemoUpgradeTests.cs:76-101` (`Build(dbName)` harness — InMemory, real `UnitOfWork`/`MembershipService`, `RecordingTokenService`,
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
**No check constraint** (amended, Opus DB-17 #4/#7): the first draft had `demo_expires_at IS NULL OR demo_converted_at IS NULL`, but the pre-wired
`Demo:ConvertRequiresVerification` mode (§3.4) legitimately holds both (converted, TTL still running until the address is verified). The invariants
that hold instead, enforced by code and §6 tests, are: (i) `demo_converted_at` is write-once; (ii) with the flag **off** a converted workspace has
`demo_expires_at IS NULL`; (iii) with the flag **on** a converted workspace's TTL is cleared by the first verification of its admin's address;
(iv) `demo_extended_at`/`demo_expiry_warned_at`/the overrides are meaningful only while `demo_expires_at IS NOT NULL` — they are **not** nulled on
conversion (history). What the schema does **not** constrain and the reader should not assume: that a row with `demo_expires_at` has a live admin
membership (the sweep tolerates admin-less demos — `HardDeleteAsync` handles them), or that `demo_expires_at` is in the future.

Doc-comment block on `Workspace` (verbatim, above `DemoExpiresAt`):
```csharp
/// <summary>
/// DB-17 (F4). Demo state lives on the WORKSPACE: non-null DemoExpiresAt = a live demo that the sweep hard-deletes at expiry;
/// DemoConvertedAt = it was kept ("convert to your workspace"); with Demo:ConvertRequiresVerification off the TTL is cleared at the same time, with it on the TTL runs until the new address is verified (no check constraint — DB-17 §3.1 lists the code-enforced invariants).
/// The admin identity's users.is_demo remains the separate fact "synthetic demo login without a real address" (exempt from e-mail
/// verification, excluded from password reset). users.expires_at / "DemoExtended" / "DemoCommentCapOverride" / "DemoTtlHoursOverride"
/// are dual-written until DB-11e drops them (DB-RULES R2).
/// </summary>
```

**Migration 1 `AddWorkspacesDemoColumns`** (scaffolded): six `AddColumn` on `workspaces`, one `CreateIndex` (with filter).
Nothing else (an operation on any other table → stop and report). No marker. `Down()` = generated (drop index, columns).
**Every existing row:** untouched (`NULL` everywhere; both production workspaces are real, not demos).

### 3.2 Migration 2 `BackfillWorkspacesDemoState` — R3 backfill, marked, `[ContractMigration("DB-17")]`

Copies the TTL/extension/overrides of every **live** demo from its admin identity to **the workspace that was provisioned for that identity** — and
no other. **Why the first draft was wrong (Gemini Pro DB-17 #1 / Opus DB-17 #1, BLOCKER):** after DB-11a an identity can hold several memberships; a
demo identity invited into a real workspace would have matched a join on memberships alone, and that real workspace would have been tagged with a TTL
and hard-deleted at expiry. The statement therefore keys on the identity's **own** workspace (`u.owner_id = w.id` — `DemoService.ProvisionAsync :132`
writes it at creation; **R8.7 is suspended for this one statement**, exactly as `TenantService.HardDeleteAsync :576-590` reads the same column for the
same one-shot "created here" question), requires the membership to be a live **Workspace Admin** one, and refuses any workspace that has **another**
live member (a demo workspace has exactly one). Hand-written `migrationBuilder.Sql(...)` in a migration with **nothing else** in it (scaffold with no model
change: `just migrate name="BackfillWorkspacesDemoState"` produces empty `Up`/`Down`; fill them). Guarded so a second run is a no-op:

```sql
UPDATE workspaces w
SET demo_expires_at           = u.expires_at,
    demo_extended_at          = CASE WHEN u."DemoExtended" THEN COALESCE(u.updated_at, u.created_at) END,
    demo_comment_cap_override = u."DemoCommentCapOverride",
    demo_ttl_hours_override   = u."DemoTtlHoursOverride"
FROM users u
JOIN workspace_memberships m ON m.user_id = u.id AND m.owner_id = w.id AND m.left_at IS NULL AND m.deleted_at IS NULL
JOIN roles r ON r.id = m.role_id AND r.name = 'Workspace Admin'
WHERE u.is_demo = true
  AND u.owner_id = w.id                                   -- the identity's OWN workspace (DemoService.ProvisionAsync :132), never a workspace it was invited into
  AND u.deleted_at IS NULL
  AND u.expires_at IS NOT NULL
  AND w.deleted_at IS NULL
  AND w.demo_expires_at IS NULL
  AND w.demo_converted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM workspace_memberships o
                  WHERE o.owner_id = w.id AND o.user_id <> u.id AND o.left_at IS NULL AND o.deleted_at IS NULL);  -- a demo has exactly one member
```
`Down()`: `UPDATE workspaces SET demo_expires_at = NULL, demo_extended_at = NULL, demo_comment_cap_override = NULL, demo_ttl_hours_override = NULL WHERE demo_converted_at IS NULL;`
(reversible: the `users` columns still hold the source — that is what dual-write buys). Marker line above `Up()` (copy `20260923062150:8-9`'s shape):
`// DB-RULES: R3 backfill approved 2026-09-23 by Moamen (owner; F4 + D17.5 confirmed 2026-09-23, relayed by the orchestrator; docs/db/execution/DB-17-demo-as-product.md)`
+ `[ContractMigration("DB-17")]` on the class. Row count expected in production: **0–3** (demos live at deploy time; `DemoMaxActive` caps at 100). Not batched (R3).

**Pre-checks (prod, read-only, pasted into the PR — §9 step 1). Q1** what will be tagged (must list only `Demo Workspace`-named rows):
```sql
SELECT w.id, w.name, u.email, u.expires_at
FROM workspaces w JOIN users u ON u.owner_id = w.id
JOIN workspace_memberships m ON m.user_id = u.id AND m.owner_id = w.id AND m.left_at IS NULL AND m.deleted_at IS NULL
JOIN roles r ON r.id = m.role_id AND r.name = 'Workspace Admin'
WHERE u.is_demo AND u.deleted_at IS NULL AND u.expires_at IS NOT NULL AND w.deleted_at IS NULL
  AND NOT EXISTS (SELECT 1 FROM workspace_memberships o WHERE o.owner_id = w.id AND o.user_id <> u.id AND o.left_at IS NULL AND o.deleted_at IS NULL);
```
**Q2** demo identities the statement will **skip** (must be explained or hand-deleted first — such a demo would otherwise never expire):
```sql
SELECT u.id, u.email, u.owner_id, u.expires_at FROM users u
WHERE u.is_demo AND u.deleted_at IS NULL AND u.expires_at IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = u.owner_id AND w.deleted_at IS NULL);
```
**Q3** demo identities with a membership **outside** their own workspace (the over-reach case; expected 0 — if > 0, Q1 must still not list those workspaces):
```sql
SELECT u.id, u.email, m.owner_id FROM users u JOIN workspace_memberships m ON m.user_id = u.id AND m.left_at IS NULL AND m.deleted_at IS NULL
WHERE u.is_demo AND u.deleted_at IS NULL AND m.owner_id <> u.owner_id;
```
**Rehearsal test for the over-reach (the migration runs only on Postgres, so this is an R11 step, not a unit test — `Backfill_DemoIdentityWithMembershipInRealWorkspace_DoesNotTagIt`):**
on the rehearsal DB *before* `database update`, insert a live demo identity (`is_demo = true, expires_at = now() + interval '1 hour', owner_id = <its own new workspace id>`)
with a Workspace Admin membership in its own new `Demo Workspace` row **and** a `Developer` membership in the real production workspace; run the migrations;
assert `SELECT demo_expires_at FROM workspaces WHERE id = <real workspace>` → NULL and the demo row → populated. Paste the two results into the PR.

### 3.3 Read-switch and dual-write (R2 steps 1–2 in one release; rollback-safe because both shapes stay written)

| Site | Today | After |
|---|---|---|
| `DemoService.ProvisionAsync :132,136-137,140-148` | `ExpiresAt` on the user; `OwnerId = workspaceId` on the user (`:132`); `Workspace { Id, Name, CreatedAt, CreatedBy }` | **both**: keep `demoUser.ExpiresAt` **and keep `OwnerId = workspaceId`** (the pre-DB-17 cleanup and a rollback read it; the backfill keys on it — Opus DB-17 #5; acceptance crit. 2a); add `DemoExpiresAt = expiresAt` to the `Workspace` initializer (compute `var expiresAt = DateTime.UtcNow.AddHours(ttlHours)` once and use it for the user, the workspace and the response) |
| `DemoService.UpgradeAsync :352-357` guards | `!user.IsDemo → Forbidden`; `user.ExpiresAt < now → DemoExpired` | signature becomes `UpgradeAsync(Guid callerPublicId, Guid workspaceId, UpgradeDemoRequest request)` — the controller passes the **session's** workspace (`TenantStamp.TryRequireOwner(currentUser, out var ws)` else `Forbidden`; R16), never "the one demo the identity belongs to" (Gemini Pro DB-17 #3: an identity may sit in two demos). Inside: `membership = await _memberships.GetMembershipAsync(user.Id, workspaceId)` must be live and `Role.Name == "Workspace Admin"` else `Forbidden(Demo.NotDemoUser)`; `workspace = Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null)`; `workspace?.DemoExpiresAt == null → Forbidden(Demo.NotDemoUser)`; `workspace.DemoExpiresAt < now → Failure(Demo.DemoExpired)`. Keep `!user.IsDemo → Forbidden` as well (identity guard) |
| `DemoService.UpgradeAsync :368-372` mutation | clears user flags | **both**: keep the user lines; add `workspace.DemoConvertedAt = now;` then **if `Demo:ConvertRequiresVerification` is false (default, D17.5 confirmed):** `workspace.DemoExpiresAt = null;` **else:** `workspace.DemoExpiresAt = now.AddHours(Demo:ConvertVerifyHours (72))` and `workspace.DemoExpiryWarnedAt = null` (a fresh warning window); `DemoExtendedAt` **kept** (history); `DemoCommentCapOverride = DemoTtlHoursOverride = null`; `workspace.Name = string.IsNullOrWhiteSpace(request.WorkspaceName) ? Workspace.PlaceholderName : request.WorkspaceName.Trim()` (D17.3); `workspace.UpdatedAt = now; UpdatedBy = callerPublicId`; `Workspaces.Update(workspace)` — in the **same** `SaveChangesAsync` as the user (`:390`) |
| `DemoService.UpgradeAsync :416,433,442` (`user.OwnerId`) | legacy read | `workspaceId` (the parameter) |
| `DemoCleanupService.cs:43-54` | expired users → `u.OwnerId` | `uow.Workspaces.IgnoreQueryFilters().AsNoTracking().Where(w => w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt < now).Select(w => w.Id)`; `:20` `FromHours(1)` → **`FromMinutes(15)`** (D17.8); plus the warning pass (§3.5); **plus the re-check before every delete (§3.5, Gemini Pro DB-17 #2)** |
| `TenantService.HardDeleteAsync :511-549` | trusts the caller; deletes the owner folder before the transaction | when `reason == "demo_expired"`: **before `:549`** `if (!await Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Id == workspaceId && w.DemoExpiresAt != null && w.DemoExpiresAt < DateTime.UtcNow)) return Result.Failure("Not an expired demo (converted or extended meanwhile).");` and **inside** `ExecuteInTransactionAsync`, first statement: `await _unitOfWork.ExecuteSqlRawAsync("SELECT id FROM workspaces WHERE id = {0} FOR UPDATE", workspaceId);` then the same `AnyAsync` again → on failure `throw new InvalidOperationException("demo converted during delete")` (rolls back; the hosted loop logs it and moves on). The audit row is written before `:549` today — move the `demo_expired` audit write **after** the first check so a refused delete writes no `tenant.hard_deleted`. Other reasons unchanged |
| `CommentService.cs:165-190` cap | `CurrentAdminAsync(owner)?.User.IsDemo/…Override` | `var demoWs = await _unitOfWork.Workspaces.IgnoreQueryFilters().Where(w => w.Id == owner && w.DemoExpiresAt != null).Select(w => new { w.DemoCommentCapOverride }).FirstOrDefaultAsync(); if (demoWs != null) { cap = demoWs.DemoCommentCapOverride ?? setting; … }` (same message) |
| `ExportImportService.cs:592-598` import cap | same shape | same replacement |
| `TenantService.ListAsync :153-157` | from `admin?.User` | `IsDemo = w.DemoExpiresAt != null, ExpiresAt = w.DemoExpiresAt, DemoExtended = w.DemoExtendedAt != null, DemoCommentCapOverride = w.DemoCommentCapOverride, DemoTtlHoursOverride = w.DemoTtlHoursOverride` (DTO names unchanged — no dashboard churn) |
| `TenantService.ExtendDemoAsync(int id) :343-386` | user row | `ExtendDemoAsync(Guid workspaceId)`: workspace live & `DemoExpiresAt != null` else `NotFound("Demo tenant not found.")`; `DemoExtendedAt != null → Failure("This demo has already been extended once.")`; `ttl = w.DemoTtlHoursOverride ?? setting`; `anchor = w.DemoExpiresAt > now ? w.DemoExpiresAt : now`; `w.DemoExpiresAt = anchor + ttl; w.DemoExtendedAt = now`; **dual-write** the admin identity (`CurrentAdminAsync(workspaceId)?.User`: `ExpiresAt`, `DemoExtended = true`); audit unchanged (`TenantDemoExtended`, `expires_at`) |
| `TenantService.SetDemoConfigAsync(int id, …) :388-430` | user row | `SetDemoConfigAsync(Guid workspaceId, …)`: validation unchanged; write the two overrides on the workspace **and** the admin identity; audit unchanged |
| `TenantsController.cs:118-140` | `{id:int}` | `[HttpPost("{workspaceId:guid}/extend")]`, `[HttpPatch("{workspaceId:guid}/demo-config")]` (F9 precedent `:104,145,162`); `ITenantService` signatures follow |
| `AuthService` — every path that mints a workspace token | no expiry check | one private helper `Task<HashSet<Guid>> ExpiredDemoWorkspaceIdsAsync(IEnumerable<Guid> workspaceIds)` (`Workspaces.IgnoreQueryFilters().Where(w => ids.Contains(w.Id) && w.DemoExpiresAt != null && w.DemoExpiresAt < DateTime.UtcNow).Select(w => w.Id)`; materialise `ids` first so `Contains` translates), applied at **every** `_tokenService.Issue(` call site (acceptance crit. 7 counts them): (a) `LoginAsync` directly after `:773` — drop expired memberships; if none remain → `Failure(MessageKeys.Demo.DemoExpired)` (for any identity, not only `IsDemo` — an invited real user of an expired demo gets the same answer); (b) `SwitchWorkspaceAsync` after the `workspaceLive` check `:1023-1027` — expired → `Failure(Demo.DemoExpired)` (Opus DB-17 #2: today it mints a fresh 12 h token); (c) quick-access login (`:1541-1584`, `link.OwnerId`) and (d) key login — same check on the link's/key's `OwnerId` → `Failure(Demo.DemoExpired)` (a demo admin can mint quick-access invites, so "quick-access cannot reach a demo" is **not** true and is not relied on) |
| `UserMapper.ToMeResponse` | no demo fields | new optional parameter `Workspace? workspace = null` → `DemoExpiresAt = workspace?.DemoExpiresAt`, `DemoCanExtend = workspace?.DemoExpiresAt != null && workspace.DemoExtendedAt == null && workspace.DemoConvertedAt == null`; callers that hold the current workspace (login `ok`, `/me`, switch, upgrade) pass it; others pass nothing |
| `TenantsController.cs:118-140` (int routes) | `{id:int}/extend`, `{id:int}/demo-config` | **kept for one release** (Opus DB-17 #3: the deployed dashboard calls them until its own deploy), marked `[Obsolete("DB-17: use the {workspaceId:guid} route; removed by DB-11e")]`, still `[Audited]`, delegating: resolve `users.id → its live Workspace Admin membership's OwnerId` (`IMembershipService.ListForIdentityAsync`) and call the Guid overload; removed in **DB-11e**. The new `{workspaceId:guid}` routes are added beside them (row above) |

Everything that reads `users.is_demo` as an identity fact (§2) is **unchanged**. `DemoExtended`/`ExpiresAt`/overrides on `users` are written by the
sites above but **read by nobody** after this release → DB-11e drops them (its scope line: "also `users.expires_at` + `IX_users_expires_at`,
`"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"`, and the `[Obsolete]` `{id:int}` extend/demo-config routes; **keep** `is_demo` and `recipient_email`").

### 3.4 Convert = `UpgradeAsync`, generalised (b)

Unchanged route and response; four additions: (1) `UpgradeDemoRequest.WorkspaceName` (`string?`, ≤ 120, validator `.MaximumLength(120)` + "not whitespace-only
when provided" — same rule as DB-03b's rename); (2) the service takes the **session's** `workspaceId` (§3.3; `DemoController` gains `ICurrentUser currentUser`
and uses `TenantStamp.TryRequireOwner`); (3) the workspace mutation of §3.3 in the same transaction; (4) `UpgradeDemoResponse` unchanged but `User`
(MeResponse) now carries `DemoExpiresAt` (null with the flag off) and `DemoCanExtend = false`.
**Pre-wired verification mode (Opus DB-17 #4; off by default, D17.5 confirmed):** `Demo:ConvertRequiresVerification` (bool, default `false`) and
`Demo:ConvertVerifyHours` (int, default `72`) in `appsettings.json` + `docker-compose.prod.yml` (`Demo__ConvertRequiresVerification: "${DEMO_CONVERT_REQUIRES_VERIFICATION:-false}"`,
`Demo__ConvertVerifyHours: "${DEMO_CONVERT_VERIFY_HOURS:-72}"`) + `.env.prod.example` (commented). Read by `DemoService` through `IConfiguration`
(constructor gains `IConfiguration? config = null`, default null → flag off, so the existing test harnesses compile). When **true**, conversion keeps the
TTL running (`DemoExpiresAt = now + 72 h`, §3.3) and the TTL is cleared by `IDemoService.OnEmailVerifiedAsync(User identity)` — called from the two sites
that set `EmailVerifiedAt` (`EmailVerificationService.ConfirmAsync :164`, `AuthService.ConfirmEmailChangeAsync :535`) right after their own save:
`Workspaces.IgnoreQueryFilters().Where(w => w.DemoConvertedAt != null && w.DemoExpiresAt != null && <identity holds a live Workspace Admin membership in w>)`
→ `DemoExpiresAt = null`, `SaveChangesAsync`. With the flag off the method finds nothing (converted rows have no TTL) and is a no-op; it is still wired
so flipping the flag needs **no code change**. The expiry sweep then deletes an unverified converted workspace like any demo (`reason: "demo_expired"`;
the T-2 h reminder goes to the **new** address — `users.email` — since `RecipientEmail` was cleared on convert: `WarnExpiringAsync` uses
`admin.User.RecipientEmail ?? admin.User.Email`). Kept as today: all data (projects, comments incl. the three seeded
samples — D17.4, memberships, API keys, settings), the `Workspace Admin` membership, `demo_started` + `workspace_converted` facts, audit
`auth.demo.upgraded` (R10) with `After: { ["source"] = "demo" }` added, `EmailVerifiedAt = null` + verification mail (DB-14), identity stamp rotation
(R16), D7 conflict. **"Require verified e-mail + password" (task wording) is satisfied as:** password = `StrongPassword` policy at the request
(`UpgradeDemoValidator.cs:15-16`; DB-14 re-validation `PasswordPolicy.Validate`); verified e-mail = the converted admin is gated from every
non-GET `/api/admin/*` until the link is clicked (`RequireVerifiedEmailFilter`, DB-14) — conversion itself does **not** wait for the click
(**D17.5, owner-confirmed 2026-09-23 18:05 Riyadh**); the stricter mode is one env flag away (above).
The `Demo.UpgradeSuccess` message stays.

### 3.5 Expiry, warning, cleanup (c) — `DemoCleanupService` (15-min cadence) + `IDemoService` additions

`IDemoService` gains:
```csharp
/// <summary>DB-17 §3.5. Sends the one T-2h reminder to each live demo whose expiry is within warnWindow and not yet warned; stamps
/// workspaces.demo_expiry_warned_at whether or not the send succeeded (one attempt, D17.6). Returns the number of workspaces stamped.</summary>
Task<int> WarnExpiringAsync(DateTime nowUtc, TimeSpan warnWindow);
/// <summary>DB-17 §3.6. Self-service one-time extension by the demo's Workspace Admin (POST /api/demo/extend); workspaceId = the session's tenant.</summary>
Task<Result<DemoStatusResponse>> ExtendAsync(Guid callerPublicId, Guid workspaceId);
/// <summary>DB-17 §3.4. Clears the TTL of converted workspaces this identity administers (no-op unless Demo:ConvertRequiresVerification was on at convert time).</summary>
Task OnEmailVerifiedAsync(User identity);
/// <summary>DB-17 §3.7. Deletes demo_email_* throttle rows older than 2 days (the raw-address legacy rows included).</summary>
Task<int> SweepThrottleRowsAsync(DateTime nowUtc);
```
`WarnExpiringAsync`: `Workspaces.IgnoreQueryFilters().Where(w => w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt > now && w.DemoExpiresAt <= now + warnWindow && w.DemoExpiryWarnedAt == null).ToListAsync()`;
per workspace: `admin = await _memberships.CurrentAdminAsync(w.Id)`; `to = admin?.User.RecipientEmail ?? (w.DemoConvertedAt != null ? admin?.User.Email : null)` (a converted-but-unverified workspace in the flag-on mode has no `RecipientEmail` any more); if `to` non-blank → `_emailService.SendAsync(to, $"Your {productName} demo expires in about two hours", BuildExpiryWarningHtml(...))`
inside `try/catch` (best-effort, `IEmailService` is capped per day); `w.DemoExpiryWarnedAt = now; Workspaces.Update(w); SaveChangesAsync()` per row
(a failure on one workspace must not block the others — same per-item isolation as `DemoCleanupService.cs:71-95`). Body (verbatim, HTML-encode the
workspace name; `{app}` = `brand.Urls.App`): "Your demo workspace, **{name}**, expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC. Everything in it — the
project, its comments and screenshots — is deleted then. To keep it, open {app} and choose **Keep this workspace** (you pick your e-mail and a
password; nothing is lost). Need a little more time? **Extend once** adds {ttl} hours. If you did not start this demo, ignore this e-mail."
Log: `DemoCleanupService: warned {Count} expiring demo(s)`. No audit row (D17.9).

`DemoCleanupService.SweepAsync` becomes three isolated steps, each in its own `try/catch` + scope: (1) `IDemoService.WarnExpiringAsync(now, TimeSpan.FromHours(2))`;
(2) the expiry delete — **inside the per-id loop and its fresh scope, immediately before `HardDeleteAsync`, re-check** (Gemini Pro DB-17 #2: a conversion
that commits between the id query and the delete must not lose the user's data):
```csharp
var stillExpired = await uow.Workspaces.IgnoreQueryFilters()
    .AnyAsync(w => w.Id == pid && w.DemoExpiresAt != null && w.DemoExpiresAt < DateTime.UtcNow, stoppingToken);
if (!stillExpired) { logger.LogInformation("DemoCleanupService: {Id} no longer an expired demo (converted/extended); skipped", pid); continue; }
var result = await tenantService.HardDeleteAsync(pid, reason: "demo_expired");   // re-checks again before the folder delete and FOR UPDATE inside the tx (§3.3)
```
→ `tenant.hard_deleted` System row, owner folder + rows gone, `demo_started`/`workspace_converted`/`usage_daily`/audit rows survive with `owner_id = NULL`;
(3) `IDemoService.SweepThrottleRowsAsync(now)`.
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
  `[ProducesResponseType(typeof(DemoStatusResponse), 200)]` + 400 + 403; no body. Controller: `if (!TenantStamp.TryRequireOwner(currentUser, out var ws)) return StatusCode(403, Result.Forbidden(MessageKeys.Common.Forbidden));`
  then `demoService.ExtendAsync(User.GetId(), ws)`. `DemoService.ExtendAsync(callerPublicId, workspaceId)`: identity live (`FindIdentityByPublicIdAsync`);
  `GetMembershipAsync(user.Id, workspaceId)` live with `Role.Name == "Workspace Admin"` and the workspace has `DemoExpiresAt != null` (else → `Forbidden(Demo.NotDemoUser)`) — the
  **session's** workspace, never a search across the identity's memberships (Gemini Pro DB-17 #3); `DemoExpiresAt < now → Failure(Demo.DemoExpired)`;
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
| D17.5 | Must the new address be verified before the workspace becomes permanent? | **No — owner-confirmed 2026-09-23 18:05 Riyadh** ("ok"): convert immediately, clear the TTL; DB-14's gate blocks admin writes until verified. The stricter mode is **pre-wired** (Opus DB-17 #4): `Demo:ConvertRequiresVerification=true` keeps a 72 h TTL on the converted workspace until `EmailVerificationService.ConfirmAsync :164` / `AuthService.ConfirmEmailChangeAsync :535` clear it (§3.4) — flip on first abuse (report §2 "CAPTCHA (first abuse)"), no code change |
| D17.6 | Expiry warning e-mail | **Yes**, once, when ≤ 2 h remain, to `users.recipient_email` of the current admin; one attempt, stamped regardless |
| D17.7 | PDPL notice on the demo form | **Notice text + privacy link in the form; no consent checkbox; no API change.** The demo processes one address for one purpose (credentials + reminder) and deletes it with the workspace |
| D17.8 | TTL enforcement between expiry and deletion | **15-min sweep + login refusal; no JWT `exp` capping.** ≤ 15 min of grace on an already-issued token is accepted |
| D17.9 | Audit rows for the reminder / throttle sweep | **No** (notifications and bookkeeping; the delete already writes `tenant.hard_deleted`). `demo.extended` **is** audited (state change by a user) |

## 4. Safety classification

**Expand** (R2 step 1 + 2 in one release, rollback-safe because the old shape stays written): Migration 1 additive (R1); Migration 2 an R3 backfill
with `.Sql(` → marker + `[ContractMigration("DB-17")]` → **contract deploy** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db17 bash scripts/deploy-api.sh`
(R7; stop → labelled dump → boot). Nothing is dropped, narrowed or renamed. Code deletes: (a) expired demo workspaces — same `HardDeleteAsync`, now
15-min cadence and **double-checked** (loop re-check + in-service check before the folder delete + `FOR UPDATE` re-check inside the transaction) so a
conversion racing the sweep is never deleted; (b) `app_settings` `demo_email_*` rows older than 2 days (PII cleanup). Tenancy: every new read is by
the **session's** workspace id (`TryRequireOwner`, R16) or an explicit id; §6 tests 7–8 prove B cannot extend, convert or read A's demo state. **The backfill
keys on the identity's own workspace and refuses any workspace with a second member** (§3.2) — the over-reach the cross-review caught. **R7 marker for
Migration 2 (owner approval given 2026-09-23):** `// DB-RULES: R3 backfill approved 2026-09-23 by Moamen (owner; F4 + D17.5 confirmed 2026-09-23, relayed by the orchestrator; docs/db/execution/DB-17-demo-as-product.md)`.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — six properties (§3.1 names/types) + the doc-comment block, after `DeletedBy` (`:24`). `Infrastructure/Mappings/WorkspaceMapping.cs`
   — six `HasColumnName` and the partial index (**no** check constraint — §3.1).
2. `just migrate name="AddWorkspacesDemoColumns"` → read against §3.1 (6 columns + 1 index on `workspaces`, nothing else; snapshot diff only `Workspace`).
3. `just migrate name="BackfillWorkspacesDemoState"` → empty scaffold; paste §3.2 `Up`/`Down` SQL; add the marker comment line and `[ContractMigration("DB-17")]`
   (`using Pointer.Infrastructure.Migrations;` — the attribute is `Infrastructure/Migrations/ContractMigrationAttribute.cs`). `Tests/MigrationSafetyTests.cs` must pass (marker + attribute agree).
4. `Application/Services/Implementation/DemoService.cs` — §3.3 rows (Provision dual-write, **`OwnerId = workspaceId` kept**; active cap on workspaces; throttle key §3.6;
   `UpgradeAsync(callerPublicId, workspaceId, request)` guards + mutation incl. the `Demo:ConvertRequiresVerification` branch, `workspaceId` for `user.OwnerId` at `:416,433,442`),
   `ExtendAsync(callerPublicId, workspaceId)`, `WarnExpiringAsync` (+ `BuildExpiryWarningHtml`, copy `:470-501`'s shape), `SweepThrottleRowsAsync`, `OnEmailVerifiedAsync`.
   `IDemoService.cs` — `UpgradeAsync` signature + the four new members (§3.5 verbatim). Constructor gains `IConfiguration? config = null` (last, optional — existing
   harnesses `DemoUpgradeTests.cs:76-101`, `DemoSessionEmailTests.cs:153-162`, `DemoServiceAnalyticsFailureTests.cs` compile; their `UpgradeAsync(callerId, request)` calls gain the workspace id).
4a. `EmailVerificationService.cs:164` and `AuthService.cs:535` — after each method's own `SaveChangesAsync`, `await _demo.OnEmailVerifiedAsync(identity);` (`IDemoService` injected; Scrutor).
    `TenantService.HardDeleteAsync :511-549` — the `demo_expired` pre-check + `FOR UPDATE` re-check (§3.3 row); move the audit write below the pre-check.
5. `Application/DTOs/Demo/UpgradeDemoRequest.cs` — `public string? WorkspaceName { get; set; }`; `Application/Validators/UpgradeDemoValidator.cs` — rule (§3.4).
   `Application/DTOs/Demo/DemoStatusResponse.cs` (new). `Application/DTOs/Auth/MeResponse.cs` — two properties (§3.6). `Application/Common/UserMapper.cs:17` —
   `Workspace? workspace = null` parameter + two assignments; update callers in `AuthService` (login ok / me / switch — pass the current workspace, loaded
   `IgnoreQueryFilters` by the tenant id already in hand) and `DemoService.UpgradeAsync :452`.
6. `Application/Common/AuditActions.cs` — `DemoExtended` (§3.6). `Application/Resources/MessageKeys.cs:413-421` — `AlreadyExtended`, `Extended`.
7. `API/Controllers/DemoController.cs` — primary constructor gains `ICurrentUser currentUser`; `Upgrade` (`:47-55`) resolves the workspace first:
   `if (!TenantStamp.TryRequireOwner(currentUser, out var ws)) return StatusCode(403, Result.Forbidden(MessageKeys.Common.Forbidden));` then `demoService.UpgradeAsync(callerId, ws, request)`. Add after `Upgrade`:
   ```csharp
   /// <summary>DB-17: the demo admin's one-time extension (+TTL hours) of the CURRENT workspace. Operator extensions live under /api/admin/tenants.</summary>
   [Authorize(Policy = Policies.Admin)]
   [Audited(AuditActions.DemoExtended)]
   [HttpPost("extend")]
   [ProducesResponseType(typeof(DemoStatusResponse), StatusCodes.Status200OK)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status400BadRequest)]
   [ProducesResponseType(typeof(Result), StatusCodes.Status403Forbidden)]
   public async Task<IActionResult> Extend()
   {
       if (!TenantStamp.TryRequireOwner(currentUser, out var ws))
           return StatusCode(StatusCodes.Status403Forbidden, Result.Forbidden(MessageKeys.Common.Forbidden));
       var result = await demoService.ExtendAsync(User.GetId(), ws);
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
8. `Application/Services/Implementation/TenantService.cs` — `ListAsync :153-157` mapping; `ExtendDemoAsync(Guid workspaceId)`, `SetDemoConfigAsync(Guid workspaceId, …)` (§3.3, dual-write);
   `ITenantService.cs` signatures. `API/Controllers/Admin/TenantsController.cs:118-140` — **add** `[HttpPost("{workspaceId:guid}/extend")]` / `[HttpPatch("{workspaceId:guid}/demo-config")]`
   and **keep** the `{id:int}` actions one release, `[Obsolete(...)]`, delegating via the identity's live Workspace Admin membership (§3.3 last row).
9. `API/Hosted/DemoCleanupService.cs` — §3.5 (three isolated steps; `PeriodicTimer(TimeSpan.FromMinutes(15))`; workspace query; **the `stillExpired` re-check in the loop**; keep the per-item scopes and log lines).
10. `Application/Services/Implementation/CommentService.cs:165-190` and `ExportImportService.cs:592-598` — workspace-based cap (§3.3). `AuthService.cs` — `ExpiredDemoWorkspaceIdsAsync` helper applied at
    `LoginAsync :773`, `SwitchWorkspaceAsync :1023-1030`, quick-access login `:1541-1584`, key login (§3.3 row).
10a. `API/appsettings.json`, `docker-compose.prod.yml`, `.env.prod.example` — `Demo:ConvertRequiresVerification` (false) and `Demo:ConvertVerifyHours` (72) (§3.4).
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
   `Upgrade_TargetsSessionWorkspace_NotAnotherDemoOfSameIdentity` (identity admin of demos A and B; call with `workspaceId = A` → only A converted, B untouched — Gemini Pro #3);
   `Upgrade_WithVerificationFlag_KeepsTtl72h_ClearedByOnEmailVerified` (`Demo:ConvertRequiresVerification=true` via `ConfigurationBuilder` → `DemoConvertedAt != null && DemoExpiresAt ≈ now+72h`; then `OnEmailVerifiedAsync(user)` → `DemoExpiresAt == null`);
   `OnEmailVerified_FlagOff_NoOp`; existing `DemoUpgradeTests` facts keep passing with the added `workspaceId` argument (D7 conflict, stamp rotation, `EmailVerifiedAt = null`, `InvalidateGate`, `workspace_converted` once).
4. `Extend_Once_MovesExpiry_StampsExtendedAt_AuditsDemoExtended` (`RecordingAuditWriter` from `Tests/AuditWrittenByServicesTests.cs`; `After["expires_at"]` present);
   `Extend_Twice_Fails_AlreadyExtended`; `Extend_AfterExpiry_Fails`; `Extend_NonDemoWorkspace_Forbidden`; `Extend_UsesTtlOverride_WhenSet`;
   `OperatorExtend_ThenSelfExtend_Fails` (shared `demo_extended_at`); `Extend_TargetsSessionWorkspace_Only` (two demos, one identity → only the passed id moves).
5. `WarnExpiring_SendsOnce_ToRecipientEmail_StampsEvenWhenSendFails` (spy e-mail; second call sends nothing); `WarnExpiring_OutsideWindow_Nothing`; `WarnExpiring_NoRecipientEmail_StampsWithoutSending`.
6. `SweepThrottleRows_DeletesOldHashedAndLegacyRawRows_KeepsToday` (seed `demo_email_<hash>_<today>`, `demo_email_<hash>_<3 days ago>`, `demo_email_someone@x.com_<3 days ago>` → only today's remains).
7. **R8** `Extend_TenantB_Admin_CannotExtendA` (B's admin calls `ExtendAsync` → Forbidden; A's `DemoExpiresAt` unchanged); `Upgrade_TenantB_Identity_CannotConvertA`
   (identity with a membership only in B, `IsDemo = true` set by hand, A is the demo → Forbidden). `TenantQueryFilter`-style: `Workspace` filter unchanged (`Tests/TenantQueryFilterTests.cs` shape) — B's context sees no `DemoExpiresAt` of A.
8. `Login_ExpiredDemo_Refused_DemoExpired` (`AuthService` fixture from `Tests/WorkspaceSwitchTests.cs`; demo identity, workspace `DemoExpiresAt` past → `Failure(Demo.DemoExpired)`);
   `Login_LiveDemo_Ok_MeCarriesDemoExpiresAt_AndCanExtend`; `Login_ConvertedWorkspace_Ok_NoDemoFields`; **`Switch_ExpiredDemo_Refused`** (identity in real workspace A and expired demo B;
   `SwitchWorkspaceAsync(B)` → `Failure(Demo.DemoExpired)`, no token; `SwitchWorkspaceAsync(A)` still ok — Opus #2); `QuickAccessLogin_ExpiredDemo_Refused`; `KeyLogin_ExpiredDemo_Refused`.
9. Sqlite `TestDb` (`RetentionServiceTests.cs:44-70`): `DemoCleanup_DeletesExpiredWorkspace_ByWorkspaceColumn_NotUsers` — seed a workspace with `DemoExpiresAt` past and an admin whose `users.expires_at` is **null** → deleted
   (proves the read-switch); `DemoCleanup_LeavesLiveDemo`; `DemoCleanup_WritesTenantHardDeleted_System` (extend `AuditWrittenByServicesTests.cs:873-900`);
   **`DemoCleanup_SkipsWorkspaceConvertedBetweenQueryAndDelete`** (Gemini Pro #2: a fake `ITenantService` that, on first call, converts the workspace via the real `DemoService` *before* delegating —
   or simpler: seed expired, pass an id list from a first query, convert, run the loop body → `HardDeleteAsync` not called, workspace present);
   `HardDelete_DemoExpiredReason_RefusesConvertedWorkspace_NoFolderDelete` (`RecordingFileStorage`: `DeletedOwners` empty, no `tenant.hard_deleted` row); `HardDelete_AdminReason_UnaffectedByDemoState`;
   drive the static/internal sweep method (make `DemoCleanupService.SweepOnceAsync(IUnitOfWork, ITenantService, IDemoService, ILogger, ct)` `internal static`, ImpersonationSweepService precedent).
9a. **Backfill over-reach** — R11 rehearsal step of §3.2 (`Backfill_DemoIdentityWithMembershipInRealWorkspace_DoesNotTagIt`); the migration SQL is Postgres-only, so this is proven on the rehearsal DB and pasted into the PR, not in `just test`.
9b. `TenantsController` int routes: `ExtendDemo_IntRoute_StillWorks_AndIsObsolete` (reflection: `[Obsolete]` present on both int actions; `[Audited]` present on all four).
10. `Tests/AuditCoverageTests.cs` passes with the new action (`[Audited]` present); `Tests/MigrationSafetyTests.cs` passes (marker + attribute on Migration 2 only).
11. `Tests/CommentFieldsTests.cs`/`CommentServiceQuickAccessTests.cs` fixture: `CommentCap_UsesWorkspaceOverride_NotUser` (workspace `DemoCommentCapOverride = 1`, user's is null → second comment refused);
    `ImportCap_UsesWorkspaceOverride` (ExportImport).
12. Existing data survives: rehearsal (§7 criterion 8) — the backfill copies every live demo (count equals the §3.2 pre-check), real workspaces keep six NULLs.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddWorkspacesDemoColumns`, `_BackfillWorkspacesDemoState` (in that order);
   `grep -c "AddColumn" Infrastructure/Migrations/*_AddWorkspacesDemoColumns.cs` → 6; `grep -c "AddCheckConstraint"` on it → 0; `grep -c "ContractMigration(\"DB-17\")" Infrastructure/Migrations/*_BackfillWorkspacesDemoState.cs` → 1; `grep -c "R3 backfill approved 2026-09-23 by Moamen"` on it → 1; `grep -c "u.owner_id = w.id\|NOT EXISTS" Infrastructure/Migrations/*_BackfillWorkspacesDemoState.cs` → 1 each; `grep -c ContractMigration Infrastructure/Migrations/*_AddWorkspacesDemoColumns.cs` → 0.
2. `grep -c "u.OwnerId\|user.OwnerId" API/Hosted/DemoCleanupService.cs Application/Services/Implementation/DemoService.cs` → 0 each (no legacy `users.owner_id` **reads** on the demo path).
2a. `grep -c "OwnerId = workspaceId" Application/Services/Implementation/DemoService.cs` → ≥ 1 (the legacy column is still **written** at provision — Opus #5).
2b. `grep -c "stillExpired" API/Hosted/DemoCleanupService.cs` → ≥ 1; `grep -c "FOR UPDATE" Application/Services/Implementation/TenantService.cs` → ≥ 1; `grep -c "TryRequireOwner" API/Controllers/DemoController.cs` → 2.
3. `grep -c "DemoExpiresAt" Application/Services/Implementation/CommentService.cs Application/Services/Implementation/ExportImportService.cs Application/Services/Implementation/TenantService.cs API/Hosted/DemoCleanupService.cs Application/Services/Implementation/AuthService.cs` → ≥ 1 each;
   `grep -c "User.IsDemo\|User.DemoCommentCapOverride" Application/Services/Implementation/CommentService.cs Application/Services/Implementation/ExportImportService.cs Application/Services/Implementation/TenantService.cs` → 0 each.
4. `grep -c "{workspaceId:guid}/extend\|{workspaceId:guid}/demo-config" API/Controllers/Admin/TenantsController.cs` → 2; `grep -c "{id:int}/extend\|{id:int}/demo-config"` → 2 (kept one release) and `grep -c "Obsolete(" API/Controllers/Admin/TenantsController.cs` → 2.
5. `curl -s …/swagger.json | jq '.paths["/api/demo/extend"].post.tags'` → `["Demo"]`; `jq '.components.schemas.MeResponse.properties | has("demoExpiresAt") and has("demoCanExtend")'` → `true`.
6. **R14/PII:** `grep -c "ToLowerInvariant" Application/Services/Implementation/DemoService.cs` → 0; `grep -c "PseudonymHasher.EmailHash" Application/Services/Implementation/DemoService.cs` → 1;
   after one sweep on the rehearsal DB: `SELECT count(*) FROM app_settings WHERE key LIKE 'demo_email_%' AND key LIKE '%@%';` → 0.
7. `grep -c "FromMinutes(15)" API/Hosted/DemoCleanupService.cs` → 1; `grep -c "WarnExpiringAsync\|SweepThrottleRowsAsync" API/Hosted/DemoCleanupService.cs` → 1 each;
   `grep -c "_tokenService.Issue(" Application/Services/Implementation/AuthService.cs` **equals** `grep -c "ExpiredDemoWorkspaceIdsAsync" Application/Services/Implementation/AuthService.cs` minus 1 (the helper's own definition) — every token mint is guarded;
   `grep -c "OnEmailVerifiedAsync" Application/Services/Implementation/EmailVerificationService.cs Application/Services/Implementation/AuthService.cs` → 1 each; `grep -c "DEMO_CONVERT_REQUIRES_VERIFICATION" docker-compose.prod.yml .env.prod.example` → 1 each.
8. Rehearsal (R11) on a same-day dump: `dotnet ef migrations script --idempotent` shows both migrations; after `database update`:
   `SELECT id, name, demo_expires_at, demo_extended_at, demo_converted_at FROM workspaces;` → real workspaces all NULL, every live demo from the pre-check populated;
   `\d workspaces` shows `ix_workspaces_demo_expires_at … WHERE (demo_expires_at IS NOT NULL)` and no `ck_workspaces_demo_*` constraint; the §3.2 over-reach rehearsal (seeded demo identity with a membership in the real workspace) leaves the real workspace's `demo_expires_at` NULL.
   Then on the rehearsal API: `POST /api/demo` → `MeResponse.demoExpiresAt` set; `POST /api/demo/extend` → 200 once, 400 twice; `POST /api/demo/upgrade { email, password, workspaceName }` → 200,
   `SELECT demo_expires_at, demo_converted_at, name FROM workspaces WHERE id = …` → `NULL, <ts>, <name>`; set `demo_expires_at = now() - interval '1 minute'` on a second demo → within 15 min
   `tenant.hard_deleted` row with `after->>'reason' = 'demo_expired'` and the workspace gone; login with that demo's credentials → `DemoExpired` message before the sweep;
   the old dashboard call `POST /api/admin/tenants/{users.id}/extend` still returns 200 (int route kept).
9. `just test` green with the ~30 new facts; DB-10 green (apply from empty + newest `Down()`/`Up()` round-trip — Migration 2's `Down()` is a real statement).

## 8. Rollback

Migration 2 `Down()` nulls the six columns on unconverted workspaces (the `users` columns still hold the source thanks to dual-write); Migration 1
`Down()` drops constraint, index, columns. Code rollback = `git checkout` the commit before this doc + `up -d --build api`: the previous code reads
`users.expires_at/…`, which this release kept writing, so **demos provisioned or extended while DB-17 was live keep their TTL under the old code**.
Conversions performed while live are real conversions (identity has a real e-mail/password; `users.is_demo = false`) — not reversible by rollback,
and that is the feature. **Hard-deleted expired demos are not restorable** except from the `pre-db17` dump + uploads tarball (they are demos; nothing
of value). The labelled dump is taken by the contract deploy itself (R7).

## 9. Release steps

0. **Owner decision D17.5 — done:** confirmed 2026-09-23 18:05 Riyadh (convert immediately; verification gate only). Ship with `DEMO_CONVERT_REQUIRES_VERIFICATION` unset (= false).
1. **Prod pre-checks (read-only, pasted into the PR):** §3.2 **Q1** (every listed row must be a `Demo Workspace`), **Q2** (expected 0; otherwise hand-delete or explain), **Q3** (expected 0), and `SELECT count(*) FROM app_settings WHERE key LIKE 'demo_email_%';` (the number of PII rows the first sweep will delete).
2. R11 rehearsal on a same-day dump (§7 criterion 8), including the two hosted-job checks.
3. **Contract deploy:** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db17 bash scripts/deploy-api.sh` (stop → dump `pre-db17` → boot; the gate refuses a plain deploy because of `[ContractMigration("DB-17")]`). R7.1 batching: not needed (one doc; DB-16 is an ordinary deploy and may ride along as code, but ship it separately for attribution).
4. Verify: `docker compose logs --since 5m api | grep -iE "migrat|DemoCleanup|error"` — two `Applying migration` lines, then `DemoCleanupService: … throttle rows deleted N` (N = step 1's count); `SELECT demo_expires_at FROM workspaces WHERE demo_expires_at IS NOT NULL;` matches the pre-check;
   `GET /api/auth/me` as the production admin → `demoExpiresAt: null`, `demoCanExtend: false`; start a demo from `demo.pointer.moamen.work` → banner shows the server countdown; extend once; convert; confirm `tenant.hard_deleted … demo_expired` appears for a demo left to expire (≤ 15 min after its TTL).
5. Watch for: `DemoCleanupService: … failed` (must not appear), `Conflict`/`Forbidden` bursts on `POST /api/demo/extend` (dashboard offering the button after use → §11 task 2), `demo_email_` rows growing without deletion.
6. Dashboard: `dashboard-agent` regenerates the client from production once (adds `postApiDemoExtend`, `DemoStatusResponse`, `MeResponse.demoExpiresAt/demoCanExtend`, `UpgradeDemoRequest.workspaceName`, the `{workspaceId}` tenant routes), then §11.
7. Open **DB-11e** scope amendment (one line in `DB-REVIEW §7`): drop `users.expires_at` + `IX_users_expires_at`, `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"` and the `[Obsolete]` `{id:int}` tenant routes after one release with `grep -rn "\.ExpiresAt\|DemoExtended\|DemoCommentCapOverride\|DemoTtlHoursOverride" Application API --include='*.cs' | grep -v "DemoExpiresAt\|workspace\." ` → only the dual-write lines.

## 10. Out of scope

Dropping the `users` demo columns and the `[Obsolete]` int routes (**DB-11e**); a `demo_extended` funnel fact (DB-15 amendment); CAPTCHA and any consent checkbox (report §2 DEFER);
capping JWT `exp` at the demo TTL (D17.8); **turning on** `Demo:ConvertRequiresVerification` (pre-wired, off; an ops decision on first abuse, not a code change); moving `users.recipient_email`; changing
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
4. `features/tenants/TenantsPage.tsx:536-547`: switch to the new hooks `postApiAdminTenantsWorkspaceIdExtend({ workspaceId: tenant.workspaceId! })` / demo-config likewise
   (the int routes keep answering until the dashboard deploy lands; removed by DB-11e); disable "Extend" when `demoExtended` (unchanged field). Show `expiresAt` from the same DTO (unchanged).
5. i18n keys en + ar for the new strings.

**Widget:** none. **CLI:** none.
**Docs/landing (same PR, tasks 12–13):** privacy retention row; SCHEMA.md; DEPLOY.md sentence; DB-11c erase inventory line.

## 12. Cross-review adjudication (2026-09-23)

Reports: `docs/db/reviews/REVIEW-AGY-DB16-17-2026-09-23.md` (Gemini Pro via agy — IMPLEMENT WITH AMENDMENTS: 1 BLOCKER, 1 HIGH, 1 MEDIUM) and the Opus
review relayed by the orchestrator (1 BLOCKER, 1 HIGH, 3 MEDIUM, LOW/nits; it independently verified all six contradictions of §1 as real). Every
citation re-checked against the tree on 2026-09-23 @ `273f31e` (R5-61 merged since the doc was written; `AuthService`/`AuditActions`/`MessageKeys`/
`MeResponse`/`AppDbContext` line numbers refreshed in §2). Owner decision D17.5 confirmed 2026-09-23 18:05 Riyadh (§3.9, §9 step 0).

| Finding | Claim | Verdict | Where it landed |
|---|---|---|---|
| **Gemini Pro DB-17 #1 = Opus DB-17 #1 (BLOCKER)** | Backfill joins on *any* live membership → a demo identity invited into a real workspace tags that workspace with a TTL → hard-deleted at expiry | **Accepted** — `u.owner_id = w.id` (one-shot legacy read, R8.7 suspended for this statement as `HardDeleteAsync :576-590` already does), Workspace Admin role join, `NOT EXISTS` another live member; three pre-checks listing exact ids; rehearsal over-reach test | §3.2 (statement, Q1–Q3, rehearsal test), §4, §6 test 9a, §7 crit. 1/8, §9 step 1 |
| **Gemini Pro DB-17 #2 (HIGH)** | `DemoCleanupService` materialises ids then deletes; a conversion in between loses the user's data — `HardDeleteAsync` trusts the caller and deletes the folder first | **Accepted, widened** — re-check in the loop (as proposed) **and** in `HardDeleteAsync` for `reason == "demo_expired"`: before the folder delete and `FOR UPDATE` inside the transaction; audit write moved below the check | §2, §3.3 rows `DemoCleanupService`/`HardDeleteAsync`, §3.5, §4, §5 tasks 4a/9, §6 test 9, §7 crit. 2b |
| **Gemini Pro DB-17 #3 (MEDIUM)** | "The one demo the identity belongs to" is ambiguous for an identity in two demos | **Accepted** — `UpgradeAsync(caller, workspaceId, request)` / `ExtendAsync(caller, workspaceId)` take the **session's** workspace via `TenantStamp.TryRequireOwner` (R16), never a search | §3.3, §3.4, §3.6, §5 tasks 4/7, §6 tests 3–4, §7 crit. 2b |
| **Opus DB-17 #2 (HIGH)** | `SwitchWorkspaceAsync :992-1031` mints a 12 h token for an expired demo | **Accepted, widened** — one helper applied at every `_tokenService.Issue(` site: login, switch, quick-access, key login. "Quick-access cannot reach a demo" **rejected as a fact** (a demo admin can mint quick-access invites) — so it is guarded, not assumed | §2, §3.3 `AuthService` row, §5 task 10, §6 test 8, §7 crit. 7 |
| Opus DB-17 #3 (MEDIUM) | Re-keying the `{id:int}` tenant routes breaks the deployed dashboard between the two deploys | **Accepted** — int routes kept one release, `[Obsolete]`, delegating; guid routes added; removal in DB-11e | §3.3 last row, §5 task 8, §6 test 9b, §7 crit. 4/8, §9 step 7, §10, §11 task 4 |
| Opus DB-17 #4 (MEDIUM) | D17.5 lets an anonymous 3/h/IP endpoint create permanent unverified workspaces; needs an explicit owner gate + a pre-wired stricter mode | **Accepted** — owner confirmed D17.5 (2026-09-23 18:05 Riyadh); `Demo:ConvertRequiresVerification` (false) + `Demo:ConvertVerifyHours` (72) pre-wired with `OnEmailVerifiedAsync` at both `EmailVerifiedAt` set sites; check constraint **removed** because the flag-on state holds TTL and converted together | §3.1, §3.3, §3.4, §3.5, §3.9 D17.5, §5 tasks 4/4a/10a, §6 test 3, §7 crit. 7, §9 step 0, §10 |
| Opus DB-17 #5 (MEDIUM) | Provision must keep `users.owner_id = workspaceId` — pre-DB-17 cleanup and rollback read it, and now the backfill keys on it | **Accepted** | §2, §3.3 Provision row, §7 crit. 2a |
| Opus DB-17 #6 (LOW) | Tracked dual-write, `RemoveRange` hard delete, classification and order are fine | **Confirmation, no change** | — |
| Opus DB-17 #7 (LOW) | Keep six columns; document what the check constraint does not constrain | **Accepted** — six columns kept; the constraint is gone (see #4) and §3.1 now lists the code-enforced invariants and what the schema does *not* guarantee | §3.1 |
| Opus DB-17 #8 (NIT) | `User.cs:12-15` is `RoleId`'s doc-comment, not `OwnerId`'s | **Accepted** — cite `TenantService.cs:576-579` (the sentence that declares `users.owner_id` never-read) and `DemoCleanupService.cs:42` | §1, §2 |
| Gemini Pro §9 amendment | Add `AND m.owner_id = u.owner_id` to the pre-check | **Accepted, superseded** by the three explicit pre-checks Q1–Q3 (Q1 keys on `u.owner_id = w.id`; Q3 lists the over-reach case directly) | §3.2 |
