# DB-18 — Workspace self-service pause and delete (export first, pause instead, e-mail + password confirmation, grace period)

Owner request (verbatim, 2026-09-24): *"when the workspace admin delete the workspace it should shows the 'export' data option so the user could
store his data 'the data is currently able to be exported' also suggest to pause insted of delete. and it should confirm the deletion by sending
email to the workspace admin to confirm that process by opening the link from the email and enter his password"*.
Rules: **R1** (eight columns on `workspaces`, one with a server default, one partial index, three check constraints on a small table), R4 (plain mode — tiny
table), R5, R6, **R7** (ordinary deploy: nothing here is flagged by the DB-02 guard, so no `[ContractMigration]`), **R8** (session workspace only; R8.7
membership queries for "the admins of W"), R10 (new audit actions, the `owner_requested` reason and the `delete-workspace` token purpose are frozen once
shipped), R11, R13, **R14** (`paused_by`/`deletion_requested_by` are `public_id` content references, no FK, no PII), **R16** (stateless scoped e-mail
token, new purpose constant, **no table**; workspace-scoped event → no identity-stamp rotation), **R17** (every new mutation `[Audited]`, anonymous
token actions attribute the identity, no PII in `before/after`; one whitelist key added), R18 (operator surfaces get metadata only).
**Class: Additive** (no backfill, nothing dropped/renamed/narrowed). **Status: written 2026-09-24, not implemented; owner answered D18.1/.2/.8/.10
(§3.10); cross-reviewed 2026-09-24 (Gemini 3.1 Pro: 2 LOW + NITs; Opus: 3 HIGH, 8 MEDIUM, LOW/NITs — `docs/db/reviews/REVIEW-DB18-2026-09-24.md`),
every claim re-checked against `main` @ `9aaf1d3` and folded the same day (§12).** Remaining D18.x use the recommended defaults.

**Dependencies.** DB-11a/c/d (memberships, scoped tokens, sole-admin guard), DB-12 (`IAuditWriter`), DB-14 (verification gate), DB-17 (`HardDeleteAsync`
guarded-reason pattern), **DB-11e (deployed; this doc is planned against the post-DB-11e tree: 80 migrations, newest `20260923205702_DropUsersLegacyDemoColumns`,
`main` @ `5254b8f`)**.

## 1. Goal

A Workspace Admin can today only leave (blocked when sole admin) or erase their own account (S-13 guard); pausing and deleting a workspace are operator-only,
and the operator "disable" does not even pause the workspace — it disables one admin membership (§2). This doc gives the Workspace Admin a **Danger zone**
in Settings with three actions: **Export data** (existing endpoint, surfaced), **Pause workspace** (reversible freeze: no new feedback, CLI apply stops,
dashboard read-only, everyone can still sign in, read and export), and **Delete workspace** — which first offers Export and "Pause instead", then e-mails the
requesting admin a one-time 30-minute link; the link opens a page that shows exactly what will be deleted, offers Export / Pause instead again, and requires
the **password** (plus the typed workspace name). Confirming **schedules** the delete after a grace period (default 7 days) during which the workspace is
frozen, every admin is notified and any admin can cancel; a hosted job then calls the existing `TenantService.HardDeleteAsync(…, "owner_requested")`.
User-visible reason: owners can leave the product cleanly and safely (portability + no accidental or hijacked-account deletion), and are nudged towards the
reversible option.

## 2. Prerequisites (verified facts, 2026-09-24 @ `5254b8f`, 80 migrations)

**Workspace row and filter**
- `Domain/Entity/Workspace.cs:8-39` — `Id, Name, CreatedAt/By, UpdatedAt/By, DeletedAt/By` (`:17-24`) + six DB-17 demo columns (`:26-38`). Not a `BaseEntity`.
- `Infrastructure/Mappings/WorkspaceMapping.cs:11-14` — `b.ToTable("workspaces", t => t.HasCheckConstraint("ck_workspaces_name_not_blank", "length(btrim(name)) > 0"));`
  `:34-44` demo columns + partial index `ix_workspaces_demo_expires_at` (the precedent to copy for the new partial index).
- Filter `Infrastructure/AppDbContext.cs:328-332` (strict-own on `Id`; super admin sees all). `IUnitOfWork.Workspaces` (`Application/Abstractions/IUnitOfWork.cs:12`),
  `ExecuteSqlRawAsync` (`:60`, no-op on InMemory — the `FOR UPDATE` precedent).
- `workspaces.deleted_at` exists but **must not** be used for the grace period: `MembershipService.ListForIdentityAsync :55-71` drops memberships of
  workspaces with `DeletedAt != null`, so members could no longer sign in to export or cancel.

**Operator actions today** (`API/Controllers/Admin/TenantsController.cs`, `[Authorize(Policy = Policies.SuperAdmin)]` `:14`, `[Tags("Tenants")]` `:15`)
- `PATCH {workspaceId:guid}/status` `:104-116` → `TenantService.SetStatusAsync(workspaceId, action)` `TenantService.cs:306-347`: resolves the **current admin
  membership** (`ResolveWorkspaceAdminAsync :291-304`) and flips `membership.IsActive/ApprovalStatus`; `disable` also rotates **that membership's** stamp
  (`:330-333`); audit `tenant.status_changed`, target `Membership` (`:339-345`). Nothing on the workspace changes; other members, API keys and the widget keep working.
- `DELETE {workspaceId:guid}` `:166-175` → `HardDeleteAsync(workspaceId)` (reason `"admin"`).
- `TenantService.HardDeleteAsync(Guid, string reason = "admin")` `:524-713` (`ITenantService.cs:27`): `isDemoExpiredReason` `:533`; pre-check `:539-549`;
  audit builder `:559-574` (`ActorKindOverride: isDemoExpiredReason ? System : null` `:573`); **non-guarded reasons write audit + delete files BEFORE the
  transaction** `:576-588`; transaction `:592`; `FOR UPDATE` + locked re-check `:597-611`; FK-ordered deletes of 23 types; memberships of this workspace deleted, then
  **`usersCreatedHere` = `users.owner_id == ws && DeletedAt == null` (`:641-646`, a legacy `owner_id` read — R8.7 exemption); each is re-homed if it has
  a membership row of *any* state (ended/soft-deleted included, `IgnoreQueryFilters`) in another workspace (`:650-656`, predicate `:654`), else hard-deleted
  (`:668`)** — so an identity *created elsewhere* is never deleted, and the requester's own account disappears only if it was created here and never
  joined another workspace (Opus HIGH 1 — the first draft cited `:620-650` and "only live membership"); guarded reason: audit inside the tx `:684-691`,
  files after commit `:694-704`. `HardDeleteOrder` `:728-753`.
- Dashboard `TenantsPage.tsx:256-267,529-535` (approve / enable / disable via `usePatchApiAdminTenantsWorkspaceIdStatus`).

**Export (the owner's "currently able to be exported")**
- `API/Controllers/ExportImportController.cs` (`[Authorize]`, no `[Tags]` → default tag `ExportImport`, which **is** in `orval.config.ts` `filters.tags`):
  `GET api/projects/{key}/export` `:38-52` and **`GET api/export` `:54-71` (whole workspace)**, both `[Audited(ExportDownloaded)]`, query `ExportQueryParams`
  (`IncludePrivate`, `IncludeDeleted`, `Status`, `Environment` — `Application/DTOs/Export/ExportQueryParams.cs`).
- `ExportImportService.ExportWorkspaceAsync :81-90` (refuses a non-impersonating operator) → `BuildExportFileAsync :130-247`: **comments + live replies +
  element metadata only**; screenshots never included (`ScreenshotUrl = null, ScreenshotOmitted` `:255-257`); private notes of others only with
  `IncludePrivate && IsAdmin` (`:98-100`); **hard cap 5 000 comments** (`MaxExportCommentCount :25`, refusal message `:140-143`); schema `"1.0"` (`:17`).
  Not exported: projects/config, members, roles, AI rules, predefined actions, statuses, settings, branding, screenshots.
- **The dashboard calls only the per-project export** — `pointer-dashboard/react/src/features/projects/ProjectsPage.tsx:36,949-965`
  (`getApiProjectsKeyExport` → blob download). **Nothing calls `GET /api/export`** (grep of `src/` → 0); the generated hook should be `getApiExport` (verify name
  during implementation).

**Scoped e-mail tokens (R16 — reuse, no table)**
- `Application/Abstractions/IResetTokenService.cs:32,39-45` `CreateScoped(publicId, stamp, purpose, payload?)` / `TryValidateScoped(token, purpose, out id, out stamp, out payload)`;
  `Infrastructure/Auth/ResetTokenService.cs:17` TTL **30 min** (const), `:18` purpose `^[a-z-]+$`, `:61-116` six-part HMAC, payload base64url.
- `Application/Common/TokenPurposes.cs:8-18` (`erase`, `change-email`, `verify-email`).
- Pattern to copy: `IdentityEraseService.RequestEraseLinkAsync :89-149` (branding `:106`, `CreateScoped :107-111`, link `{brand.Urls.App}/delete-account?token=` `:112-113`,
  best-effort `SendAsync` in try/catch `:115-128`, audit with `ActorUserIdOverride` `:130-146`) and `EraseByTokenAsync :151-178` (one failure message; stamp compare `:167`).
  Password check precedent `EraseSelfAsync :50-70` (`PasswordlessOnly` → e-mail rail `:64`; `_passwordHasher.Verify` `:67`).
- Anonymous redeem endpoints: `AuthController.cs:182-209` (`confirm-email-change`, `confirm-erase`: `[AllowAnonymous]`, `[Audited]`, `[EnableRateLimiting("signup")]`).
  Dashboard landing precedent `react/src/features/auth/DeleteAccountPage.tsx` (route `App.tsx:58`, reads `?token=`).
- Key-session refusal precedent: `MfaService.cs:78-79` (`currentUser.KeyScopes is not null → Forbidden`). MFA is super-admin-only (`MfaService.cs:81-90`,
  `AuthService.cs:953`) — no workspace admin has TOTP, so no step-up factor exists to require.

**Identity / membership**
- `User.PasswordHash :9`, `SecurityStamp :49`, `IsDemo :52`, `PasswordlessOnly :65`, `Language :33` (`"ar"|"en"|null`, validated `PreferencesService.cs:33`).
- `IMembershipService` (`Application/Services/Interfaces/IMembershipService.cs`): `FindIdentityByPublicIdAsync :19`, `GetMembershipAsync(userId, ws) :22`
  (live = `LeftAt == null && DeletedAt == null`, includes `Role`, `User` — `MembershipService.cs:38-50`), `InWorkspace(ws) :28` (includes `User`, `Role` — `:74-81`),
  `CountLiveAdminsAsync :80`. Role name constant `"Workspace Admin"` (`TenantService.cs:22`). `WorkspaceMembership.IsActive :27`, `ApprovalStatus :30`
  (`Approved = 1`, `Domain/Enums/ApprovalStatus.cs`), `SecurityStamp` (JWT `mstamp`).
- `TenantStamp.TryRequireOwner` (`Application/Common/TenantStamp.cs:16-20`). `ICurrentUser` (`Application/Abstractions/ICurrentUser.cs:5-28`: `Id`, `IsAdmin`,
  `IsSuperAdmin`, `IsQuickAccess`, `TenantId`, `KeyScopes`, `IsImpersonating`). `Policies.Admin` = `is_admin` claim (covers Deputy too — `API/Auth/Policies.cs:5-6`).

**Workspace surface for admins**
- `API/Controllers/Admin/WorkspaceController.cs` `[Route("api/admin/workspace")]` `[Tags("Workspace")]` (in orval) `[Authorize(Policy = Policies.Admin)]` `:16-21`;
  `GET` `:24-32`, `PUT name` `:35-44`. `WorkspaceService.GetAsync :37-62`, `ToResponse :110-118`; `WorkspaceResponse.cs:4-14` (`Id, Name, IsPlaceholderName, CreatedAt, UpdatedAt`).
- Dashboard Settings: `react/src/features/settings/SettingsPage.tsx:985-996` (cards gated `isAdmin && !isSuperAdmin`), `WorkspaceNameCard.tsx`; banners in
  `features/shell/Shell.tsx:195-204` (`ImpersonationBanner`, `VerificationBanner`, `DemoPanel`); header-driven event precedent `react/src/lib/api.ts:36-45,209-221`
  (`x-email-verification-required`); i18n `react/public/assets/i18n/{en,ar}.json`; `components/ConfirmDialog.tsx`.

**Global filters, CORS, hosted jobs, rate limits**
- `API/Program.cs:62-74` — global filters (`AuditCoverageFilter :68`, `ImpersonationRequestCounter :70`, `RequireVerifiedEmailFilter :73`); `:106-117` hosted services;
  CORS `WithExposedHeaders("X-Email-Verification-Required")` `:155` and `:163`.
- `API/Auth/RequireVerifiedEmailFilter.cs:28-116` — the shape to copy (method check, `ControllerActionDescriptor`, attribute opt-out, 60 s `IMemoryCache`, fail-open, header + result).
  `API/Auth/AllowUnverifiedAttribute.cs` (opt-out attribute precedent).
- `API/Hosted/DemoCleanupService.cs:13-140` — 30 s initial delay, `PeriodicTimer`, isolated steps, `internal static SweepOnceAsync(IServiceScopeFactory, ILogger, ct)` with a
  **fresh scope per item** (`:96-110` remarks).
- `API/Extensions/RateLimitingExtensions.cs:71-79` `"signup"` = 5/h/IP (`signupPermitLimit` `:32-35`); fixed-window policy shape `:85-102`.
- `IEmailService.SendAsync(to, subject, html)` (`Application/Services/Interfaces/IEmailService.cs:10`), daily cap 250 (`EmailService.cs:17,26-33`).
  **Every existing e-mail is English-only inline HTML** (e.g. `IdentityEraseService.cs:117-127`, `DemoService`) — the en/ar split below is new.

**Enforcement surfaces**
- Widget writes are all authenticated JWT calls: `CommentsController.cs:12,18-20` (`POST api/projects/{key}/comments`), `RepliesController.cs:18-22`,
  `UploadsController.cs:53`, plus PATCH/PUT/DELETE on comments `:63-126`. Widget boot check: `WidgetPublicController.cs:14-25` (anonymous) →
  `ProjectService.CheckWidgetActiveAsync :1408-1562` (project resolved `:1442`; `Active = true` returns at `:1449-1451` and `:1559-1562`); DTO
  `Application/DTOs/Project/WidgetActivationResponse.cs` (`Active` only). Widget: `web-component/src/element.ts:474-487` (`_checkWidgetActive`),
  `:2182-2225` (comment POST error handling — **409/404 → `disableSilently()`**, 403/429 toasts, else `throw`), `:1049` (same 409 handling on list).
  Widget bundle budget ~1 KB headroom (memory note R4-01).
- CLI: every CLI/MCP session is an **API-key session** (device login returns a raw key, `DeviceLoginService.cs:164`; exchanged via `POST /api/auth/login-with-key`;
  `JwtTokenService.cs:105` adds `key_scopes`). `cli/src/api.ts:7-32` (`ApiError(code, message)`), `cli/src/apply/queue.ts:40,138` (403 fallback), commands in `cli/src/commands/apply.ts`.
- Membership-creating anonymous paths: `InviteService.AcceptJoinExistingWorkspaceAsync :638-654` (DB-17 expired-demo check at `:644-654` — insert next to it),
  `AuthService.RegisterAsync :1330` (owner resolved at `:1359`). Admin-side creation (`UserService.cs:157`, `InviteService.CreateQuickAccessInviteAsync :1055`) is
  behind non-GET admin routes → covered by the filter.
- `AuthController` `GET me` `:132-141`; `MeController` `:16-195` (`[Tags("Me")]` by default); `MfaController` `[Tags("Me")]` `:21-22`; `MetaController`/`BrandingController`
  class-level `[AllowAnonymous]`; `EventsController.cs:17-19` (`POST api/events`, `[Authorize]`).
- `AuthService.MeAsync :1609`; `UserMapper.ToMeResponse(…, Workspace? workspace = null)` `Application/Common/UserMapper.cs:20-57` (DB-17 already passes the
  current workspace from every caller); `MeResponse.cs:41-46` (demo fields — add after them).
- `TenantResponse.cs:30-46`; `TenantService.ListAsync :51-160`.

**Audit**
- `Application/Common/AuditActions.cs:88-98` ("Workspace and tenants" block); `AuditTargets.Workspace` (`AuditTargets.cs:9`); `AuditFields.Allowed` (`AuditFields.cs:13-43`:
  includes `reason, count, expires_at, source, with_password`, **no** `scheduled_for`); `AuditActorKind.System/User`.
- `Tests/AuditCoverageTests.cs` (every non-GET needs `[Audited]`/`[NoAudit]`), `Tests/MigrationSafetyTests.cs:75-82` (risky-op regex: `AddColumn`/`CreateIndex`/
  `AddCheckConstraint` are **not** flagged → no marker).

**Backups / privacy**
- `DEPLOY.md:96-97` local dumps pruned after **14 days**; `:159` off-box copies pruned after **30 days**. `landing/privacy.html:253-260` retention table, `:265-267`
  "hard-delete … only when the whole workspace is deleted".

**Added by the cross-review (verified 2026-09-24 @ `9aaf1d3`)**
- Access-removing admin actions that a freeze must **not** block: `API/Controllers/Admin/UsersController.cs:68-69` (`PATCH {id}` — `UpdateUserRequest { RoleId?, IsActive?, Password? }`,
  `Application/DTOs/User/UpdateUserRequest.cs:3-7`, so the same route also *grants*), `:88-89` (`DELETE {id}`, member removed); `API/Controllers/Admin/InvitesController.cs:41-42`
  (`DELETE {id}` revoke, incl. quick-access links), `:56-57` (`POST {id}/quick-link/rotate`). No admin "revoke API key" route exists (keys die with the membership; `MeController` `api-key/regenerate` `:101`).
- `EventsController.cs:17-19` `POST api/events` **and** `:36-37` `GET api/admin/events/summary` (Admin) share the class — a class-level exemption would cover both.
- Per-identity lockout: `Application/Abstractions/ILoginAttemptLimiter.cs:8-14` (`IsLockedAsync/RecordFailureAsync/ResetAsync(email)`), `Infrastructure/Auth/LoginAttemptLimiter.cs` (R5-59).
- `IUnitOfWork.ExecuteSqlRawAsync` returns `Task` (no affected-row count) — conditional `UPDATE … WHERE` cannot report whether it claimed a row; use lock + server-side re-check instead.
- Query filters hide every workspace row from a context without a tenant (`AppDbContext.cs:88-90` comment, `:328-332`); jobs and anonymous paths must `IgnoreQueryFilters()` + explicit `Id`/`OwnerId` predicate (precedent `DemoCleanupService.cs:115-119`).
- CORS split: `API/Program.cs:434-447` `IsDashboardOnly` (open CORS for everything else, incl. every `/api/auth/*` not listed).
- Rate-limit config precedent: `RateLimitingExtensions.cs:32-35` reads `Security:RateLimits:SignupPerHour`.
- CI-facing CLI: `cli/src/commands/deployed.ts:57` (`GET …/comments?status=3`), `:74-80` (`POST …/builds`), `:113-140` (exit codes); `cli/src/mcp/tools.ts:4,300,358` (ApiError handling, no `process.exit`);
  `ProjectBuildsController.cs:22-23`.
- Widget: every JWT call goes through `api()` `element.ts:967`; the screenshot upload uses `pfFetch` directly `:1786-1792` **before** the comment POST (`!r.ok → throw`).
- `AuditCoverageFilter.cs:51` (`Audit:StrictCoverage` → 500 when an `[Audited]` action writes no row).
- `UserNameResolver` is a **static** class taking `IUnitOfWork` (`UserNameResolver.cs:14-29`) — no constructor dependency.
- `AuthService.LoginWithApiKeyAsync` builds `ToMeResponse(user, role, tenantName)` without the workspace (`:1325`); `LoginWithInviteAsync` likewise (`~:1716-1720`).
- `RetentionService.cs:167-183` keeps sweeping (usage events, read notifications, snapshots, invites, screenshot purge) regardless of workspace state.

**Tooling**: `just migrate name="…"` (`justfile:6` = `dotnet ef migrations add {{name}} -p Infrastructure -s API`), `just test`, `just fmt`;
`dotnet ef migrations list -p Infrastructure -s API --no-connect`; local gate `scripts/local-e2e-gate.sh`; Mailpit helpers `e2e/scripts/lib/mail.mjs`
(`clear`, `awaitMessage({to, subjectIncludes})`, `extractLink(html, pathPrefix)`), precedent spec `e2e/mail/tenant-invite-mail.spec.mjs`.

## 3. Design

### 3.1 States (one row, three independent facts)

| State | Predicate on `workspaces` | Meaning |
|---|---|---|
| **Active** | `paused_at IS NULL AND deletion_scheduled_for IS NULL` | normal |
| **Paused (self)** | `paused_at IS NOT NULL AND NOT paused_by_operator` | frozen; any live Workspace Admin (or operator) resumes |
| **Paused (operator)** | `paused_at IS NOT NULL AND paused_by_operator` | frozen; **only the operator** resumes; self-service delete refused (D18.6) |
| **Deletion pending e-mail** | `deletion_requested_at IS NOT NULL AND deletion_scheduled_for IS NULL` | a link *may* be outstanding (valid ≤ 30 min after `deletion_requested_at`); **not** frozen |
| **Deletion scheduled** | `deletion_scheduled_for IS NOT NULL` | frozen; any live Workspace Admin (or operator) cancels; job deletes at `deletion_scheduled_for` |

**Frozen** ⇔ `paused_at IS NOT NULL OR deletion_scheduled_for IS NOT NULL`. Pause and schedule are independent: cancelling a deletion restores whatever
pause state existed before (a workspace that was paused before the delete stays paused after cancel).

### 3.2 Schema — Migration `AddWorkspacesPauseAndDeletionState` (R1, one migration, nothing else)

| property | column | type | null / default | meaning |
|---|---|---|---|---|
| `PausedAt` | `paused_at` | `timestamptz` | null | non-null ⇔ paused |
| `PausedBy` | `paused_by` | `uuid` | null | `users.public_id` of who paused (R14 content reference, **no FK**; operator's id when operator-paused) |
| `PausedByOperator` | `paused_by_operator` | `boolean` | **NOT NULL DEFAULT false** | true ⇔ the operator paused; admins cannot resume |
| `DeletionRequestedAt` | `deletion_requested_at` | `timestamptz` | null | time of the newest e-mailed request; **the token binds this value** (a newer request or a cancel voids older links) |
| `DeletionRequestedBy` | `deletion_requested_by` | `uuid` | null | requester's `public_id` (no FK) |
| `DeletionConfirmedAt` | `deletion_confirmed_at` | `timestamptz` | null | when the link + password were accepted |
| `DeletionScheduledFor` | `deletion_scheduled_for` | `timestamptz` | null | non-null ⇔ deletion scheduled; the job's predicate |
| `DeletionReminderSentAt` | `deletion_reminder_sent_at` | `timestamptz` | null | T-24 h reminder attempted (idempotency, DB-17 `demo_expiry_warned_at` precedent) |

Index: `ix_workspaces_deletion_scheduled_for (deletion_scheduled_for) WHERE deletion_scheduled_for IS NOT NULL` — plain `CreateIndex` (R4: a handful of rows).

Check constraints (all states in §3.1 satisfy them; every write path in §3.4 sets/clears the columns in these groups):
- `ck_workspaces_pause_consistent`: `(paused_at IS NULL) = (paused_by IS NULL) AND (paused_at IS NOT NULL OR NOT paused_by_operator)`
- `ck_workspaces_deletion_request_consistent`: `(deletion_requested_at IS NULL) = (deletion_requested_by IS NULL)`
- `ck_workspaces_deletion_schedule_consistent`: `(deletion_confirmed_at IS NULL) = (deletion_scheduled_for IS NULL) AND (deletion_confirmed_at IS NULL OR deletion_requested_at IS NOT NULL) AND (deletion_reminder_sent_at IS NULL OR deletion_scheduled_for IS NOT NULL)`

Expected migration operations (EF may order them differently; the **set** must match exactly): 8 × `AddColumn` on `workspaces` (only `paused_by_operator`
with `defaultValue: false, nullable: false`), 1 × `CreateIndex` with `filter: "deletion_scheduled_for IS NOT NULL"`, 3 × `AddCheckConstraint`. Any operation on
another table, any `Sql(`, any `AlterColumn` → stop and report (R13). `Down()` = generated (drop constraints, index, columns). No marker, no `[ContractMigration]`.

**Every existing row:** gets `NULL` in seven columns and `false` in `paused_by_operator` (PG15 `ADD COLUMN … DEFAULT false` is metadata-only, no rewrite).
No row changes state; no backfill.

Doc-comment block on `Workspace` (verbatim, above `PausedAt`):
```csharp
/// <summary>
/// DB-18. Workspace lifecycle. Frozen (read-only for members, widget and CLI) ⇔ PausedAt != null || DeletionScheduledFor != null
/// (enforced by API/Auth/WorkspaceFrozenFilter via IWorkspaceStateService). PausedByOperator = only the operator may resume.
/// DeletionRequestedAt = newest e-mailed request (the scoped token binds it; a newer request or a cancel voids older links);
/// DeletionScheduledFor = confirmed with password, deleted by WorkspaceDeletionService via TenantService.HardDeleteAsync(id, "owner_requested").
/// DeletedAt is NOT used for the grace period (members must still sign in to export or cancel). PausedBy/DeletionRequestedBy are
/// users.public_id content references (R14, no FK). Check constraints: DB-18 §3.2.
/// </summary>
```

### 3.3 One source of truth for "frozen": `IWorkspaceStateService`

New `Application/Services/Interfaces/IWorkspaceStateService.cs` + `Implementation/WorkspaceStateService.cs` (Scrutor picks it up like the other services — verify during implementation):
```csharp
public sealed record WorkspaceFreeze(bool IsFrozen, bool IsPaused, bool PausedByOperator, DateTime? DeletionScheduledFor);
public interface IWorkspaceStateService
{
    /// <summary>DB-18 §3.3. Cached 30 s per workspace (IMemoryCache key "wsfrozen:{id:N}"); IgnoreQueryFilters + AsNoTracking +
    /// explicit `w.Id == workspaceId && w.DeletedAt == null` (Gemini LOW #2); a missing or soft-deleted row → not frozen.</summary>
    Task<WorkspaceFreeze> GetAsync(Guid workspaceId);
    /// <summary>Called by every write in §3.4 right after its SaveChangesAsync.</summary>
    void Invalidate(Guid workspaceId);
}
```
Readers: `WorkspaceFrozenFilter` (§3.5), `ProjectService.CheckWidgetActiveAsync` (§3.6), `InviteService.AcceptJoinExistingWorkspaceAsync`, `AuthService.RegisterAsync` (§3.6).
Single API instance on one VM → in-process cache invalidation is sufficient; worst case after a crash is ≤ 30 s staleness.

### 3.4 Service — `IWorkspaceLifecycleService` / `WorkspaceLifecycleService` (new, Application)

Constructor: `IUnitOfWork, ICurrentUser, IMembershipService, IPasswordHasher, IResetTokenService, IEmailService, IBrandingService, IWorkspaceStateService,
IWorkspaceService, ITenantService, ILoginAttemptLimiter, IConfiguration? config = null, IAuditWriter? audit = null` (optional last two keep test harnesses simple —
DB-17 precedent; `UserNameResolver` is static, no injection).
Config: `WorkspaceDeletion:GraceDays` (int, default **7**, **clamp 0..14** — Opus NIT: keeps "grace + 30 d backups" ≤ 44 d and the privacy text is derived from
the configured value) and `WorkspaceDeletion:SweepMinutes` (int, default 15, clamp 1..60; e2e sets 1).

**Query rule for every non-session path (Opus MEDIUM):** the job methods (`SendDueRemindersAsync`, due-id query, `ExecuteDueDeletionAsync`), the anonymous
token methods (`ValidateTokenAsync` workspace load, preview counts) and the operator methods run without a usable tenant claim, so every `Workspaces`,
`Comments`, `Projects`, `WorkspaceMemberships` and `Users` read in them is `IgnoreQueryFilters()` **with an explicit `Id == ws` / `OwnerId == ws` predicate**
(precedent `DemoCleanupService.cs:115-119`); §6 tests 9 and 4 run them with a no-tenant `FakeCurrentUser`.

**Concurrency rule (Opus MEDIUM ×2, Gemini LOW #1):** every state transition that can race another (confirm, pause-instead, cancel, operator cancel, reminder
claim) runs inside `_unitOfWork.ExecuteInTransactionAsync`, starts with `ExecuteSqlRawAsync("SELECT id FROM workspaces WHERE id = {0} FOR UPDATE", ws)`,
**then loads the row it will modify for the first time** (never reuse an entity tracked before the lock — EF identity resolution would hand back the stale
instance) or, if one is already tracked, `await _unitOfWork.Entry(w).ReloadAsync()` (add a thin `ReloadAsync(object)` to `IUnitOfWork` if absent — verify
during implementation); re-evaluates its preconditions on that fresh row; if the row is gone → `Conflict(Workspace.AlreadyDeleted)`. `DbUpdateConcurrencyException`
from the save is caught and mapped to `Conflict(Workspace.StateChanged)` — never a 500.

**Common guard `RequireLifecycleAdminAsync()`** (used by every session method): `KeyScopes != null → Forbidden(Workspace.KeySessionCannotManage)`;
`IsQuickAccess || IsSuperAdmin || IsImpersonating → Forbidden(Common.Forbidden)`; `TenantStamp.TryRequireOwner(_currentUser, out ws)` else Forbidden (R16);
identity = `FindIdentityByPublicIdAsync(_currentUser.Id)`; membership = `GetMembershipAsync(identity.Id, ws)` must be live, `IsActive`, `ApprovalStatus == Approved`,
`Role.Name == "Workspace Admin"` (**Deputy excluded** — owner wording "workspace admin") else `Forbidden(Workspace.AdminOnly)`; workspace tracked load
`Workspaces.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == ws && w.DeletedAt == null)` else NotFound; **live, unconverted demo**
(`DemoExpiresAt != null && DemoConvertedAt == null` — Opus LOW: a converted workspace in the `Demo:ConvertRequiresVerification` 72 h window still has a TTL,
`Workspace.cs:26-28`, and is a real workspace) → `Failure(Workspace.DemoCannotPauseOrDelete)` (D18.9).

| Method | Preconditions (after the guard) | Writes (one `SaveChangesAsync`, then `_state.Invalidate(ws)`) | Audit (after save, R17) | E-mail |
|---|---|---|---|---|
| `PauseAsync()` | not already paused → else `Conflict(Workspace.AlreadyPaused)`; not scheduled → else `Conflict(Workspace.DeletionAlreadyScheduled)` | `PausedAt = now, PausedBy = caller, PausedByOperator = false, UpdatedAt/By` | `workspace.paused`, target Workspace, `After { source = "admin" }` | none |
| `ResumeAsync()` | paused → else `Conflict(Workspace.NotPaused)`; `!PausedByOperator` → else `Forbidden(Workspace.PausedByOperator)`; not scheduled → else `Conflict(Workspace.CancelDeletionFirst)` | pause columns → `NULL/NULL/false` | `workspace.resumed`, `After { source = "admin" }` | none |
| `RequestDeletionAsync()` | not scheduled → else Conflict; `!PausedByOperator` → else `Forbidden(Workspace.PausedByOperator)`; cooldown: `DeletionRequestedAt > now - 5 min` → `Failure(Workspace.DeletionEmailJustSent)`; **per-workspace daily cap** (Opus MEDIUM — the global cap is 250/day, `EmailService.cs:17,26-33`): `AuditEvents.IgnoreQueryFilters().Count(e => e.OwnerId == ws && e.Action == "workspace.deletion_requested" && e.CreatedAt > now - 24h) >= 5` → `Failure(Workspace.DeletionDailyLimit)` (column names of `AuditEvent` — verify during implementation) | `DeletionRequestedAt = TruncateToMs(now)`, `DeletionRequestedBy = caller` | `workspace.deletion_requested`, `After { expires_at = now+30min:O }` | **E1 to the requester only** (D18.2) |
| `CancelDeletionAsync()` | locked tx (concurrency rule); scheduled **or** a pending request (`DeletionRequestedAt != null`) → else `Conflict(Workspace.NoDeletionScheduled)`; row gone → `Conflict(Workspace.AlreadyDeleted)` | the four deletion columns + reminder → NULL (pause columns untouched). Clearing a *pending* request voids the outstanding link (Opus LOW "no way to void a link") | `workspace.deletion_cancelled`, `After { source = "admin" }` (scheduled) or `{ source = "admin_pending" }` (pending only) | **E5 to all live admins** only when a schedule was cancelled |
| `PreviewDeletionAsync(token)` *(anonymous)* | `ValidateTokenAsync` (below) | none | `[NoAudit]` | none |
| `ConfirmDeletionAsync(req)` *(anonymous)* | validator first (`ConfirmWorkspaceDeletionRequestValidator`: `Token` required, `WorkspaceName` required ≤ 120, `Password` ≤ 128); `ValidateTokenAsync`; `!PausedByOperator` else `Forbidden(Workspace.PausedByOperator)`; name: `NormalizeName(req.WorkspaceName) == NormalizeName(w.Name)` where `NormalizeName = s.Normalize(FormC)` with U+200E, U+200F, U+061C, U+200B removed, then `Trim()`, ordinal compare, else `Failure(Workspace.DeletionNameMismatch)`; if `!identity.PasswordlessOnly`: **lockout** (Opus MEDIUM, R5-59) `await _lockout.IsLockedAsync(identity.Email)` → `Failure(Auth lockout message)`; `string.IsNullOrEmpty(req.Password) \|\| !_passwordHasher.Verify(req.Password, identity.PasswordHash)` → `_lockout.RecordFailureAsync(identity.Email)` and `Failure(User.CurrentPasswordIncorrect)`; after the **5th** failure on this link the link is spent (`DeletionRequestedAt/By = NULL`, locked tx) (D18.5) | locked tx (concurrency rule): lock, **fresh** load, re-run the token *state* checks on the fresh row (a concurrent confirm/pause-instead/cancel makes this one fail with `DeletionLinkInvalid`), then `DeletionConfirmedAt = now`, `DeletionScheduledFor = now + GraceDays`, `DeletionReminderSentAt = null`; success → `_lockout.ResetAsync(identity.Email)` | `workspace.deletion_confirmed`, `ActorUserIdOverride = identity.PublicId`, `ActorKindOverride = User`, `After { scheduled_for = …:O, with_password = "true"/"false" }` | **E2 to all live admins** (incl. requester) |
| `PauseInsteadAsync(token)` *(anonymous)* | `ValidateTokenAsync`; locked tx with fresh load (a racing confirm wins or loses cleanly — no check-constraint 500) | if not paused: pause columns (`PausedBy = identity.PublicId`, `PausedByOperator = false`); **always** `DeletionRequestedAt/By = NULL` (spends the link) | **exactly one row** (Opus LOW, `AuditCoverageFilter.cs:51`): `workspace.paused { source = "delete_link" }` if it paused, else `workspace.deletion_cancelled { source = "delete_link" }`; actor override as above | none |
| `OperatorPauseAsync(ws)` / `OperatorResumeAsync(ws)` / `OperatorCancelDeletionAsync(ws)` | `_currentUser.IsSuperAdmin && !IsImpersonating` else Forbidden; workspace live else NotFound; cancel uses the locked tx | pause: `PausedAt = now, PausedBy = operator, PausedByOperator = true` (overrides a self-pause; **an operator pause during the grace period holds the deletion** — §3.4 job rows); resume: clear pause columns — **documented behaviour: operator resume also clears an earlier admin self-pause** (the admin can pause again; Opus NIT); cancel: as `CancelDeletionAsync` | same actions, `After { source = "operator" }` (actor kind SuperAdmin is automatic; R17 redaction applies in the workspace audit view) | cancel: E5 (sender name shown as "the platform operator") |
| `SendDueRemindersAsync(now)` *(job)* | candidates (no-tenant query rule): `DeletedAt == null && DeletionScheduledFor != null && DeletionScheduledFor <= now + 24h && DeletionReminderSentAt == null && DeletionScheduledFor - DeletionConfirmedAt >= 48h` (**grace < 2 d: no reminder** — E2 is the notice; Opus NIT). Per row, locked tx + fresh load + same predicate (Opus LOW: a racing cancel would otherwise leave `reminder_sent_at` on an unscheduled row and violate `ck_workspaces_deletion_schedule_consistent`) | stamp `DeletionReminderSentAt = now` (whether or not the send succeeds); **missed reminder** (Opus LOW, downtime across T-24 h): if the fresh row is already due (`DeletionScheduledFor <= now + 1 h`), also set `DeletionScheduledFor = now + 24h` | missed-reminder reschedule only: `workspace.deletion_rescheduled`, System, `After { scheduled_for, reason = "reminder_missed" }` | **E3 to all live admins** (after commit) |
| `ExecuteDueDeletionAsync(ws)` *(job, per item)* | re-check `DeletionScheduledFor != null && <= now && !PausedByOperator && (DeletionReminderSentAt != null \|\| DeletionScheduledFor - DeletionConfirmedAt < 48h)` (**operator pause holds the delete** — Opus HIGH 3; log `WorkspaceDeletionService: {Id} held by operator pause`) | capture admin recipients as an **`AsNoTracking` projection** `(Email, Language, DisplayName)` before the delete (Opus NIT — nothing tracked in the context `HardDeleteAsync` uses), then `ITenantService.HardDeleteAsync(ws, "owner_requested")` | `tenant.hard_deleted` (existing; System actor, `reason = owner_requested`) | **E4 to the captured admins** after success |

"All live admins of W" = `_memberships.InWorkspace(W).Where(m => m.LeftAt == null && m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved && m.Role.Name == "Workspace Admin" && m.User.DeletedAt == null && !m.User.IsDemo)` (R8.7 — never `users.owner_id`).
All e-mails are best-effort (`try { … } catch { }` like `IdentityEraseService.cs:115-128`) and are sent **after** the commit; a failed send never rolls back state.
Operator methods also write `UpdatedAt/UpdatedBy`.

**Who is deleted with the workspace — one query (Opus HIGH 1).** New `public static IQueryable<User> IdentitiesDeletedWithWorkspace(IUnitOfWork uow, Guid ws)` on
`TenantService` (a legacy `users.owner_id` read, R8.7 exemption documented in its doc-comment exactly as `HardDeleteAsync :636-640` does):
`uow.Repository<User>().Query().IgnoreQueryFilters().Where(u => u.OwnerId == ws && u.DeletedAt == null && !uow.Repository<WorkspaceMembership>().Query().IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.OwnerId != ws))`.
`HardDeleteAsync :641-670` is refactored to use it for the delete set (the re-home set stays `usersCreatedHere` minus that set) — behaviour-preserving; the
preview's `AccountsDeletedWithWorkspace` (**an `int` count only** — no names, no e-mails) and `RequesterAccountDeleted` (`Any(u => u.Id == identity.Id)`) use the
same query; §6 test 9 asserts preview count == rows actually deleted.

**Token (R16 reuse, purpose `TokenPurposes.DeleteWorkspace = "delete-workspace"`)** —
`_resetTokens.CreateScoped(identity.PublicId, identity.SecurityStamp, TokenPurposes.DeleteWorkspace, payload)` with
`payload = $"{ws:N}|{membership.SecurityStamp:N}|{new DateTimeOffset(requestedAt).ToUnixTimeMilliseconds()}"`.
`requestedAt` is truncated to whole milliseconds **before** it is saved (`new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc)`) —
Postgres stores microseconds, .NET ticks are 100 ns; comparing untruncated values would fail after the round-trip.
Link: `{brand.Urls.App.TrimEnd('/')}/confirm-workspace-deletion?token={Uri.EscapeDataString(token)}`.

`ValidateTokenAsync(token)` → `(User identity, WorkspaceMembership m, Workspace w)` or **one** failure `Failure(Workspace.DeletionLinkInvalid)` for every one of:
signature/expiry/purpose (`TryValidateScoped`); payload not three `|` parts or unparsable; identity missing, `identity.SecurityStamp != stamp` (**password
change/reset, e-mail change, erase ⇒ link dead**); workspace missing or `DeletedAt != null`; `w.DeletionRequestedAt` null or its ms ≠ payload ms (**newer request
or cancel ⇒ dead**); `w.DeletionRequestedBy != identity.PublicId`; `w.DeletionScheduledFor != null` (**already confirmed ⇒ single-use**); membership
(`GetMembershipAsync(identity.Id, ws)`) not live / inactive / not approved / not `Workspace Admin`, or `m.SecurityStamp` ≠ payload stamp (**demoted, disabled,
removed or left ⇒ dead**); live demo. Consequence: no database row for the token, no new table (R16: "a new purpose is a new constant, not a new table");
single-use comes from state (`deletion_scheduled_for` set, or `deletion_requested_at` cleared by pause-instead/cancel).
Confirm does **not** rotate the identity or membership stamp (R16: workspace-scoped event; the admin must stay signed in to export or cancel during the grace
period). At execution every membership is deleted → sessions die within the 60 s validator window.

**What happens after confirmation if the requester leaves, is demoted or erases their account:** nothing changes — the schedule is a workspace state; every
other admin has already received E2 and can cancel. (Leave/erase are still blocked when the requester is the sole admin — S-13.)

`WorkspaceDeletionPreviewResponse` (new, `Application/DTOs/Workspace/`): `WorkspaceId`, `WorkspaceName`, `ProjectCount`, `CommentCount` (live), `MemberCount`
(live memberships), `AccountsDeletedWithWorkspace` (int — `IdentitiesDeletedWithWorkspace(ws).Count()`, the exact `HardDeleteAsync :641-670` rule),
`RequesterAccountDeleted` (bool, same query for the token's identity), `RequiresPassword` (= `!identity.PasswordlessOnly`), `GraceDays`,
`WouldBeDeletedOn` (= now + grace), `IsPaused`. Counts only — no content (R18).
`ConfirmWorkspaceDeletionRequest { Token, Password?, WorkspaceName }`; `WorkspaceDeletionTokenRequest { Token }`;
`WorkspaceDeletionRequestResponse { ExpiresAt }`; confirm returns `WorkspaceDeletionScheduledResponse { WorkspaceId, DeletionScheduledFor }`.

`WorkspaceResponse` gains: `PausedAt`, `PausedByOperator`, `PausedByName` (null when `PausedByOperator` — R17 redaction; else `UserNameResolver.ResolveAsync`),
`DeletionRequestedAt`, `DeletionScheduledFor`, `DeletionRequestedByName`, `CanManageLifecycle` (the §3.4 guard evaluated without side effects),
`GraceDays`. Session lifecycle methods return `Result<WorkspaceResponse>` by calling `IWorkspaceService.GetAsync()` after their save.
`MeResponse` gains `WorkspacePausedAt`, `WorkspacePausedByOperator`, `WorkspaceDeletionScheduledFor` (filled in `UserMapper.ToMeResponse` from the
`workspace` argument it already receives). `TenantResponse` gains `PausedAt`, `PausedByOperator`, `DeletionScheduledFor` (`TenantService.ListAsync` mapping).

### 3.5 Enforcement — `API/Auth/WorkspaceFrozenFilter.cs` (global, registered after `RequireVerifiedEmailFilter`, `Program.cs:73`)

Copy the shape of `RequireVerifiedEmailFilter.cs:28-116`. Decision order:
1. not authenticated, or the endpoint carries `IAllowAnonymous` metadata → `next()`.
2. `current.IsSuperAdmin` (plain or impersonating — impersonation writes are fenced already) → `next()`.
3. `current.TenantId is not Guid t` → `next()`.
4. `attr = method ?? controller [AllowWhenWorkspacePaused]` (new `API/Auth/AllowWhenWorkspacePausedAttribute.cs`, `AttributeTargets.Class | Method`,
   property `bool AllowKeySessions { get; init; }`). `isKey = current.KeyScopes != null`.
5. not a key session: GET/HEAD/OPTIONS → `next()`; `attr != null` → `next()`.
   key session: `attr?.AllowKeySessions == true` → `next()`; **otherwise every method is checked (reads too — D18.4)**.
6. `state = await IWorkspaceStateService.GetAsync(t)` (exception → log warning, `next()` — fail-open like `RequireVerifiedEmailFilter.cs:98-103`); not frozen → `next()`.
7. Refuse: header `X-Workspace-Paused: true`, `ctx.Result = new ObjectResult(Result.Failure(msg)) { StatusCode = 423 }` with
   `msg = state.DeletionScheduledFor != null ? Workspace.DeletionScheduledReadOnly : state.PausedByOperator ? Workspace.PausedByOperator : Workspace.Paused`.
   **423 Locked** is chosen deliberately: the widget treats 409/404 as "project disabled → tear down silently" (`element.ts:1049,2188`), the dashboard treats
   409 as conflict and 403 as permission/verification; 423 is unused anywhere today (verify with `grep -rn "423" API web-component/src cli/src react/src`).

`[AllowWhenWorkspacePaused]` placements (and **only** these — the coverage test in §6 pins the list):
- class level: `AuthController` (login, MFA verify, switch workspace, device approve/deny, …), `MeController` (password, e-mail, preferences, notification
  read-state, leave, erase, api-key regenerate — personal, not workspace content), `MfaController`;
- method level **with `AllowKeySessions = true`**: `EventsController.RecordEvent` (`POST api/events`, `:17-19` — telemetry; **not** class level, which would also
  open `GET api/admin/events/summary` to key sessions — Opus LOW);
- method level, **access-removing actions only** (Opus HIGH 2 — a freeze must never stop an admin from locking someone out): `UsersController` `PATCH {id}` (`:68`)
  and `DELETE {id}` (`:88`), `InvitesController` `DELETE {id}` (`:41`) and `POST {id}/quick-link/rotate` (`:56`). Because `PATCH users/{id}` can also *grant*,
  `UserService`'s update method (verify name during implementation) refuses while frozen any request with `Password != null`, `IsActive == true`, or a `RoleId`
  whose role has `IsAdmin == true` → `Forbidden(Workspace.FrozenNoAccessGrant)`; disabling, demoting to a non-admin role, removing, revoking and rotating pass.
  D18.11 still refuses every *addition* (`POST users`, `POST invites`, approvals stay blocked by the filter);
- method level **with `AllowKeySessions = true`**: `AuthController.Me` (`GET api/auth/me`, so `pointer whoami`/`apply` can read the state and explain it);
- method level: `WorkspaceController.Resume`, `.RequestDeletion` (allowed while *self*-paused; the service refuses the other cases), `.CancelDeletion`.
- `ExportImportController` needs nothing: exports are GET (allowed for non-key sessions); imports are refused while frozen, intentionally.
CORS: add `"X-Workspace-Paused"` to both `WithExposedHeaders(...)` calls (`Program.cs:155,163`) as a second argument; add
`path.StartsWithSegments("/api/auth/workspace-deletion", StringComparison.OrdinalIgnoreCase)` to `IsDashboardOnly` (`Program.cs:434-447`) — the anonymous token
endpoints are dashboard-only and must not be callable cross-origin from arbitrary sites (Opus LOW).

**Paths that do not go through MVC filters or are anonymous, handled in services:**
- `InviteService.AcceptJoinExistingWorkspaceAsync` — right after the DB-17 demo check (`:644-654`): `if ((await _workspaceState.GetAsync(ownerId)).IsFrozen) return Result<LoginResponse>.Failure(MessageKeys.Workspace.FrozenNoNewMembers);`
- `AuthService.RegisterAsync` — right after `projectOwnerId` is resolved (`:1359-1360`): same check → `Result.Failure(MessageKeys.Workspace.FrozenNoNewMembers)`.
- Login, switch-workspace, quick-access login, key login: **not** refused (members must be able to sign in, read, export, cancel — D18.3); the frozen state
  reaches clients through `MeResponse` and the 423s.
- Hosted jobs: `RetentionService`, `ScreenshotPurge`, `UsageRollup`, `ImpersonationSweepService` — unchanged (policy, not user action). `DemoCleanupService` —
  unchanged: an operator-paused demo still expires at its TTL (self-service pause/delete is refused for live demos).

### 3.6 Widget, CLI and dashboard behaviour (server side)

- `WidgetActivationResponse` gains `public bool Paused { get; set; }` (doc: "DB-18: the project's workspace is frozen — render read-only").
  `ProjectService.CheckWidgetActiveAsync`: after `var project = projectMatches[0];` (`:1442`) compute
  `var paused = project.OwnerId is Guid o && (await _workspaceState.GetAsync(o)).IsFrozen;` and set `Paused = paused` on the two responses that can be
  `Active = true` (`:1449-1451`, `:1559-1562`). `Active` semantics unchanged (D18.3: reads stay available). `ProjectService` constructor gains
  `IWorkspaceStateService? workspaceState = null` (optional, last).
- Widget reads (`GET …/comments`, capture-config) keep working for JWT sessions; every write gets 423 (§3.5).
- CLI/MCP (key sessions): everything but `GET /api/auth/me` and `POST /api/events` gets 423 while frozen — so even an **old** CLI stops at the queue fetch,
  before any file is edited. **`pointer deployed` runs in customers' CI** (`deployed.ts:57,74,113-140`): a 423 there must print a warning and **exit 0** (a paused
  Pointer workspace must never fail someone's deploy pipeline — Opus MEDIUM); exit 2 is reserved for `apply` / `apply --plan` / `apply --mark`. MCP tools
  (`cli/src/mcp/tools.ts`) return a tool error carrying the server message and never `process.exit`.
- `LoginWithApiKeyAsync :1325` and `LoginWithInviteAsync ~:1718` pass the loaded workspace to `UserMapper.ToMeResponse` so their `LoginResponse.User` carries the
  freeze fields too (Opus LOW).
- Hosted retention/purge jobs keep running for frozen workspaces (policy, not user action); therefore the UI copy says pause **keeps your projects, comments and
  settings**, not "keeps everything" (Opus NIT).

### 3.7 E-mail templates (en + ar, branding-aware)

Built by a new `Application/Common/WorkspaceLifecycleEmails.cs` (static): `(string Subject, string Html) Build(kind, lang, model)`; `lang = recipient.Language == "ar" ? "ar" : "en"`;
`productName = brand.ProductName`, `app = brand.Urls.App.TrimEnd('/')` from `_branding.BuildResponseAsync("", new HashSet<string>())` (`IdentityEraseService.cs:106`
precedent). Wrapper copies `IdentityEraseService.cs:120-126` styling; the Arabic body is wrapped `<div dir="rtl" lang="ar" style="…;text-align:right">`.
**HTML-encode** workspace names and display names (`System.Net.WebUtility.HtmlEncode`). Dates `yyyy-MM-dd HH:mm` + " UTC". `{backupDays}` = 30 (`DEPLOY.md:159`).
Texts (verbatim; `**x**` = `<strong>`, `[x]` = the link):

| # | Subject en / ar | Body en | Body ar |
|---|---|---|---|
| E1 | Confirm deleting the {workspace} workspace / تأكيد حذف مساحة العمل {workspace} | You asked to delete the workspace **{workspace}** on {productName}. To confirm, open the link below and enter your password. The link expires in 30 minutes and works once. [Review and confirm deletion →] Before you delete: you can export your comments from Settings → Danger zone → Export data, or pause the workspace instead — pausing keeps everything and stops new feedback until you resume. After you confirm, the workspace is deleted after {graceDays} days; until then any workspace admin can cancel. If you did not ask for this, ignore this e-mail — nothing happens — and consider changing your password. | طلبتَ حذف مساحة العمل **{workspace}** على {productName}. للتأكيد، افتح الرابط أدناه وأدخل كلمة المرور. تنتهي صلاحية الرابط خلال 30 دقيقة ويعمل مرة واحدة فقط. [مراجعة الحذف وتأكيده ←] قبل الحذف: يمكنك تصدير التعليقات من الإعدادات ← منطقة الخطر ← تصدير البيانات، أو إيقاف مساحة العمل مؤقتًا بدلًا من حذفها — الإيقاف المؤقت يحتفظ بكل شيء ويوقف استقبال الملاحظات الجديدة حتى تستأنفها. بعد التأكيد تُحذف مساحة العمل بعد {graceDays} أيام، ويمكن لأي مسؤول في مساحة العمل إلغاء الحذف قبل ذلك. إذا لم تطلب ذلك فتجاهل هذه الرسالة ولن يحدث شيء، وننصحك بتغيير كلمة المرور. |
| E2 | {workspace} will be deleted on {date} / ستُحذف مساحة العمل {workspace} في {date} | {name} confirmed deleting the workspace **{workspace}**. It will be permanently deleted on {date} UTC — projects, comments, replies, screenshots, settings, API keys, and the accounts that were created in this workspace and belong to no other workspace. Until then the workspace is read-only. To keep it, open {app}/settings and choose **Cancel deletion**. To keep a copy, choose **Export data**. | أكّد {name} حذف مساحة العمل **{workspace}**. ستُحذف نهائيًا في {date} (UTC)، بما في ذلك المشاريع والتعليقات والردود ولقطات الشاشة والإعدادات ومفاتيح API والحسابات التي أُنشئت في مساحة العمل هذه ولا تنتمي إلى أي مساحة عمل أخرى. حتى ذلك الحين تكون مساحة العمل للقراءة فقط. للاحتفاظ بها افتح {app}/settings واختر **إلغاء الحذف**، وللاحتفاظ بنسخة اختر **تصدير البيانات**. |
| E3 | {workspace} will be deleted in 24 hours / ستُحذف مساحة العمل {workspace} خلال 24 ساعة | Reminder: the workspace **{workspace}** will be permanently deleted on {date} UTC. To keep it, open {app}/settings and choose **Cancel deletion**. Export your data before then if you want a copy. | تذكير: ستُحذف مساحة العمل **{workspace}** نهائيًا في {date} (UTC). للاحتفاظ بها افتح {app}/settings واختر **إلغاء الحذف**. صدّر بياناتك قبل ذلك إذا أردت الاحتفاظ بنسخة. |
| E4 | The {workspace} workspace has been deleted / حُذفت مساحة العمل {workspace} | The workspace **{workspace}** was permanently deleted on {date} UTC at the request of one of its admins. Remaining copies in our backups expire within {backupDays} days. This cannot be undone. | حُذفت مساحة العمل **{workspace}** نهائيًا في {date} (UTC) بناءً على طلب أحد مسؤوليها. تنتهي صلاحية النسخ المتبقية في نسخنا الاحتياطية خلال {backupDays} يومًا. لا يمكن التراجع عن هذا الإجراء. |
| E5 | Deletion of {workspace} was cancelled / أُلغي حذف مساحة العمل {workspace} | {name} cancelled the scheduled deletion of **{workspace}**. Nothing was deleted. *(if still paused:)* The workspace is still paused; an admin can resume it from Settings. | ألغى {name} الحذف المجدول لمساحة العمل **{workspace}**. لم يُحذف أي شيء. *(إن كانت لا تزال موقوفة:)* ما زالت مساحة العمل موقوفة مؤقتًا، ويمكن لأي مسؤول استئنافها من الإعدادات. |

`{name}` for an operator action = "the platform operator" / "مشغّل المنصة" (R17 spirit: the workspace never learns which operator). No e-mail for pause/resume (D18.7).

**Rate limits and expiry:** new policy `"danger"` in `RateLimitingExtensions.cs` (copy the `"device-start"` block `:85-93`): fixed window **10 per 10 min**,
limit read from `Security:RateLimits:DangerPer10Min` (default 10; the e2e compose raises it — Opus LOW, precedent `:32-35`), **partitioned by
`sub` claim + IP when authenticated, IP otherwise** (Opus MEDIUM), on `deletion/request` and the three anonymous `workspace-deletion/*` endpoints. This is a
**documented R16 departure** (R16 names `"signup"` for anonymous token endpoints; its 5/h/IP budget is shared with signup/reset and too small for
preview + confirm + retries) — R16 amendment text in task 19. Plus the 5-min cooldown and 5/day per-workspace cap on request (§3.4); plus the global e-mail cap. Link TTL
30 min (shared `ResetTokenService` const — unchanged); single-use per §3.4.

### 3.8 Endpoints

| Route | Controller | Auth | Attributes | Response (inner type) |
|---|---|---|---|---|
| `POST api/admin/workspace/pause` | `WorkspaceController` | `Policies.Admin` (class) | `[Audited(WorkspacePaused)]` | `WorkspaceResponse` 200; 403; 409 |
| `POST api/admin/workspace/resume` | same | same | `[Audited(WorkspaceResumed)]`, `[AllowWhenWorkspacePaused]`, `[AllowUnverified]` | `WorkspaceResponse` |
| `POST api/admin/workspace/deletion/request` | same | same | `[Audited(WorkspaceDeletionRequested)]`, `[AllowWhenWorkspacePaused]`, `[EnableRateLimiting("danger")]` | `WorkspaceDeletionRequestResponse` |
| `POST api/admin/workspace/deletion/cancel` | same | same | `[Audited(WorkspaceDeletionCancelled)]`, `[AllowWhenWorkspacePaused]`, `[AllowUnverified]` | `WorkspaceResponse` |
| `POST api/auth/workspace-deletion/preview` | `AuthController` | `[AllowAnonymous]` | `[NoAudit("read of a deletion preview; the token is the credential")]`, `[EnableRateLimiting("danger")]` | `WorkspaceDeletionPreviewResponse` |
| `POST api/auth/workspace-deletion/confirm` | same | `[AllowAnonymous]` | `[Audited(WorkspaceDeletionConfirmed)]`, `[EnableRateLimiting("danger")]` | `WorkspaceDeletionScheduledResponse`; 400; 403 |
| `POST api/auth/workspace-deletion/pause-instead` | same | `[AllowAnonymous]` | `[Audited(WorkspacePaused)]`, `[EnableRateLimiting("danger")]` | `Result` (no body type — copy `TenantsController.cs:68-70` comment) |
| `POST api/admin/tenants/{workspaceId:guid}/pause` | `TenantsController` | SuperAdmin (class) | `[Audited(WorkspacePaused)]` | `Result` |
| `POST api/admin/tenants/{workspaceId:guid}/resume` | same | same | `[Audited(WorkspaceResumed)]` | `Result` |
| `POST api/admin/tenants/{workspaceId:guid}/deletion/cancel` | same | same | `[Audited(WorkspaceDeletionCancelled)]` | `Result` |

Tags already in `orval.config.ts:6` (`Workspace`, `Auth`, `Tenants`). Status mapping copies `WorkspaceController.cs:28-31` (`IsForbidden → 403`, `IsNotFound → 404`,
`IsConflict → 409`, else 400). Every `[ProducesResponseType]` names the **inner** type. `PATCH …/status` (approve/enable/disable) is **unchanged** (D18.6).

### 3.9 Grace period vs. immediate delete — recommendation: **7-day grace, frozen, cancellable** (D18.1)

Immediate hard delete is irreversible except by a full-database restore that would also roll back every other tenant — unacceptable once a second real tenant
exists. A grace period protects against (a) accidental confirmation, (b) a hijacked admin mailbox + password, (c) one admin deleting a multi-admin workspace
over colleagues' heads (E2 + cancel). GDPR Art. 17 / Art. 12(3) and PDPL require erasure "without undue delay", generally within one month; 7 days + the
existing backup expiry (14 d local, **30 d off-box** — `DEPLOY.md:96-97,159`) keeps total residual retention ≤ grace + 30 days (≤ 44 d with the 14-day clamp),
which the privacy page must state from the configured value
(task 12). Export remains available throughout the grace period. `GraceDays = 0` is supported by config (job deletes within one sweep) if the owner prefers
near-immediate.

### 3.10 Owner decisions (defaults apply unless the owner says otherwise before §9)

**Answered by the owner 2026-09-24:** D18.1 = 7-day cancellable grace; D18.2 = requester only receives the confirm link (all admins get
the scheduled / reminder / cancelled / deleted notices); D18.8 = ship the existing comment export, labelled honestly; D18.10 = accounts that
belong only to this workspace are deleted with it, listed in the preview. D18.3–D18.7, D18.9, D18.11: recommended defaults below apply.

| # | Question | Recommended default |
|---|---|---|
| D18.1 | Immediate delete or grace period; length | **Grace 7 days** (`WORKSPACE_DELETION_GRACE_DAYS`), workspace frozen, any admin cancels; reminder 24 h before |
| D18.2 | Who receives the confirmation e-mail | **The requesting Workspace Admin only** (the password proof must be the requester's); every live admin gets the scheduled / reminder / cancelled / deleted notices |
| D18.3 | Does pause block widget **reads**? | **No** — widget shows existing comments read-only with a "Feedback is paused" notice; members can sign in, read and export |
| D18.4 | API keys / CLI while frozen | **Refused entirely (reads too)** except `GET /api/auth/me` and telemetry; `pointer apply` exits before touching files |
| D18.5 | Confirmation factor | **Password** (password accounts) **+ typed workspace name** (everyone); passwordless admins: link + typed name. No MFA step-up (MFA is operator-only today) |
| D18.6 | Operator suspend | **Keep** the membership `approve/enable/disable` action unchanged (it is person-scoped); **add** operator workspace pause (`paused_by_operator`) that admins cannot lift; self-service delete refused while operator-paused; **an operator pause during the grace period holds the scheduled delete** (job skips it until resumed or cancelled); operator resume clears an admin self-pause too |
| D18.7 | E-mail non-admin members about a scheduled deletion; e-mail on pause/resume | **No** — dashboard banner for every member; e-mails only to admins, only for deletion events |
| D18.8 | Export scope | **Ship with the existing `GET /api/export`** (comments + replies + element metadata, ≤ 5 000 comments, no screenshots/config/members), labelled "Export comments (JSON)" with a line saying what is not included; a full workspace archive (screenshots zip, projects, settings) is a separate doc if wanted |
| D18.9 | Demo workspaces | Self-service pause/delete **refused** for live demos (they expire; "Keep this workspace" exists); operator may pause |
| D18.10 | Accounts that belong only to this workspace | **Deleted with it** (existing `HardDeleteAsync :641-670` rule: created in this workspace and no membership row of any state elsewhere; shared query §3.4), shown as a count in the preview and worded exactly so in E2; alternative (keep membership-less identities) is a `HardDeleteAsync` change → separate decision |
| D18.11 | New members while frozen | **Refused** (invite accept, stakeholder register); existing members keep signing in |

## 4. Safety classification

**Additive** (R1): one migration of `AddColumn`/`CreateIndex`/`AddCheckConstraint` on `workspaces` only; no data moved, nothing dropped/renamed/narrowed; no
backfill (R3 not engaged — the server default fills `paused_by_operator`). Ordinary deploy through `scripts/deploy-api.sh` (pre-deploy dump, R6); the DB-09 gate
does not trigger (no `[ContractMigration]`, R7). **Code that deletes data:** only the new job, and only through the existing `HardDeleteAsync` with the new guarded
reason `owner_requested` (pre-check + `FOR UPDATE` locked re-check + audit inside the transaction + files after commit — the DB-17 `demo_expired` treatment),
so a cancel racing the job never loses data, and an operator pause holds the delete (both checks include `!paused_by_operator`). Tenancy (R8): no new entity; every session action resolves the workspace from the session (`TryRequireOwner`),
every anonymous action from a signed token bound to one workspace + membership; §6 tests 8 prove A/B isolation. **No owner approval line is required** (not
Destructive); the owner decisions in §3.10 are product defaults.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — after `DemoTtlHoursOverride` (`:38`): the doc-comment block (§3.2) and eight properties (`DateTime? PausedAt; Guid? PausedBy;
   bool PausedByOperator; DateTime? DeletionRequestedAt; Guid? DeletionRequestedBy; DateTime? DeletionConfirmedAt; DateTime? DeletionScheduledFor; DateTime? DeletionReminderSentAt;`).
2. `Infrastructure/Mappings/WorkspaceMapping.cs` — replace `:11-14` with `b.ToTable("workspaces", t => { t.HasCheckConstraint("ck_workspaces_name_not_blank", "length(btrim(name)) > 0"); t.HasCheckConstraint("ck_workspaces_pause_consistent", "…"); t.HasCheckConstraint("ck_workspaces_deletion_request_consistent", "…"); t.HasCheckConstraint("ck_workspaces_deletion_schedule_consistent", "…"); });`
   (SQL strings verbatim from §3.2); after `:44` add eight `b.Property(x => x.X).HasColumnName("snake_name")` lines, `b.Property(x => x.PausedByOperator).HasColumnName("paused_by_operator").HasDefaultValue(false);`,
   a comment `// DB-18: PausedBy/DeletionRequestedBy are users.public_id content references (R14) — no FK by design.`, and
   `b.HasIndex(x => x.DeletionScheduledFor).HasFilter("deletion_scheduled_for IS NOT NULL").HasDatabaseName("ix_workspaces_deletion_scheduled_for");`.
3. `just migrate name="AddWorkspacesPauseAndDeletionState"` → open `Infrastructure/Migrations/<ts>_AddWorkspacesPauseAndDeletionState.cs` and compare to §3.2 (8 `AddColumn`,
   1 `CreateIndex` with filter, 3 `AddCheckConstraint`, all on `workspaces`); diff `AppDbContextModelSnapshot.cs` — only the `Workspace` entity may change. **Differs → stop and report** (R13).
4. `Application/Common/TokenPurposes.cs` — `public const string DeleteWorkspace = "delete-workspace";` with doc "DB-18: workspace deletion confirmation (payload = workspaceId|membershipStamp|requestedAtUnixMs)".
5. `Application/Common/AuditActions.cs` — in the block `:88-98` add `WorkspacePaused = "workspace.paused"`, `WorkspaceResumed = "workspace.resumed"`,
   `WorkspaceDeletionRequested = "workspace.deletion_requested"`, `WorkspaceDeletionConfirmed = "workspace.deletion_confirmed"`, `WorkspaceDeletionCancelled = "workspace.deletion_cancelled"`,
   `WorkspaceDeletionRescheduled = "workspace.deletion_rescheduled"` (System; missed-reminder push, §3.4).
   `Application/Common/AuditFields.cs` — add `"scheduled_for"` to `Allowed` (a timestamp; no personal data — R17 whitelist addition, reviewer named in the PR).
6. `Application/Resources/MessageKeys.cs` `Workspace` (`:215-221`) — add: `Paused = "This workspace is paused. It is read-only until an admin resumes it."`,
   `PausedByOperator = "This workspace was paused by the platform operator. Contact support to resume it."`,
   `DeletionScheduledReadOnly = "This workspace is scheduled for deletion and is read-only. An admin can cancel the deletion in Settings."`,
   `AlreadyPaused = "The workspace is already paused."`, `NotPaused = "The workspace is not paused."`, `CancelDeletionFirst = "Cancel the scheduled deletion first."`,
   `DeletionAlreadyScheduled = "Deletion is already scheduled for this workspace."`, `NoDeletionScheduled = "No deletion is scheduled for this workspace."`,
   `DeletionEmailJustSent = "A confirmation e-mail was just sent. Check your inbox."`, `DeletionEmailSent = "We sent a confirmation link to your e-mail address. It expires in 30 minutes."`,
   `DeletionLinkInvalid = "This link is invalid, expired or already used. Request a new one from Settings."`, `DeletionNameMismatch = "Type the workspace name exactly as shown."`,
   `DeletionScheduled = "The workspace will be deleted on {0} UTC."`, `DeletionCancelled = "Deletion cancelled."`, `AdminOnly = "Only a Workspace Admin can do this."`,
   `KeySessionCannotManage = "API-key sessions cannot pause or delete a workspace."`, `DemoCannotPauseOrDelete = "Demo workspaces expire on their own — keep it or let it expire."`,
   `FrozenNoNewMembers = "This workspace is not accepting new members right now."`,
   `FrozenNoAccessGrant = "While the workspace is paused you can only remove or reduce access."`, `AlreadyDeleted = "This workspace has already been deleted."`,
   `StateChanged = "The workspace changed while you were acting on it. Reload and try again."`, `DeletionDailyLimit = "Too many deletion requests for this workspace today. Try again tomorrow."`.
7. `Application/Services/Interfaces/IWorkspaceStateService.cs` + `Implementation/WorkspaceStateService.cs` (§3.3 verbatim).
8. `Application/Services/Interfaces/IWorkspaceLifecycleService.cs` + `Implementation/WorkspaceLifecycleService.cs` (§3.4 table row by row; token §3.4; e-mails §3.7).
   `Application/Common/WorkspaceLifecycleEmails.cs` (§3.7). DTOs in `Application/DTOs/Workspace/`: `WorkspaceDeletionPreviewResponse`, `ConfirmWorkspaceDeletionRequest`,
   `WorkspaceDeletionTokenRequest`, `WorkspaceDeletionRequestResponse`, `WorkspaceDeletionScheduledResponse`; extend `WorkspaceResponse.cs` (§3.4).
   Validators in `Application/Validators/`: `ConfirmWorkspaceDeletionRequestValidator`, `WorkspaceDeletionTokenRequestValidator` (Token required). The name normaliser is a private
   static `NormalizeName` in the service (§3.4 confirm row). Follow the §3.4 **query rule** and **concurrency rule** in every method they name.
9. `Application/Services/Implementation/WorkspaceService.cs` — `GetAsync` (`:37-62`) fills the new `WorkspaceResponse` fields (constructor gains `IMembershipService? memberships = null, IConfiguration? config = null`); `ToResponse` (`:110-118`) maps the columns.
10. `Application/Services/Implementation/TenantService.cs` `HardDeleteAsync` (`:524-713`) — replace `isDemoExpiredReason` (`:533`) with `var isGuardedReason = reason is "demo_expired" or "owner_requested";`
    and a local `Task<bool> StillDueAsync()` that runs the DB-17 predicate for `demo_expired` and `w.Id == workspaceId && w.DeletionScheduledFor != null && w.DeletionScheduledFor <= DateTime.UtcNow`
    for `owner_requested`; use it at `:539-549` (failure text for owner_requested: `"Not a due scheduled deletion (cancelled meanwhile)."`) and `:603-611` (throw `"scheduled deletion cancelled during delete"`);
    `:573` → `ActorKindOverride: isGuardedReason ? AuditActorKind.System : null`; `:576`, `:684`, `:694` → `isGuardedReason`. The `owner_requested` predicate in **both** the pre-check and
    the locked re-check includes `&& !w.PausedByOperator` (Opus HIGH 3). Add `public static IQueryable<User> IdentitiesDeletedWithWorkspace(IUnitOfWork, Guid)` (§3.4) and make `:641-670`
    use it for the delete set (re-home the rest exactly as today); a Sqlite parity test (§6 test 9) guards the refactor. `ListAsync` mapping (`:51-160`) — three `TenantResponse` fields; `TenantResponse.cs` — three properties.
11. `API/Auth/AllowWhenWorkspacePausedAttribute.cs`, `API/Auth/WorkspaceFrozenFilter.cs` (§3.5). `API/Program.cs` — `options.Filters.Add<Pointer.API.Auth.WorkspaceFrozenFilter>();` after `:73` with a two-line DB-18 comment;
    `"X-Workspace-Paused"` in both `WithExposedHeaders` (`:155`, `:163`); `builder.Services.AddHostedService<WorkspaceDeletionService>();` beside `:106-107`.
12. Attributes per §3.5 on `AuthController` (class + `Me` method), `MeController`, `MfaController`, `EventsController.RecordEvent` (method), `Admin/UsersController` (`PATCH {id}`, `DELETE {id}`),
    `Admin/InvitesController` (`DELETE {id}`, `POST {id}/quick-link/rotate`); the frozen-state refusal of access *grants* inside `UserService`'s update method (§3.5).
    `Program.cs` `IsDashboardOnly` (`:434-447`) gains the `/api/auth/workspace-deletion` segment.
13. `API/Controllers/Admin/WorkspaceController.cs` — four actions (§3.8; constructor gains `IWorkspaceLifecycleService lifecycle`). `API/Controllers/AuthController.cs` — three anonymous actions (§3.8; copy `:182-209`).
    `API/Controllers/Admin/TenantsController.cs` — three operator actions after `SetStatus` (`:116`). `API/Extensions/RateLimitingExtensions.cs` — `"danger"` policy (§3.7: config key
    `Security:RateLimits:DangerPer10Min`, partition `sub`+IP / IP). `AuthService.cs:1325` and `~:1718` pass the workspace to `ToMeResponse` (§3.6).
14. `API/Hosted/WorkspaceDeletionService.cs` — copy `DemoCleanupService.cs` structure: 30 s initial delay; `PeriodicTimer(TimeSpan.FromMinutes(SweepMinutes))`; step 1 `SendDueRemindersAsync` (own scope + try/catch);
    step 2 `internal static SweepOnceAsync(IServiceScopeFactory, ILogger, ct)`: query due ids (`DeletionScheduledFor != null && <= now && DeletedAt == null`), then **per id, fresh scope**:
    re-check still due → `IWorkspaceLifecycleService.ExecuteDueDeletionAsync(id)`; log `WorkspaceDeletionService: hard-deleted {Id} (owner_requested)` / `… skipped (cancelled)` /
    `… held by operator pause`. The due-id query is `Workspaces.IgnoreQueryFilters().AsNoTracking().Where(w => w.DeletedAt == null && w.DeletionScheduledFor != null && w.DeletionScheduledFor <= now && !w.PausedByOperator)`.
    Failure handling (Opus LOW): `Result.Failure` from the pre-check or the `InvalidOperationException` thrown by the locked re-check = **skip** (Information); any other exception = `LogError`
    `WorkspaceDeletionService: FAILED {Id}` and an in-memory per-id counter — at 3 consecutive failures log **Critical** `WorkspaceDeletionService: {Id} failing repeatedly` (§9 step 5 watches for it).
15. `ProjectService.CheckWidgetActiveAsync` + `WidgetActivationResponse` (§3.6). `InviteService.cs:644-654` and `AuthService.cs:1359-1360` frozen checks (§3.5).
    `MeResponse.cs` three properties after `:46`; `UserMapper.cs:53-57` three assignments from `workspace`.
16. Config (GraceDays clamp 0..14): `API/appsettings.json` → `"WorkspaceDeletion": { "GraceDays": 7, "SweepMinutes": 15 }` and `"Security:RateLimits:DangerPer10Min": 10` (inside the existing `Security:RateLimits` block — verify its location); e2e compose sets it to 100; `docker-compose.prod.yml` after `:90` →
    `WorkspaceDeletion__GraceDays: "${WORKSPACE_DELETION_GRACE_DAYS:-7}"`, `WorkspaceDeletion__SweepMinutes: "${WORKSPACE_DELETION_SWEEP_MINUTES:-15}"`; `.env.prod.example` after `:70` the two commented lines.
17. Widget `web-component/src/` — `element.ts` `_checkWidgetActive` (`:474-487`) also stores `this._paused = data?.paused === true`; when `_paused`: hide the add-comment control,
    reply box and status/menu mutations, show one line `t('paused.notice')` in the panel header. **423 is handled once, in `api()` (`:967`)** (Opus LOW — covers the comment POST
    `:2182`, PATCH/PUT/DELETE of comments and replies, verify, fields, visibility): on `r.status === 423` set `this._paused = true`, re-render read-only, toast `t('paused.notice')` once per
    page, and return the response so callers' `!r.ok` branches exit without their generic "failed" toast (add `if (r.status === 423) return false;` at the top of those branches — verify each site).
    The screenshot upload (`:1786`, `pfFetch` direct) is **skipped when `_paused`**, and a 423 from it is treated like the above. `i18n.ts`: `paused.notice` en "Feedback is paused for this workspace." / ar "تم إيقاف استقبال الملاحظات مؤقتًا في مساحة العمل هذه.". `npm run build` + `node scripts/check-budget.mjs`
    (must stay within budget — if over, drop the header line and keep only the toast + hidden controls); commit `API/wwwroot/pointer.*`.
18. CLI `cli/src/` — `api.ts`: nothing (ApiError carries 423 + server message); `commands/apply.ts`: after authentication, `GET /api/auth/me`; if `workspaceDeletionScheduledFor` →
    print `Workspace "<tenantName>" is scheduled for deletion on <date> — apply is disabled.`; else if `workspacePausedAt` → print `Workspace "<tenantName>" is paused — apply is disabled until a workspace admin resumes it.`;
    exit code **2**, before any git/AI work; `--plan` and `--mark` print the same warning and exit 2 too (the queue GET is 423 for key sessions). **`commands/deployed.ts`** (`:57-80,113-140`): a
    423 from either call → `console.warn("Pointer workspace is paused — build not reported.")` and **exit 0** (never fail a customer's CI). **`mcp/tools.ts`**: a 423 → return a tool error with the
    server message (no `process.exit`). No publish (memory: publish only on request).
19. Docs: `landing/privacy.html:253-260` add `<tr><td>Workspaces deleted by their admin</td><td>Deleted {GraceDays} days after the admin confirms (cancellable until then); remaining backup copies expire within 30 days</td></tr>` (write the configured value, 7 — Opus NIT) and adjust `:265-267` to mention admin-requested deletion;
    `DEPLOY.md` § Backups one sentence ("Admin-requested workspace deletions run from `WorkspaceDeletionService` after `WORKSPACE_DELETION_GRACE_DAYS` (7); restoring one means restoring a dump — see Restore");
    `docs/db/SCHEMA.md` `workspaces` row (eight columns, index, three constraints). **DB-RULES R19** (the db-architect adds it when this merges): "Every new mutating action is either gated by
    `WorkspaceFrozenFilter` or carries `[AllowWhenWorkspacePaused]` with a reason; `WorkspaceFreezeCoverageTests` pins the list; a new anonymous path that creates a membership checks `IWorkspaceStateService`; access-*removing* admin actions are always exempt."
    **R16 amendment** (same commit, Opus LOW — the two documented departures): "Anonymous endpoints that redeem a scoped token may use a dedicated fixed-window
    policy instead of `signup` when the flow needs several calls per link (DB-18 `danger`: 10/10 min, `sub`+IP); single-use may come from **state** bound into
    the payload (DB-18: `deletion_requested_at` + `deletion_scheduled_for`) instead of an identity-stamp rotation when the event is workspace-scoped and the
    session must survive."
20. `just fmt`; `just test`.

## 6. Tests

InMemory harness: copy `Tests/ChangeEmailTests.cs:27-230` (`FakeCurrentUser`, `IdentityHasher`, `NoopBrandingService`, `CapturingEmail`, `RealResetTokens()`), new file
`Tests/Db18WorkspaceLifecycleTests.cs`:
1. `Pause_ByWorkspaceAdmin_SetsColumns_AuditsWorkspacePaused`; `Pause_ByDeputy_Forbidden`; `Pause_KeySession_Forbidden`; `Pause_QuickAccess_Forbidden`; `Pause_LiveDemo_Refused`; `Pause_Twice_Conflict`.
2. `Resume_AfterSelfPause_Clears`; `Resume_OperatorPaused_ForbiddenForAdmin`; `OperatorResume_ClearsOperatorPause`; `Resume_WhileScheduled_Conflict`.
3. `RequestDeletion_OneMail_ToRequesterOnly_LinkHasPurposeDeleteWorkspace`; `RequestDeletion_Cooldown5Min`; `RequestDeletion_SixthIn24h_DailyLimit`; `RequestDeletion_WhenScheduled_Conflict`; `RequestDeletion_WhenOperatorPaused_Forbidden`; `RequestDeletion_ArabicUser_GetsArabicMail` (`Language = "ar"` → subject contains "تأكيد" and `dir="rtl"`).
4. `Confirm_ValidTokenPasswordName_SchedulesGraceDays_MailsEveryLiveAdmin` (3 admins → 3 E2 mails; a Deputy and a left admin get none); `Confirm_WrongPassword_NoStateChange`; `Confirm_NameMismatch`;
   `Confirm_Passwordless_NameOnly_Ok`; `Confirm_Twice_SecondIsLinkInvalid`; `Confirm_AfterPasswordChange_LinkInvalid` (rotate `users.security_stamp`); `Confirm_AfterDemotion_LinkInvalid` (rotate membership stamp + role Deputy);
   `Confirm_AfterLeave_LinkInvalid`; `Confirm_AfterNewerRequest_OldLinkInvalid`; `Confirm_AfterCancel_OldLinkInvalid`; `Confirm_EraseTokenOfSameIdentity_Invalid` (purpose); `Confirm_TimestampRoundTrip_MsTruncation` (reload the row from a fresh context before validating).
5. `PauseInstead_PausesAndSpendsLink` (second confirm → invalid); `PauseInstead_AlreadyPaused_OnlySpendsLink`.
6. `Cancel_ByAnotherAdmin_ClearsSchedule_KeepsPriorPause_MailsAdmins`; `Preview_ReturnsCounts_AndAccountsDeletedWithWorkspace` (identity only here → counted; identity also in B → not).
7. Filter: new `Tests/WorkspaceFrozenFilterTests.cs` copying `Tests/RequireVerifiedEmailFilterTests.cs:1-260`: `Frozen_Post_Returns423_WithHeader`; `Frozen_Get_Passes`; `Frozen_AllowWhenPaused_Passes`;
   `Frozen_KeySession_Get_Returns423`; `Frozen_KeySession_AuthMe_Passes`; `SuperAdmin_Passes`; `NoTenant_Passes`; `AllowAnonymous_Passes`; `LookupThrows_FailsOpen`; `OperatorPause_MessageIsPausedByOperator`.
   Coverage: new `Tests/WorkspaceFreezeCoverageTests.cs` copying the enumeration of `Tests/AuditCoverageTests.cs:72-110` — the set of controllers/actions carrying `[AllowWhenWorkspacePaused]` equals the §3.5 list exactly.
8. **R8 tenancy** — `TenantB_Admin_PauseAndCancel_AffectOnlyB` (identity admin of A and B, session tenant B → A untouched); `LinkMintedForA_CannotScheduleB` (tamper the payload → invalid; valid A link → only A scheduled);
   `WorkspaceResponse_TenantB_SeesNoStateOfA` (`Tests/TenantQueryFilterTests.cs` shape).
9. Sqlite (copy `Tests/Db17HardDeleteAndCleanupTests.cs:31-330`, `BuildSweepContainer`, `RecordingFileStorage`): `Job_DueDeletion_HardDeletes_TenantHardDeletedOwnerRequestedSystem`; `Job_NotDue_Untouched`;
   `Job_CancelledBetweenQueryAndDelete_Skipped_NoFileDelete` (use the `ExtendingOnLockUnitOfWork` idea `:244-288` to cancel inside the lock); `HardDelete_OwnerRequested_NotScheduled_Refused_NoAudit_NoFiles`;
   `HardDelete_AdminReason_Unaffected`; `Job_Reminder_SentOnce_Within24h_StampedEvenIfSendFails`; `Job_DeletedMail_SentToAdminsCapturedBeforeDelete`.
10. `InviteAccept_FrozenWorkspace_Refused` (InviteService fixture), `Register_FrozenWorkspace_Refused` (AuthService fixture from `Tests/WorkspaceSwitchTests.cs`); `Login_FrozenWorkspace_StillOk_MeCarriesState`;
    `WidgetStatus_FrozenWorkspace_ActiveTrue_PausedTrue`.
11. `Tests/AuditCoverageTests.cs` passes with the ten new actions; `Tests/MigrationSafetyTests.cs` passes (no marker expected); `Tests/WorkspaceTests.cs` `HardDeleteOrder_CoversEveryOwnerCarryingEntity` unchanged.
12. **Existing data survives** — R11 rehearsal (§7 criterion 7): every production `workspaces` row keeps its values, new columns NULL/false; `comments`/`projects` counts identical before/after.
9a. **Cross-review additions (2026-09-24)** — `Preview_AccountsCount_EqualsRowsActuallyDeleted` (Sqlite, Opus HIGH 1: seed an identity created here with an *ended* membership in B → not counted and re-homed;
    one created elsewhere and joined here → not counted; one created here only → counted and deleted; run preview, then `HardDeleteAsync`, assert equal);
    `Freeze_AllowsRevocations_RefusesGrants` (PATCH `isActive:false` / role→non-admin / DELETE member / DELETE invite / rotate → pass; PATCH `isActive:true`, admin role, password, POST users/invites → refused; Opus HIGH 2);
    `Job_OperatorPausedDuringGrace_NotDeleted` + `HardDelete_OwnerRequested_OperatorPaused_Refused` (Opus HIGH 3); `Confirm_Concurrent_OnlyOneSchedules` and `PauseInstead_RacingConfirm_NoConstraintViolation`
    (Sqlite, two contexts; second call → `DeletionLinkInvalid`, no 500); `Cancel_WhileJobHoldsLock_Conflict` and `Cancel_AfterRowDeleted_ConflictAlreadyDeleted` (Gemini LOW #1);
    `Reminder_RacingCancel_NoConstraintViolation`; `Reminder_Missed_ReschedulesPlus24h_Audited`; `Reminder_GraceUnder2Days_NotSent`; `Confirm_FiveWrongPasswords_LockoutAndLinkSpent`;
    `Confirm_NullPassword_400NotServerError`; `Confirm_ArabicNameWithRlmMarks_Matches`; `CancelPending_VoidsLink`; `PauseInstead_AlreadyPaused_WritesExactlyOneAuditRow`;
    `Job_And_Reminder_RunWithNoTenantCurrentUser` (Opus MEDIUM query rule); `StateService_SoftDeletedWorkspace_NotFrozen` (Gemini LOW #2);
    `KeySession_EventsSummary_Returns423` (method-level Events exemption); `Register_ReapplyAfterRejection_RefusedWhileFrozen`; `ConvertedDemoInVerifyWindow_CanPause`;
    `LoginWithKey_ResponseCarriesFreezeFields`; `DangerLimiter_PartitionsByIdentity` (two identities, one IP → independent budgets). CLI (`cli/test`): `deployed_423_WarnsExitsZero`,
    `mcp_423_ReturnsToolError`. Coverage test (§6 test 7) pins the four revocation actions and `EventsController.RecordEvent` in the exemption list.
13. e2e (isolated stack, memory note "Isolated e2e stack recipe"; `WORKSPACE_DELETION_SWEEP_MINUTES=1`, `WORKSPACE_DELETION_GRACE_DAYS=0` for the last case): new `e2e/mail/workspace-lifecycle-mail.spec.mjs` using `e2e/scripts/lib/mail.mjs`:
    (a) admin pauses → widget `POST …/comments` → 423 + `x-workspace-paused`; `GET …/widget-status` → `paused: true`; `GET …/comments` (JWT) → 200; key session `GET …/comments` → 423; `GET /api/auth/me` (key) → 200 with `workspacePausedAt`; resume → POST 200;
    (b) `POST deletion/request` → `awaitMessage({ to: admin, subjectIncludes: 'Confirm deleting' })` → `extractLink(html, '/confirm-workspace-deletion')` → preview 200 → confirm wrong password 400 → confirm 200 (`deletionScheduledFor` ≈ now + grace) → confirm again 400 →
    every admin receives "will be deleted" → cancel → "was cancelled" mail; (c) request → pause-instead → workspace paused, link dead; (d) grace 0: confirm → within ~90 s `tenant.hard_deleted` (`reason = owner_requested`) and "has been deleted" mail; login refused.
    Dashboard UI spec (Playwright) for the Danger-zone dialog steps and the confirmation page (§11).

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` → 81 entries, the last `_AddWorkspacesPauseAndDeletionState`. Counts are taken on the **`Up()` slice only**
   (`Down()` legitimately contains `DropColumn`/`DropIndex`/`DropCheckConstraint` — Opus MEDIUM; same slicing as `Tests/MigrationSafetyTests.cs:85-87`):
   `F=$(ls Infrastructure/Migrations/*_AddWorkspacesPauseAndDeletionState.cs | grep -v Designer); UP=$(awk '/void Up\(/,/void Down\(/' "$F")`;
   `echo "$UP" | grep -c "AddColumn"` → 8, `grep -c "AddCheckConstraint"` → 3, `grep -c "CreateIndex"` → 1, `grep -c '\.Sql(\|ContractMigration\|DropColumn\|AlterColumn'` → 0;
   every `table:` argument in the slice is `"workspaces"` (`echo "$UP" | grep -o 'table: "[a-z_]*"' | sort -u` → one line).
2. `grep -c "DeleteWorkspace = \"delete-workspace\"" Application/Common/TokenPurposes.cs` → 1; `grep -rn "class .*Token.*Mapping\|workspace_deletion_tokens" Infrastructure` → nothing (no token table).
3. `grep -c "owner_requested" Application/Services/Implementation/TenantService.cs` → ≥ 2; `grep -c "isDemoExpiredReason" Application/Services/Implementation/TenantService.cs` → 0.
4. `grep -c "WorkspaceFrozenFilter" API/Program.cs` → 1; `grep -c "X-Workspace-Paused" API/Program.cs` → 2; `grep -rln "AllowWhenWorkspacePaused" API/Controllers` lists exactly `AuthController.cs, MeController.cs, MfaController.cs, EventsController.cs, Admin/WorkspaceController.cs, Admin/UsersController.cs, Admin/InvitesController.cs`;
   `grep -c "workspace-deletion" API/Program.cs` → 1 (`IsDashboardOnly`); `grep -c "PausedByOperator" Application/Services/Implementation/TenantService.cs` → ≥ 2; `grep -c "IdentitiesDeletedWithWorkspace" Application/Services/Implementation/TenantService.cs Application/Services/Implementation/WorkspaceLifecycleService.cs` → ≥ 2 and ≥ 1.
5. `curl -s …/swagger/v1/swagger.json | jq '.paths | keys[]' | grep -c "workspace/pause\|workspace/resume\|workspace/deletion\|workspace-deletion\|tenants/{workspaceId}/pause\|tenants/{workspaceId}/resume\|tenants/{workspaceId}/deletion"` → 10;
   `jq '.components.schemas.MeResponse.properties | has("workspacePausedAt") and has("workspaceDeletionScheduledFor")'` → true; `jq '.components.schemas.WidgetActivationResponse.properties | has("paused")'` → true.
6. `just test` green (≈ 60 new facts); DB-10 green (apply from empty + newest `Down()`/`Up()` round-trip).
7. **R11 rehearsal** on a same-day prod dump (commands `DB-RULES.md` R11): before → `SELECT count(*) FROM workspaces;` = N; after `database update`: `SELECT count(*) FROM workspaces WHERE paused_at IS NULL AND NOT paused_by_operator AND deletion_requested_at IS NULL AND deletion_scheduled_for IS NULL;` = N;
   `\d workspaces` shows the eight columns, `ix_workspaces_deletion_scheduled_for … WHERE (deletion_scheduled_for IS NOT NULL)`, and `SELECT conname FROM pg_constraint WHERE conrelid = 'workspaces'::regclass AND contype = 'c' ORDER BY 1;` → the four `ck_workspaces_*` names;
   `database update 20260923205702_DropUsersLegacyDemoColumns` (Down) then Up again → clean. On the rehearsal API: flows (a)–(c) of §6 test 13 by `curl`, pasted into the PR.
8. Local e2e gate `scripts/local-e2e-gate.sh` PASS including the new spec (memory: full e2e green on the isolated prod-dump stack before any deploy).
9. Widget budget check passes; `grep -c "423" web-component/src/element.ts` → ≥ 1; CLI: `node cli/dist/… apply` against a paused workspace exits 2 and `git status` in the fixture repo shows no change.

## 8. Rollback

Code rollback (preferred): `git checkout <commit before DB-18>` + `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api`. The additive columns stay and are ignored:
**paused workspaces become writable again and scheduled deletions simply never run** (the safe direction). List them first and tell their admins:
`SELECT id, name, paused_at, paused_by_operator, deletion_scheduled_for FROM workspaces WHERE paused_at IS NOT NULL OR deletion_scheduled_for IS NOT NULL;`.
Schema rollback (only if the columns themselves are the problem): `dotnet ef database update 20260923205702_DropUsersLegacyDemoColumns` runs the generated `Down()`
(drop constraints, index, eight columns) — **this discards pause and schedule state**; run the query above and keep its output first. **Workspaces already
hard-deleted by the job are not restorable** except from a dump (`DEPLOY.md` § Restore — restores every tenant to that point, so it is an owner-level
decision) — that is exactly why the grace period exists. The pre-deploy dump is taken by `deploy-api.sh` (R6).

## 9. Release steps

0. Owner decisions §3.10 — defaults apply unless answered; D18.1 / D18.2 / D18.8 / D18.10 relayed to the owner before step 3.
1. Prod pre-checks (read-only, pasted into the PR): `SELECT count(*) FROM workspaces;` and the operator-disable census
   `SELECT m.owner_id, m.is_active, m.approval_status FROM workspace_memberships m JOIN roles r ON r.id = m.role_id AND r.name = 'Workspace Admin' WHERE m.left_at IS NULL AND m.deleted_at IS NULL AND (NOT m.is_active OR m.approval_status <> 1);`
   (expected small; unchanged by this doc — recorded so D18.6's "keep disable as is" is an informed choice).
2. R11 rehearsal (§7.7) + local e2e gate (§7.8).
3. Ordinary deploy: `bash scripts/deploy-api.sh` (dump `pre-deploy` → build → boot applies the additive migration; the widget build ships inside `wwwroot`).
4. Verify: `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|WorkspaceDeletionService|error"` → one `Applying migration '…_AddWorkspacesPauseAndDeletionState'`, then
   `WorkspaceDeletionService` start lines, no errors; `GET /api/auth/me` as the production admin → `workspacePausedAt: null`; `GET /api/admin/workspace` → `canManageLifecycle: true`;
   `GET /api/public/projects/<key>/widget-status` → `paused: false`. Optionally on a throwaway workspace: pause → widget POST 423 → resume.
5. Watch: `423` counts in the access log (a burst on an unpaused workspace = cache/invalidation bug), `WorkspaceDeletionService: FAILED` and any **Critical** `… failing repeatedly`
   (a stuck scheduled deletion — investigate, never hand-delete rows), `held by operator pause` (expected only while an operator pause is on), `429` bursts on `danger`, e-mail cap exhaustion.
6. `dashboard-agent`: regenerate the client from production once, then §11. `rebranding-agent`: new customer-visible strings, the `/confirm-workspace-deletion` route and five e-mail templates (CLAUDE.md rule 6).
7. Landing deploy for the privacy row (task 19). CLI: build and test; publish only when the owner asks.

## 10. Out of scope

A full workspace archive export (screenshots, projects, settings, members — D18.8); changing which identities `HardDeleteAsync` removes (D18.10); retiring
or re-meaning the operator `approve/enable/disable` action (D18.6); workspace transfer of ownership; billing/subscription cancellation side effects
(`Subscription` rows are deleted by `HardDeleteAsync` as today — no provider call is added); MFA step-up; soft-delete via `workspaces.deleted_at`; any other
table; `clients/` (generated); Angular/Vue (retired).

## 11. Dashboard / widget / CLI tasks

**Dashboard** (`pointer-dashboard/react`, after client regen; hook names as generated — verify):
1. `src/features/settings/DangerZoneCard.tsx` (new), rendered in `SettingsPage.tsx` after `WorkspaceNameCard` (`:986`) when `isAdmin && !isSuperAdmin && workspace.canManageLifecycle`
   (from `useGetApiAdminWorkspace`). Three rows: **Export data** → `getApiExport({ includePrivate: true })` → blob download named `pointer-export-workspace-<yyyymmdd>.json` (copy `ProjectsPage.tsx:950-965`;
   on the 5 000-comment refusal show the server message + link "Export per project" → `/projects`); caption "Comments and replies as JSON. Screenshots, project settings and members are not included."
   **Pause workspace / Resume** → `postApiAdminWorkspacePause` / `…Resume` (confirm via `ConfirmDialog`); **Delete workspace** → dialog.
2. Delete dialog, two steps: step 1 "Before you delete" — buttons **Export data** (same handler) and **Pause instead** (pauses, closes), and a secondary **Continue to delete**; step 2 — what happens
   (grace days, frozen meanwhile, admins notified, accounts created here that belong to no other workspace are deleted) and **Send confirmation e-mail** → `postApiAdminWorkspaceDeletionRequest` → toast `envelope.message`,
   card shows "Confirmation e-mail sent at hh:mm — valid 30 minutes" from `deletionRequestedAt` with a **Cancel request** button (`postApiAdminWorkspaceDeletionCancel` — voids the link, Opus LOW).
   A `429` (limiter) or the cooldown/daily-limit `400` shows `envelope.message` inline in the dialog, never a generic error (Gemini NIT; verify the global interceptor does not swallow 429). When `deletionScheduledFor` is set the card shows the date + **Cancel deletion** (`postApiAdminWorkspaceDeletionCancel`).
   When `pausedByOperator`: Resume and Delete disabled with the operator text.
3. `src/features/auth/ConfirmWorkspaceDeletionPage.tsx` (new, anonymous route `/confirm-workspace-deletion` in `App.tsx` beside `:58`; copy `DeleteAccountPage.tsx`): reads `?token=`; `postApiAuthWorkspaceDeletionPreview({ token })`
   → shows name, counts, `accountsDeletedWithWorkspace` (bold when `requesterAccountDeleted`), `wouldBeDeletedOn`; **Export first**: if the stored session's `workspaceId` equals the preview's → export button, else "Sign in to export" → `/login`;
   **Pause instead** → `postApiAuthWorkspaceDeletionPauseInstead({ token })` → done state; form: workspace-name input (must equal the shown name to enable the button) + password input when `requiresPassword` →
   **Delete workspace** (destructive) → `postApiAuthWorkspaceDeletionConfirm` → done state with the scheduled date and "Any admin can cancel in Settings until then". Invalid token → the server message + link to `/login`.
4. `src/components/WorkspaceStateBanner.tsx` (new) in `Shell.tsx` after `VerificationBanner` (`:200`): shown when `me.workspacePausedAt || me.workspaceDeletionScheduledFor`; text per state; admins (`canManageLifecycle`) get **Resume** / **Cancel deletion** buttons.
   `src/lib/api.ts` (`:209-221` pattern): on `423` with `x-workspace-paused: true` → dispatch `workspace-frozen` event → invalidate `/me` query + toast the server message.
   Expose `isFrozen = !!(me.workspacePausedAt || me.workspaceDeletionScheduledFor)` from the auth context; while frozen **hide** Invite, Add member, Import and New project, and disable
   create/edit buttons in the shared table/row-action components (Opus LOW); keep remove/disable/revoke enabled (they are allowed, §3.5).
5. `features/tenants/TenantsPage.tsx`: row items **Pause workspace** / **Resume workspace** / **Cancel scheduled deletion** (new hooks), a state badge from `pausedAt`/`pausedByOperator`/`deletionScheduledFor`;
   relabel the existing `disable` item "Disable admin login" (behaviour unchanged).
6. i18n `public/assets/i18n/{en,ar}.json`, namespace `workspaceLifecycle` (en / ar):
   `dangerZone` Danger zone / منطقة الخطر · `export` Export data / تصدير البيانات · `exportCaption` Comments and replies as JSON. Screenshots, project settings and members are not included. / التعليقات والردود بصيغة JSON. لا تشمل لقطات الشاشة وإعدادات المشاريع والأعضاء. ·
   `pause` Pause workspace / إيقاف مساحة العمل مؤقتًا · `pauseHint` Stops new feedback, CLI apply and changes. Keeps your projects, comments and settings; everyone can still sign in, read and export. / يوقف استقبال الملاحظات الجديدة وتطبيقها عبر CLI وأي تعديلات، ويحتفظ بمشاريعك وتعليقاتك وإعداداتك؛ ويظل بإمكان الجميع تسجيل الدخول والقراءة والتصدير. ·
   `resume` Resume workspace / استئناف مساحة العمل · `delete` Delete workspace / حذف مساحة العمل · `beforeDelete` Before you delete / قبل الحذف ·
   `pauseInstead` Pause instead / إيقاف مؤقت بدلًا من الحذف · `pauseInsteadHint` Pausing keeps your projects, comments and settings and can be undone at any time. / الإيقاف المؤقت يحتفظ بمشاريعك وتعليقاتك وإعداداتك ويمكن التراجع عنه في أي وقت. · `cancelRequest` Cancel request / إلغاء الطلب ·
   `continueDelete` Continue to delete / متابعة الحذف · `whatHappens` After you confirm from the e-mail, the workspace becomes read-only and is deleted after {{days}} days. Every admin is notified and can cancel. Accounts created in this workspace that belong to no other workspace are deleted too. / بعد التأكيد من البريد الإلكتروني تصبح مساحة العمل للقراءة فقط وتُحذف بعد {{days}} أيام. يُبلَّغ جميع المسؤولين ويمكنهم الإلغاء. تُحذف أيضًا الحسابات التي أُنشئت في مساحة العمل هذه ولا تنتمي إلى أي مساحة عمل أخرى. ·
   `sendEmail` Send confirmation e-mail / إرسال رسالة التأكيد · `emailSent` Confirmation e-mail sent at {{time}} — valid for 30 minutes. / أُرسلت رسالة التأكيد عند {{time}} — صالحة لمدة 30 دقيقة. ·
   `scheduled` This workspace will be deleted on {{date}}. / ستُحذف مساحة العمل هذه في {{date}}. · `cancelDeletion` Cancel deletion / إلغاء الحذف ·
   `bannerPaused` This workspace is paused — it is read-only. / مساحة العمل هذه موقوفة مؤقتًا — وهي للقراءة فقط. · `bannerPausedOperator` This workspace was paused by the platform operator. Contact support. / أوقف مشغّل المنصة مساحة العمل هذه مؤقتًا. تواصل مع الدعم. ·
   `bannerScheduled` This workspace is scheduled for deletion on {{date}} and is read-only. / مساحة العمل هذه مجدولة للحذف في {{date}} وهي للقراءة فقط. · `byOperator` the platform operator / مشغّل المنصة ·
   `confirmTitle` Delete workspace / حذف مساحة العمل · `confirmCounts` {{projects}} projects, {{comments}} comments, {{members}} members / {{projects}} مشاريع، {{comments}} تعليقات، {{members}} أعضاء ·
   `confirmAccounts` {{count}} accounts created in this workspace and belonging to no other workspace will be deleted. / سيُحذف {{count}} من الحسابات التي أُنشئت في مساحة العمل هذه ولا تنتمي إلى أي مساحة عمل أخرى. · `confirmYourAccount` Your own account was created in this workspace and belongs to no other, so it will be deleted too. / أُنشئ حسابك في مساحة العمل هذه ولا ينتمي إلى غيرها، لذا سيُحذف أيضًا. ·
   `typeName` Type the workspace name to confirm / اكتب اسم مساحة العمل للتأكيد · `password` Your password / كلمة المرور · `confirmButton` Delete workspace / حذف مساحة العمل ·
   `confirmDone` Deletion scheduled for {{date}}. Any admin can cancel it in Settings until then. / جُدول الحذف في {{date}}. يمكن لأي مسؤول إلغاؤه من الإعدادات حتى ذلك الحين. · `signInToExport` Sign in to export / سجّل الدخول للتصدير ·
   `pausedDone` The workspace is paused. Nothing was deleted. / أُوقفت مساحة العمل مؤقتًا. لم يُحذف أي شيء. · `tenantsDisableAdmin` Disable admin login / تعطيل دخول المسؤول.

**Widget:** task 17 (paused read-only mode; 423 handled once in `api()`; upload skipped when paused; en/ar `paused.notice`). **CLI:** task 18 (`apply` exits 2 before touching files;
`deployed` warns and exits 0; MCP returns a tool error).
**Apply skill** (`API/wwwroot/skill.md`): one line in the error section — "If the CLI reports the workspace is paused or scheduled for deletion, stop and tell the user; do not retry."

## 12. Cross-review adjudication (2026-09-24)

Reports: `docs/db/reviews/REVIEW-DB18-2026-09-24.md` (Gemini 3.1 Pro via agy; Opus code-reviewer relayed by the orchestrator). Every claim re-checked against
`main` @ `9aaf1d3` before its verdict; owner answers D18.1/.2/.8/.10 unchanged.

| Finding | Verdict (why) | Where changed |
|---|---|---|
| **Opus HIGH 1** — preview/E2 use the wrong "deleted accounts" predicate; real rule `owner_id == ws && DeletedAt == null` and no membership of **any** state elsewhere (`TenantService.cs:641-670`, `:654`) | **Accepted** — verified: `IgnoreQueryFilters`, no `LeftAt` filter; doc cited stale `:620-650`. One shared `IdentitiesDeletedWithWorkspace` query for delete + preview, count only (no names/e-mails), E2/i18n reworded, parity test | §2, §3.4 (shared query, preview DTO), §3.7 E2, §3.10 D18.10, §5 task 10, §6 9a, §7.4, §11 |
| **Opus HIGH 2** — freeze blocks security revocations | **Accepted** — verified `UsersController.cs:68,88`, `InvitesController.cs:41,56`; no admin key-revoke route exists (keys die with the membership). Method-level exemptions; `PATCH users/{id}` also grants, so the service refuses grants while frozen | §2, §3.5, §5 task 12, §6 9a + test 7, §7.4, §11 |
| **Opus HIGH 3** — operator pause during grace does not stop the delete | **Accepted** — the draft's due query and `StillDueAsync` ignored `PausedByOperator`. Added to due-ids, pre-check and locked re-check; "held" log | §3.4 job row, §3.10 D18.6, §4, §5 tasks 10/14, §6 9a, §7.4, §9.5 |
| Opus MEDIUM — confirm re-check reads the tracked (stale) entity | **Accepted** — EF identity resolution returns the tracked instance unchanged. Concurrency rule: lock, then first load / `ReloadAsync`, re-check on the fresh row | §3.4 concurrency rule + confirm/pause-instead rows, §6 9a |
| Opus MEDIUM + **Gemini LOW #1** — cancel races the job (`DbUpdateConcurrencyException` 500) | **Accepted** — cancel/operator cancel in the locked tx; gone → `Conflict(AlreadyDeleted)`; concurrency exception → `Conflict(StateChanged)` | §3.4, §5 task 6, §6 9a |
| Opus MEDIUM — one workspace can drain the 250/day global e-mail cap | **Accepted** — verified `EmailService.cs:17,26-33`. 5-min cooldown + ≤ 5 requests/24 h per workspace (audit count); limiter partitioned by `sub`+IP | §3.4 request row, §3.7, §5 tasks 6/13, §6 9a |
| Opus MEDIUM — job/anonymous queries need `IgnoreQueryFilters` + explicit predicate | **Accepted** — verified `AppDbContext.cs:88-90,328-332` (no tenant → no rows). Query rule stated once, tests with a no-tenant user | §2, §3.4 query rule, §5 tasks 8/14, §6 9a |
| Opus MEDIUM — 423 would break `pointer deployed` in customer CI | **Accepted** — verified `deployed.ts:57,74,113-140`. `deployed` warns + exit 0; exit 2 only for `apply`; MCP returns a tool error | §2, §3.6, §5 task 18, §6 9a, §11 |
| Opus MEDIUM — confirm password guessing bypasses the R5-59 per-identity lockout | **Accepted** — `ILoginAttemptLimiter` reused keyed by the identity e-mail; 5th failure spends the link | §2, §3.4 confirm row + ctor, §6 9a |
| Opus MEDIUM — §7.1 greps fail on `Down()` drops | **Accepted** — counts on the `Up()` slice (MigrationSafetyTests slicing) | §7.1 |
| Opus LOW — Events class-level exemption also opens `GET api/admin/events/summary` to key sessions | **Accepted** — verified `EventsController.cs:17-19,36-37`; method-level on `RecordEvent` | §2, §3.5, §5 task 12, §6 9a |
| Opus LOW — widget upload before POST; no 423 branch on other writes | **Accepted** — verified `element.ts:967` (`api()`), `:1786-1792` (upload via `pfFetch`). 423 handled once in `api()`, upload skipped when paused | §2, §5 task 17, §11 |
| Opus LOW — undocumented R16 departures (policy, state-based single-use) | **Accepted** — R16 amendment text added beside R19 | §3.7, §5 task 19 |
| Opus LOW — hard-coded `danger` limit → e2e 429s | **Accepted** — `Security:RateLimits:DangerPer10Min` (precedent `:32-35`), e2e override | §3.7, §5 tasks 13/16 |
| Opus LOW — key/invite login responses lack freeze fields (`AuthService.cs:1325,~1718`) | **Accepted** — verified `:1325` passes no workspace; both pass it | §2, §3.6, §5 task 13, §6 9a |
| Opus LOW — reminder racing cancel violates the schedule constraint | **Accepted, different mechanism** — `ExecuteSqlRawAsync` returns no row count, so a conditional UPDATE cannot tell whether it claimed; lock + fresh re-check instead | §2, §3.4 reminder row, §6 9a |
| Opus LOW — downtime across T-24 h deletes without a reminder | **Accepted** — missed reminder → E3 + reschedule `now + 24h`, audited `workspace.deletion_rescheduled` (System); the job only deletes rows whose reminder was sent (or grace < 2 d) | §3.4, §5 tasks 5/14, §6 9a |
| Opus LOW — non-race job failures retry forever silently | **Accepted** — skip vs error split, Critical after 3 consecutive failures, watched in §9.5 | §5 task 14, §9.5 |
| Opus LOW — no way to void a pending link | **Accepted** — cancel also clears a pending request; "Cancel request" button | §3.4 cancel row, §11 |
| Opus LOW — demo guard refuses a converted workspace in the 72 h re-verify window | **Accepted** — verified `Workspace.cs:26-28`; guard is `DemoExpiresAt != null && DemoConvertedAt == null` | §3.4 guard, §6 9a |
| Opus LOW — anonymous `api/auth/workspace-deletion/*` under open CORS | **Accepted** — verified `Program.cs:434-447`; added to `IsDashboardOnly` | §3.5, §5 task 12, §7.4 |
| Opus LOW — dashboard mutation controls stay enabled while frozen | **Accepted** — `isFrozen` from `/me`; hide Invite/Add member/Import/New project, disable create/edit | §11 task 4 |
| Opus LOW — listed test gaps | **Accepted** — all covered in §6 9a | §6 9a |
| Opus LOW — pause-instead on an already-paused workspace writes no audit row (StrictCoverage 500) | **Accepted** — verified `AuditCoverageFilter.cs:51`; exactly one row in both branches | §3.4 |
| Opus NIT — ctor lacks `ITenantService` and `UserNameResolver` | **Accepted in part** — `ITenantService` (and `ILoginAttemptLimiter`) added; **`UserNameResolver` rejected**: it is a static class (`UserNameResolver.cs:14`) | §2, §3.4 |
| Opus NIT — null password → 500; validator | **Accepted** | §3.4, §5 task 8, §6 9a |
| Opus NIT — typed-name compare (FormC, bidi marks) | **Accepted** | §3.4, §6 9a |
| Opus NIT — grace ≤ 1 d sends E3 right after E2 | **Accepted** — no reminder when grace < 2 d | §3.4 |
| Opus NIT — E4 recipients tracked in the delete context | **Accepted** — `AsNoTracking` projection | §3.4 |
| Opus NIT — retention keeps deleting while paused vs. "keeps everything" copy | **Accepted (reword)** — verified `RetentionService.cs:167-183`; jobs unchanged, copy says "keeps your projects, comments and settings" | §2, §3.6, §11 |
| Opus NIT — clamp 0..30 vs. "≤ ~37 d" | **Accepted** — clamp 0..14; privacy text from config | §3.4, §3.9, §5 tasks 16/19 |
| Opus NIT — operator resume clears the admin self-pause | **Accepted (documented, not changed)** — one pause slot keeps the constraint simple; the admin can pause again | §3.4, §3.10 D18.6 |
| **Gemini LOW #2** — state service reads soft-deleted rows | **Accepted** | §3.3, §6 9a |
| Gemini NIT — dashboard 429 handling on request | **Accepted** | §11 task 2 |
| Gemini NITs — key-session reads blocked, confirm vs operator pause, anonymous upload GET, erase-token cross-use | **Confirmations, no change** | — |


## 13. Release record (2026-09-24)

- Implemented on `feat/db-18-api` (backend) + `feat/db-18-clients` (widget, CLI), merged; code review by Gemini 3.8 Flash (agy) + Opus → `docs/db/reviews/REVIEW-DB18-CODE-2026-09-24.md`; fix pass fixed 3 shared BLOCKERs (anonymous/job `Workspaces` loads under the tenant filter) and the pre-existing `HardDeleteAsync` FK violation on soft-deleted/erased identities (`fk_users_workspaces_owner_id`, Sqlite repro). `dotnet test` 1407; widget 109, 67,210 B gz; CLI 230.
- **Local gate PASS** (all phases) incl. the new `e2e/mail/workspace-lifecycle-mail.spec.mjs` via Mailpit: E1 confirm link, E2 scheduled, E5 cancelled, pause-instead from a second E1; wrong password refused, link single-use, 423 + `X-Workspace-Paused` while paused/scheduled.
- **R11 rehearsal** on `pointer-20260923T232253Z-pre-deploy.dump` (80 migrations): only `…_AddWorkspacesPauseAndDeletionState` pending; applied; 8 columns + 3 check constraints; all 3 workspaces active; counts unchanged (3 workspaces / 7 users / 122 comments); down to `…_DropUsersLegacyDemoColumns` and back up clean.
- Not yet built: the dashboard half (§11 — danger-zone card, `/confirm-workspace-deletion` page, paused banner); until it ships, the e-mail link lands on a dashboard route that does not exist.
