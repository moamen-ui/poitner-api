# DB-11e — Contract: drop the legacy demo state on `users` and the `[Obsolete]` `{id:int}` tenant demo routes

Opened by DB-17 §9 step 7 and `DB-REVIEW-2026-09-22.md` "Status 2026-09-23 (evening)". This is the **R2 step 3 (contract)** of the column
move DB-17 started (`users` → `workspaces`). Rules: **R2** (contract step 3, marker), **R5** (rollback, irreversible data → labelled dump),
R6, **R7** (contract deploy `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11e`; `[ContractMigration("DB-11e")]`), R7.1 (**not batched**),
R8 (no new entity; existing DB-17 tenancy tests stay green), **R10** (no migration id, table or surviving column renamed; historical migrations
never edited), **R11** (rehearsal on a same-day prod dump in a throwaway Postgres 15), **R13** (read the generated migration + snapshot diff), R15, R17
(two `[Audited]` actions removed with their endpoints; no new action). **Class: Contract — Destructive (four columns, one index).**

**Owner approval (verbatim, owner Moamen, 2026-09-23 — paste into the PR description):** `Approved to drop users.expires_at, IX_users_expires_at, users."DemoExtended", users."DemoCommentCapOverride", users."DemoTtlHoursOverride" and the int tenant routes (DB-11e), 2026-09-23.`

**Numbering note (read this first).** "DB-11e" was pencilled in by DB-11a/c/d (`DB-11a…md:12,735`, `DB-11c…md:454`, `DB-11d…md:12,157`,
`Domain/Entity/User.cs:14`, `SCHEMA.md:79`) for the *DB-11a* legacy-column contract (`users.owner_id`, `role_id`, `approval_status`,
`ux_users_email_owner_live`). That contract is **not** in this doc and becomes **DB-11f** (not written): `users.owner_id` still has live readers —
`TenantService.HardDeleteAsync` reads and rewrites it (`TenantService.cs:664-681`), `DemoService.ProvisionAsync` writes it (`DemoService.cs:154`) and
the DB-17 backfill keys on it. Historical docs are not rewritten; this doc's task 11 fixes the two live pointers (`User.cs:14`, `SCHEMA.md`) and the
review row records the renumber.

**Status: written 2026-09-23, not implemented.** Verified against `pointer-api` @ `9de4ba6` (79 migrations, newest `20260923155947_BackfillWorkspacesDemoState`)
and `pointer-dashboard` @ `600ab8f`.

## 1. Goal

DB-17 made `workspaces.demo_*` the only authority for demo TTL, extension and overrides, and for one release kept dual-writing the old shape on the
demo admin's `users` row plus two `{id:int}` operator routes for the not-yet-deployed dashboard. Both halves of DB-17 are now in production and
the dashboard calls only the `{workspaceId:guid}` routes, so the old shape has **writers but no readers**. This doc removes the four columns, their
index, the `User` properties and mappings, every dual-write, and the two obsolete routes. User-visible reason: none — the demo, the countdown, extend
and convert behave exactly as today; the operator Tenants page is unchanged. It removes the last place where one demo fact could hold two values,
and the last super-admin action keyed on a `users.id`.

## 2. Prerequisites (verified facts, 2026-09-23 @ `9de4ba6`)

**Schema being dropped** (snapshot `Infrastructure/Migrations/AppDbContextModelSnapshot.cs`, entity block starting `:2119`)
- `expires_at timestamptz NULL` — `User.ExpiresAt` (`Domain/Entity/User.cs:52`), mapped `UserMapping.cs:58` (`HasColumnName("expires_at")`), index
  `IX_users_expires_at` from `UserMapping.cs:59` (`b.HasIndex(x => x.ExpiresAt)`); created by `20260629155415_AddDemoColumns.cs:15,28-30`. Snapshot `:2184-2186`, index `:2264`.
- **PascalCase, no `HasColumnName`** (quote them in SQL), created by `20260701121004_AddDemoTenantConfig.cs:13-30`, snapshot `:2155-2162`:
  `"DemoCommentCapOverride" integer NULL` (`User.cs:57-58`), `"DemoExtended" boolean NOT NULL DEFAULT false` (`User.cs:54-55`),
  `"DemoTtlHoursOverride" integer NULL` (`User.cs:60-61`).
- **Kept on `users`:** `is_demo` (`User.cs:51`, identity fact — readers `EmailVerificationService`, `RequireVerifiedEmailFilter`, `UserMapper.cs:32`, password
  reset), `recipient_email` (`User.cs:63-64`, read by `DemoService.WarnExpiringAsync :566`), `owner_id` (`User.cs:50`, DB-11f).

**Remaining writers of the old shape (the complete list — no reader exists)**
- `Application/Services/Implementation/DemoService.cs:158-160` — provision: comment + `ExpiresAt = expiresAt,` in the `new User { … }` initializer.
- `DemoService.cs:414-417` — `UpgradeAsync`: `user.ExpiresAt = null; user.DemoExtended = false; user.DemoCommentCapOverride = null; user.DemoTtlHoursOverride = null;`.
- `DemoService.cs:660-663` — `ExtendAsync`: comment, `user.ExpiresAt = workspace.DemoExpiresAt;`, `user.DemoExtended = true;`, `_unitOfWork.Repository<User>().Update(user);`.
  (`user` is still needed above for the membership check `:617-630`.)
- `Application/Services/Implementation/TenantService.cs:384-391` — `ExtendDemoAsync(Guid)`: `var admin = await _memberships.CurrentAdminAsync(workspaceId); if (admin != null) { … ExpiresAt … DemoExtended = true; … Update(adminIdentity); }`; comment `:351-352`.
- `TenantService.cs:445-452` — `SetDemoConfigAsync(Guid, …)`: the same `admin`/`adminIdentity` block writing the two overrides.
- Comments that describe the dual-write: `Application/Services/Interfaces/ITenantService.cs:15-16,19-20`, `Domain/Entity/Workspace.cs:30-31`, `Infrastructure/Mappings/WorkspaceMapping.cs:31`.
- The grep in the task brief also hits many **other** types' `ExpiresAt` — invites (`InviteService.cs:267,281,316,421,1241,1272,1443,1467,1473,1515`,
  `TenantInviteService.cs:66,89,134,195`, `UnitOfWork.cs:46,61`, `RetentionService.cs:360`), quick-access links (`AuthService.cs:1654`), device logins
  (`DeviceLoginService.cs:57-306`), impersonation sessions (`ImpersonationService.cs:167,253`, `ImpersonationSweepService.cs:73,82`, `ImpersonationLiveness.cs:31`),
  their mappings (`InviteMapping.cs:40`, `QuickAccessLinkMapping.cs:33`, `DeviceLoginMapping.cs:37`, `ImpersonationSessionMapping.cs:30`), and the **workspace**
  columns of the same name (`Workspace.cs:37-38`, `WorkspaceMapping.cs:38-39`, `CommentService.cs:175,181`, `ExportImportService.cs:597,603`,
  `DemoService.cs:458-459,575,651`, `TenantService.cs:159-161,371,437-442`) and the audit constants `AuditActions.DemoExtended` (`AuditActions.cs:55`) /
  `TenantDemoExtended` (`:95`). **None of these is touched.** No raw SQL outside migrations references the four columns (`grep` for `FROM users`,
  `UPDATE users`, `FromSql` in `Application API Infrastructure` → 0; `e2e/`, `scripts/`, `cli/src` → 0).

**Obsolete routes** — `API/Controllers/Admin/TenantsController.cs`
- `:122-138` comment + `[Obsolete(…)] [HttpPost("{id:int}/extend")] [Audited(AuditActions.TenantDemoExtended)] … ExtendDemo(int id)`.
- `:140-158` `[Obsolete(…)] [HttpPatch("{id:int}/demo-config")] [Audited(AuditActions.TenantDemoConfigChanged)] … SetDemoConfig(int id, …)`.
- `:190-221` `private async Task<Guid?> ResolveWorkspaceIdFromUserIdAsync(int id)` — the only user of the constructor parameters
  `IMembershipService memberships` (`:23`, used `:198`) and `IUnitOfWork unitOfWork` (`:24`, used `:214-218`), both added by DB-17 (`da8cb3a`, `f5288f2`),
  and of `using Microsoft.EntityFrameworkCore;` (`:3`, `FirstOrDefaultAsync`/`IgnoreQueryFilters` at `:215-218`).
- **Kept:** `:160-188` `ExtendDemo(Guid workspaceId)` `[HttpPost("{workspaceId:guid}/extend")]` and `SetDemoConfig(Guid workspaceId, …)`
  `[HttpPatch("{workspaceId:guid}/demo-config")]` (comment `:160` mentions "beside the int ones above").
- Controller tag `[Tags("Tenants")]` (`:17`) is in `orval.config.ts` `filters.tags`. No test constructs `TenantsController`; the only test that reflects
  on it is `Tests/Db17TenantsControllerRoutesTests.cs:1-50` (four facts: two int-route `[Obsolete]` facts, two Guid-route facts).

**DTOs** — no DTO field exists only for the old shape. `Application/DTOs/Tenant/TenantResponse.cs:42-46` (`IsDemo`, `ExpiresAt`, `DemoExtended`,
`DemoCommentCapOverride`, `DemoTtlHoursOverride`) are **already sourced from the workspace** (`TenantService.cs:155-161`: `IsDemo = w.DemoExpiresAt != null,
ExpiresAt = w.DemoExpiresAt, DemoExtended = w.DemoExtendedAt != null, …`) and **the deployed dashboard reads all five**
(`pointer-dashboard/react/src/features/tenants/TenantsPage.tsx:377-378,521,537-542`) → **keep them unchanged** (comment-only edit, task 7).
`DemoSessionResponse.ExpiresAt`, `DemoStatusResponse.ExpiresAt`, `MeResponse.DemoExpiresAt/DemoCanExtend` are workspace/response values — untouched.

**Dashboard (deployed) no longer uses the int routes** — `pointer-dashboard` @ `600ab8f` (DB-17 dashboard commit `98056df`), `react/package.json:13`
`"@moamen-ui/pointer-react": "^1.0.48"`; `TenantsPage.tsx:16-17` import `usePostApiAdminTenantsWorkspaceIdExtend`, `usePatchApiAdminTenantsWorkspaceIdDemoConfig`
(used `:361,382`); `grep -rn "usePostApiAdminTenantsIdExtend\|usePatchApiAdminTenantsIdDemoConfig\|postApiAdminTenantsIdExtend\|patchApiAdminTenantsIdDemoConfig" react/src` → **0**.
`API/wwwroot/admin` (the fallback page) and `e2e/` never call the demo routes (`grep` → 0). `openapi.json` is gitignored (`.gitignore:16`).

**Tests that set or assert the dropped `User` members** (found by scanning every `new User { … }` block and every `.ExpiresAt` in `Tests/`):
- `Tests/DemoUpgradeTests.cs:227` (`ExpiresAt = expires,` — `expires` stays used at `:240`), `:329-332` (four asserts), `:194` doc-comment.
- `Tests/Db17DemoServiceTests.cs:224-225` (`ExpiresAt = expires, DemoTtlHoursOverride = ttlOverride,` in the seeded `User`; both variables stay used at `:240-241`),
  `:283-285` (dual-write assert), `:556-558` (`var user = …; user.ExpiresAt = null; db.SaveChanges();` + comment `:549-550`), `:960-972`
  (`Provision_SetsWorkspaceDemoExpiresAt_AndUserExpiresAt_SameInstant` asserts `user.ExpiresAt == workspace.DemoExpiresAt`), `:948` comment.
- `Tests/Db17DemoAuthTests.cs:231` (`ExpiresAt = demoExpiresAt,` — parameter stays used `:243`).
- `Tests/DemoServiceAnalyticsFailureTests.cs:238` (`ExpiresAt = expiresAt,` — variable stays used `:251`).
- `Tests/EmailVerificationTests.cs:811` (`ExpiresAt = DateTime.UtcNow.AddHours(1),`).
- `Tests/Db17HardDeleteAndCleanupTests.cs:342` (`ExpiresAt = null,`) + comment `:327-328`.
- `Tests/Db17DemoCapTests.cs:127,382` (`DemoCommentCapOverride = 1000,` on the seeded `User`) + comments `:17-21,114-115,369-370`.
- Comment-only: `Tests/Db17TenantServiceDemoOperatorTests.cs:22,312-313`.
- Every other `ExpiresAt` hit in `Tests/` is an `Invite`, `QuickAccessLink`, `DeviceLogin`, `ImpersonationSession` or a DTO — untouched.

**Tooling / conventions**
- `just migrate name="…"` = `dotnet ef migrations add … -p Infrastructure -s API` (`justfile:6`); `just test` = `dotnet test`; `just fmt` = `dotnet csharpier .`.
- Marker + attribute precedent for a column drop: `Infrastructure/Migrations/20260922115038_DropUsersLegacyApiKey.cs:8,11` (DB-07). Guard:
  `Tests/MigrationSafetyTests.cs:75-82` (risky-op + marker regexes), `:113` (risky ops need a marker), `:146-172` (marker ⇔ `[ContractMigration]`).
  Attribute: `Infrastructure/Migrations/ContractMigrationAttribute.cs`.
- Gate: `scripts/deploy-api.sh:33-72` (refuses a pending `[ContractMigration]` unless `POINTER_APPLY_CONTRACT=1`; then stops `api`, dumps under
  `POINTER_CONTRACT_LABEL`, boots with `DBApplyContractMigrations=true`). Restore: `DEPLOY.md:169-230`. Local e2e gate: `scripts/local-e2e-gate.sh [<worktree>]`.
- Deployed dashboard on the VM: `~/pointer-api/dashboard/react` (copied from `~/pointer-dashboard/react/dist`, `scripts/deploy-dashboards.sh:29-31`).
- Engine: `postgres:15` (`docker-compose.yaml:3`, `docker-compose.prod.yml:6`). `users` holds single-digit live rows (DB-11a census: 7 users; `DEPLOY.md:192`).

## 3. Design

### 3.1 Target shape

`users` loses exactly: index `IX_users_expires_at`, columns `expires_at`, `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"`.
Nothing else on any table changes. `User` loses `ExpiresAt`, `DemoExtended`, `DemoCommentCapOverride`, `DemoTtlHoursOverride` (with their doc-comments);
`UserMapping` loses the two lines that map `ExpiresAt` and index it (the three PascalCase columns were mapped by convention — deleting the properties
removes them). `Workspace` and its six `demo_*` columns + `ix_workspaces_demo_expires_at` are **unchanged**.

### 3.2 Migration `DropUsersLegacyDemoColumns` (scaffolded; one migration, nothing else in it)

Expected `Up()` — exactly these five operations (EF may order the four `DropColumn` calls differently; `DropIndex` comes first):
```csharp
migrationBuilder.DropIndex(name: "IX_users_expires_at", table: "users");
migrationBuilder.DropColumn(name: "DemoCommentCapOverride", table: "users");
migrationBuilder.DropColumn(name: "DemoExtended", table: "users");
migrationBuilder.DropColumn(name: "DemoTtlHoursOverride", table: "users");
migrationBuilder.DropColumn(name: "expires_at", table: "users");
```
Expected `Down()` — the scaffolded inverse, recreating the columns **empty** (values are not recoverable, §8):
```csharp
migrationBuilder.AddColumn<int>(name: "DemoCommentCapOverride", table: "users", type: "integer", nullable: true);
migrationBuilder.AddColumn<bool>(name: "DemoExtended", table: "users", type: "boolean", nullable: false, defaultValue: false);
migrationBuilder.AddColumn<int>(name: "DemoTtlHoursOverride", table: "users", type: "integer", nullable: true);
migrationBuilder.AddColumn<DateTime>(name: "expires_at", table: "users", type: "timestamp with time zone", nullable: true);
migrationBuilder.CreateIndex(name: "IX_users_expires_at", table: "users", column: "expires_at");
```
Class header (copy `20260922115038_DropUsersLegacyApiKey.cs:7-12`'s shape): `[ContractMigration("DB-11e")]` on the class; on the line directly above
`/// <inheritdoc />` of `Up()`:
`// DB-RULES: R2 contract approved 2026-09-23 by Moamen (owner, verbatim: "Approved to drop users.expires_at, IX_users_expires_at, users.\"DemoExtended\", users.\"DemoCommentCapOverride\", users.\"DemoTtlHoursOverride\" and the int tenant routes (DB-11e), 2026-09-23."; docs/db/execution/DB-11e-drop-legacy-demo-state.md)`

Locking: `ALTER TABLE users DROP COLUMN` / `DROP INDEX` are catalog-only in Postgres (no table rewrite), ACCESS EXCLUSIVE for milliseconds on a
single-digit-row table; the API is stopped anyway (R7). R4 concurrent mode: not applicable.

Historical migrations that reference these columns (`20260629155415_AddDemoColumns`, `20260701121004_AddDemoTenantConfig`,
`20260923155947_BackfillWorkspacesDemoState` — its SQL reads `u.expires_at`, `u."DemoExtended"`, …) are **not edited** (R10): applied in order from an
empty database the columns still exist when they run (DB-10 CI proves it).

### 3.3 Code: remove every dual-write; nothing reads the old shape today, so no read switches

| Site | Today | After |
|---|---|---|
| `DemoService.ProvisionAsync :158-160` | comment + `ExpiresAt = expiresAt,` on `new User` | both lines' comment and the assignment deleted; `OwnerId = workspaceId` (`:154`, DB-11f) and `RecipientEmail` stay; `expiresAt` stays used by the `Workspace` (`:171`) and the response (`:344`) |
| `DemoService.UpgradeAsync :414-417` | nulls the four user members | four lines deleted; `user.IsDemo = false;` (`:413`) and everything after stays; comment `:398-400` → `// 4. Guard: an already-expired demo cannot be salvaged. The WORKSPACE is the only authority (DB-17; the users columns were dropped by DB-11e).` |
| `DemoService.ExtendAsync :660-663` | dual-writes the identity | four lines deleted (comment, two assignments, `_unitOfWork.Repository<User>().Update(user);`); the workspace update + single `SaveChangesAsync` (`:665`) stay |
| `TenantService.ExtendDemoAsync :384-391` | `CurrentAdminAsync` + identity dual-write | the whole `var admin …` statement and `if (admin != null) { … }` block deleted; comment `:351-352` → `// DB-17 §3.3 / DB-11e: the WORKSPACE is the only demo authority.` |
| `TenantService.SetDemoConfigAsync :445-452` | same block for the overrides | block deleted |
| `ITenantService.cs:15-16,19-20` | "dual-writes the current admin identity for one release" | `/// <summary>DB-17 §3.3: keyed on the workspace id (the only demo authority since DB-11e).</summary>` for both |
| `TenantsController.cs:122-158,190-221` | two `[Obsolete]` int actions + resolver | deleted; ctor loses `IMembershipService memberships,` and `IUnitOfWork unitOfWork` (line `:22` ends `impersonationService` with **no** trailing comma); comment `:160` → `// DB-17 §3.3 (F9 precedent): workspace-keyed demo routes (the {id:int} pair was removed by DB-11e).` |

Unrouted request `POST /api/admin/tenants/5/extend` after the deploy: no endpoint matches (`{workspaceId:guid}` rejects `5`) → **404** (today: 401
unauthenticated / 200 for a super admin). That is the mechanical proof the route is gone (§7, §9).

### 3.4 What happens to every existing row

`users`: every row loses four values. For live, unconverted demos these are copies of `workspaces.demo_*` written at the same instant
(`DemoService.cs:142,160,171`; extension/config dual-writes) — the §9 pre-checks P1/P2 prove it on production before the drop. For converted
identities they were already `NULL/false/NULL/NULL` (`DemoService.cs:414-417`). For soft-deleted rows they are dead history. `workspaces`, memberships,
comments, projects, audit rows: untouched. The one irreversible effect: the dropped values exist afterwards only in the `pre-db11e` dump.

### 3.5 What is deliberately not changed

`users.is_demo`, `users.recipient_email`, `users.owner_id/role_id/approval_status` + `ux_users_email_owner_live` (DB-11f), `TenantResponse` (all fields,
incl. legacy `Id`), `workspaces.demo_*`, audit action strings `tenant.demo_extended` / `tenant.demo_config_changed` / `demo.extended` (R10 — still used by
the Guid routes and `POST /api/demo/extend`), `DemoCleanupService`, `HardDeleteAsync`, the `AuthService` expiry guard, every historical migration.

## 4. Safety classification

**Contract (R2 step 3) — Destructive.** Drops four columns and one index; the values are unrecoverable except from the labelled dump (R5). Justified
because the old shape has had zero readers since DB-17 deployed (2026-09-23 19:18 UTC, `f5288f2`) and the deployed dashboard no longer calls the
routes (§2). Marker (verbatim, §3.2) + `[ContractMigration("DB-11e")]` → the DB-09 gate refuses an ordinary deploy; ships **only** as
`POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11e bash scripts/deploy-api.sh` (R7: API stopped → dump `pre-db11e` → boot applies it).
**R7.1: not batched** — ship alone, one label. **Owner approval: see the header line** (given verbatim 2026-09-23).
Tenancy (R8): no new entity; the operator routes that remain are super-admin + workspace-keyed; DB-17's cross-tenant tests stay the proof (§6).

## 5. File-level tasks

Work on a branch from `main`. One commit for code + migration, one for docs is fine. Do **not** touch any file not listed.

1. `Domain/Entity/User.cs` — delete lines `:52-61` exactly: `public DateTime? ExpiresAt { get; set; }`, the blank line, the `DemoExtended` summary +
   property, blank, the `DemoCommentCapOverride` summary + property, blank, the `DemoTtlHoursOverride` summary + property. Leave one blank line between
   `public bool IsDemo { get; set; }` and the `RecipientEmail` summary. At `:14` replace `Dropped by DB-11e.` with
   `Dropped by DB-11f (the DB-11a legacy-column contract; DB-11e dropped the demo columns instead).`
2. `Infrastructure/Mappings/UserMapping.cs` — delete `:58` `b.Property(x => x.ExpiresAt).HasColumnName("expires_at");` and `:59` `b.HasIndex(x => x.ExpiresAt);`. Nothing else.
3. `Domain/Entity/Workspace.cs:30-31` — replace the sentence `users.expires_at / "DemoExtended" / "DemoCommentCapOverride" / "DemoTtlHoursOverride" are dual-written until DB-11e drops them (DB-RULES R2).`
   with `The former users.expires_at / "DemoExtended" / "DemoCommentCapOverride" / "DemoTtlHoursOverride" copies were dropped by DB-11e; these columns are the only demo state.`
   `Infrastructure/Mappings/WorkspaceMapping.cs:31` — `(dual-written for one release — R2)` → `(the users copies were dropped by DB-11e — R2 contract)`.
4. `Application/Services/Implementation/DemoService.cs` — the three rows of §3.3 (`:158-160`, `:398-400` comment, `:414-417`, `:660-663`).
5. `Application/Services/Implementation/TenantService.cs` — §3.3 rows (`:351-352` comment, `:384-391`, `:445-452`).
   `Application/Services/Interfaces/ITenantService.cs:15-16,19-20` — §3.3 row.
6. `API/Controllers/Admin/TenantsController.cs` — delete `:122-158` (comment + both `[Obsolete]` actions) and `:190-221` (resolver incl. its doc-comment);
   ctor per §3.3; comment `:160` per §3.3. Then `grep -n "unitOfWork\|memberships\|IgnoreQueryFilters\|FirstOrDefaultAsync\|IUnitOfWork\|ICurrentUser" API/Controllers/Admin/TenantsController.cs`
   → must print nothing; if so delete `using Microsoft.EntityFrameworkCore;` (`:3`) and `using Pointer.Application.Abstractions;` (`:5`); if `dotnet build` then
   fails on a missing type, restore only the using it names.
7. `Application/DTOs/Tenant/TenantResponse.cs:40-41` — comment becomes `// Demo tenants (sourced from workspaces.demo_* — DB-17; the users copies were dropped by DB-11e): surfaced so the super-admin UI can offer a one-time "Extend demo" action`
   + unchanged second line. **No property change.**
8. `dotnet build` → every remaining compile error must be in a `Tests/` file listed in §2; fix each per task 10. An error anywhere else (Application/API/
   Infrastructure) means a reader this doc missed → **stop and report**, do not "fix" it.
9. `just migrate name="DropUsersLegacyDemoColumns"` → `Infrastructure/Migrations/<yyyyMMddHHmmss>_DropUsersLegacyDemoColumns.cs` + `.Designer.cs` + snapshot.
   **Read the migration against §3.2** (R13): `Up()` = 1 `DropIndex` + 4 `DropColumn` on `users`, `Down()` = 4 `AddColumn` + 1 `CreateIndex`, nothing on any
   other table. **Diff the snapshot:** only the `Pointer.Domain.Entity.User` block changes (four `b.Property<…>` blocks and `b.HasIndex("ExpiresAt");` removed).
   Any other difference → stop and report (R13, R15 — never hand-edit the snapshot/Designer). Then add the marker line and `[ContractMigration("DB-11e")]`
   exactly as §3.2 (the file is in namespace `Pointer.Infrastructure.Migrations`, same as the attribute — no `using` needed).
10. Tests (§6).
11. Docs (same PR): `docs/db/SCHEMA.md` — header note `:46-47` becomes `… backfill tagged live demos only. **DB-11e (<deploy date>):** users.expires_at / "DemoExtended" / "DemoCommentCapOverride" / "DemoTtlHoursOverride" and IX_users_expires_at dropped; the {id:int} tenant demo routes removed.`;
    `:49` `(79 migrations)` → `(80 migrations)` after deploy; `workspaces` row `:78` last sentence → `The users copies were dropped by DB-11e.`; `users` row `:79`: replace both
    `dropped by DB-11e` (index cell and `owner_id/role_id/approval_status` sentence) with `dropped by DB-11f`, delete `, \`expires_at\`, caps` from "Carries demo fields (…)",
    and replace "these columns are dual-written for one release and dropped by DB-11e" with "these columns were dropped by DB-11e". `docs/db/DB-REVIEW-2026-09-22.md` §7 DB-11e row status (task for the release, §9 step 8).
12. `just fmt`; `just test`; `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` → no changes.

## 6. Tests

**Remove / edit (compile fixes, keep every test's intent):**
1. `Tests/Db17TenantsControllerRoutesTests.cs` — **delete the file** (its int-route facts are obsolete; the Guid facts move to test 6 below).
2. `Tests/DemoUpgradeTests.cs` — delete `:227`; delete `:329-332` and in their place assert the workspace side:
   `var ws = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == demoWorkspaceId); Assert.Null(ws.DemoExpiresAt); Assert.NotNull(ws.DemoConvertedAt); Assert.Null(ws.DemoCommentCapOverride); Assert.Null(ws.DemoTtlHoursOverride);`
   (`demoWorkspaceId` is the local already passed to `UpgradeAsync` at `:316`); `:194` doc-comment: drop `(DemoExpiresAt = the same instant as the legacy \`users.expires_at\`, dual-write shape)`.
3. `Tests/Db17DemoServiceTests.cs` — delete `:224-225`; delete `:283-285`; in `Upgrade_ExpiredWorkspace_Fails_DemoExpired` delete `:556-558` (`var user…`, `user.ExpiresAt = null;`, `db.SaveChanges();`)
   and change the comment `:549-550` to `// The workspace's own TTL lapsed — the workspace is the only authority (DB-11e).`; rename `Provision_SetsWorkspaceDemoExpiresAt_AndUserExpiresAt_SameInstant`
   → `Provision_SetsWorkspaceDemoExpiresAt_SameInstantAsResponse` and replace `Assert.Equal(user.ExpiresAt, workspace.DemoExpiresAt);` with
   `Assert.Equal(result.Data!.ExpiresAt, workspace.DemoExpiresAt);` (keep the `user` lookup — it finds the workspace via `user.OwnerId`); `:948` comment `dual-write` → `workspace TTL`.
4. `Tests/Db17DemoAuthTests.cs:231`, `Tests/DemoServiceAnalyticsFailureTests.cs:238`, `Tests/EmailVerificationTests.cs:811`, `Tests/Db17HardDeleteAndCleanupTests.cs:342` — delete the `ExpiresAt = …,` line;
   `Db17HardDeleteAndCleanupTests.cs:327-328` comment → `// The workspace column alone selects and deletes it (there is no users column any more — DB-11e).`
5. `Tests/Db17DemoCapTests.cs` — delete `:127` and `:382` (`DemoCommentCapOverride = 1000,`); delete the two-line comments `:114-115`, `:369-370`; replace the class summary text lines `:17-21` (keep `<summary>`/`</summary>`) with
   `/// DB-17 review finding #6: the demo comment cap (create + import) reads the WORKSPACE'S <c>DemoCommentCapOverride</c> — the only copy since DB-11e dropped the users column.`
   `Tests/Db17TenantServiceDemoOperatorTests.cs:22,312-313` — `dual-write the SAME one-extension-total flag` → `share the SAME one-extension-total flag (workspaces.demo_extended_at)`.

**Add** `Tests/Db11eLegacyDemoStateRemovedTests.cs` (namespace `Pointer.Tests`; copy the `Method(...)` helper from the deleted route test and the `Ctx(dbName)`
InMemory builder + `FakeCurrentUser` from `Tests/Db17DemoServiceTests.cs:161-166`):
6. `TenantsController_GuidDemoRoutes_Present_NotObsolete_AndAudited` — the two Guid facts from the deleted file (`ExtendDemo(Guid)`, `SetDemoConfig(Guid)`: `[Obsolete]` null, `[Audited]` non-null).
7. `TenantsController_HasNoIntKeyedDemoRoutes` — `typeof(TenantsController).GetMethods(Public|Instance|DeclaredOnly)`: no method named `ExtendDemo`/`SetDemoConfig` whose first
   parameter is `int`; no `HttpMethodAttribute` on any method whose `Template` contains `{id:int}/extend` or `{id:int}/demo-config`; no method carries `ObsoleteAttribute`;
   and `typeof(TenantsController).GetMethod("ResolveWorkspaceIdFromUserIdAsync", NonPublic|Instance)` is null.
8. `User_HasNoLegacyDemoMembers` — for each of `ExpiresAt`, `DemoExtended`, `DemoCommentCapOverride`, `DemoTtlHoursOverride`: `typeof(User).GetProperty(name)` is null **and**
   `db.Model.FindEntityType(typeof(User))!.FindProperty(name)` is null (catches a shadow property re-introduced by a mapping); and
   `FindEntityType(typeof(User))!.GetIndexes()` has none whose properties include a property named `ExpiresAt`. `IsDemo` and `RecipientEmail` **are** present (guards over-deletion).
9. **Tenancy invariant (R8)** — no new entity, so no new filter test; the existing `Db17DemoServiceTests.Extend_TenantB_Admin_CannotExtendA` and
   `Upgrade_TenantB_Identity_CannotConvertA`, `Db17DemoAuthTests.*ExpiredDemo_Refused*`, `Tests/TenantQueryFilterTests.cs` must pass **unchanged** (list them in the PR as run).
10. **Existing data survives** — Postgres-only (the migration is DDL on the real schema), proven in the R11 rehearsal §7 criterion 7 (seed a live, extended demo
    through the old API → migrate → the same credentials log in with the same `demoExpiresAt`), pasted into the PR, as DB-17 §13 did.
11. Guards that must pass unchanged: `Tests/MigrationSafetyTests.cs` (marker + attribute agree, frozen ids), `Tests/AuditCoverageTests.cs` (two audited actions removed with their endpoints — no gap).

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect | tail -2` → `…_BackfillWorkspacesDemoState`, `…_DropUsersLegacyDemoColumns`; total 80.
2. On the new migration file: `grep -c "DropColumn(" ` → 4; `grep -c "DropIndex(" ` → 1; `grep -c "AddColumn<" ` → 4; `grep -c "CreateIndex(" ` → 1; `grep -c 'table: "users"'` → 10;
   `grep -c 'ContractMigration("DB-11e")'` → 1; `grep -c "R2 contract approved 2026-09-23 by Moamen"` → 1; `grep -c "\.Sql(" ` → 0.
3. `grep -cE "ExpiresAt|DemoExtended|DemoCommentCapOverride|DemoTtlHoursOverride" Domain/Entity/User.cs Infrastructure/Mappings/UserMapping.cs` → `0` each.
   `awk '/modelBuilder.Entity\("Pointer.Domain.Entity.User", b =>/{f=1} f&&/^            }\);/{f=0} f' Infrastructure/Migrations/AppDbContextModelSnapshot.cs | grep -cE '"ExpiresAt"|"DemoExtended"|"DemoCommentCapOverride"|"DemoTtlHoursOverride"'` → `0`.
4. `grep -cE "user\.(ExpiresAt|DemoExtended|DemoCommentCapOverride|DemoTtlHoursOverride)" Application/Services/Implementation/DemoService.cs` → `0`;
   `grep -cE "^\s+ExpiresAt = expiresAt," Application/Services/Implementation/DemoService.cs` → `1` (only the `DemoSessionResponse` initializer, today `:344`; was 2);
   `grep -c "Repository<User>().Update(user)" Application/Services/Implementation/DemoService.cs` → `1` (only `UpgradeAsync`, today `:431`; was 2);
   `grep -c "adminIdentity" Application/Services/Implementation/TenantService.cs` → `0`; `grep -ci "dual-writ" Application/Services/Implementation/DemoService.cs Application/Services/Implementation/TenantService.cs Application/Services/Interfaces/ITenantService.cs` → `0` each.
5. `grep -cE "\{id:int\}/extend|\{id:int\}/demo-config|Obsolete\(|ResolveWorkspaceIdFromUserIdAsync|IUnitOfWork|IMembershipService" API/Controllers/Admin/TenantsController.cs` → `0`;
   `grep -cE "\{workspaceId:guid\}/extend|\{workspaceId:guid\}/demo-config" API/Controllers/Admin/TenantsController.cs` → `2`; `test ! -f Tests/Db17TenantsControllerRoutesTests.cs`.
6. `just test` green (tests 6–8 new; all Db17* and DemoUpgrade/DemoSession/EmailVerification tests green); `has-pending-model-changes` → no changes; CI DB-10 job green
   (apply from empty + newest `Down()`/`Up()` round-trip).
7. **R11 rehearsal on a same-day prod dump restored into a throwaway Postgres 15** (commands below), output pasted into the PR:
   - before `database update`: P1–P4 of §9 print the same results as production (P1 **0 rows**, P4 4 columns + 1 index + 0 dependent views, 79 migrations);
   - existing-data seed: on the **pre-DB-11e** code (`main` before this PR) booted against the rehearsal DB, `POST /api/demo {"email":"rehearsal@example.com"}` → save `email`,
     `password`, `expiresAt`; log in, `POST /api/demo/extend` → 200; record `GET /api/auth/me` → `demoExpiresAt` (= T), `demoCanExtend: false`; unauthenticated
     `curl -s -o /dev/null -w '%{http_code}' -X POST http://localhost:8095/api/admin/tenants/1/extend` → `401`; census `SELECT (SELECT count(*) FROM users), (SELECT count(*) FROM workspaces), (SELECT count(*) FROM workspace_memberships), (SELECT count(*) FROM comments);`;
   - `dotnet ef migrations script --idempotent -p Infrastructure -s API -o /tmp/db11e-pending.sql` — read it: only the new migration is pending, it is the §3.2 DDL;
   - `database update` applies it; `\d users` shows none of the four columns and no `IX_users_expires_at`; census identical to before;
   - on the **DB-11e** code booted against it: log in with the saved credentials → 200, `/me.demoExpiresAt` = T, `demoCanExtend: false`; `POST /api/demo/extend` → 400
     (`AlreadyExtended`); the same unauthenticated int-route curl → `404`; `curl -s http://localhost:8095/swagger/v1/swagger.json | jq '[.paths | keys[] | select(test("tenants/\\{id\\}/(extend|demo-config)"))] | length'` → `0` and
     `jq '.paths | has("/api/admin/tenants/{workspaceId}/extend") and has("/api/admin/tenants/{workspaceId}/demo-config")'` → `true`;
   - rollback drill (§8 path A): `dotnet ef migrations script <ts>_DropUsersLegacyDemoColumns 20260923155947_BackfillWorkspacesDemoState -p Infrastructure -s API -o /tmp/db11e-down.sql`,
     apply with `docker exec -i pointer-db11e-rehearsal psql -U pointer -d pointer_rehearsal -v ON_ERROR_STOP=1 -1 < /tmp/db11e-down.sql` → the four columns and index are back
     (`DemoExtended` all `false`, others `NULL`), `SELECT count(*) FROM "__EFMigrationsHistory"` → 79, `SELECT demo_expires_at FROM workspaces WHERE demo_expires_at IS NOT NULL` unchanged; then
     `database update` again → dropped again, 80.
   - no `42703` / `column … does not exist` in either API's console output.
8. `scripts/local-e2e-gate.sh <DB-11e worktree>` → **PASS** (founder rule: full e2e on the isolated stack before any deploy).

Rehearsal commands (throwaway container; never the shared dev DB, never production):
```bash
# 1. same-day dump: on the VM, if the newest ~/backups/pointer-*.dump is not from today, take one (read-only for prod):
#      bash ~/pointer-api/scripts/backup-db.sh rehearsal-db11e
#    then fetch it (ssh details: DEPLOY.md / memory "Prod VM deploy access") to /tmp/prod.dump
# 2. throwaway Postgres 15
docker run -d --rm --name pointer-db11e-rehearsal -e POSTGRES_USER=pointer -e POSTGRES_PASSWORD=pointer \
  -e POSTGRES_DB=pointer_rehearsal -p 5439:5432 postgres:15
until docker exec pointer-db11e-rehearsal pg_isready -U pointer >/dev/null; do sleep 1; done
docker exec -i pointer-db11e-rehearsal pg_restore -U pointer -d pointer_rehearsal --no-owner --no-privileges < /tmp/prod.dump
export ConnectionStrings__Default="Host=localhost;Port=5439;Database=pointer_rehearsal;Username=pointer;Password=pointer"
# 3. queries:  docker exec -i pointer-db11e-rehearsal psql -U pointer -d pointer_rehearsal -c "<§9 P1..P4>"
# 4. rehearsal API (both code versions, from their own worktrees; same env as DB-17 §13's rehearsal API):
#      set -a; . ./.env; set +a; ASPNETCORE_URLS=http://localhost:8095 DBMigrationEnabled=false dotnet run --project API
#    (DBMigrationEnabled=false: the schema moves only via `dotnet ef database update -p Infrastructure -s API`)
# 5. throw away:  docker stop pointer-db11e-rehearsal
```
If the rehearsal API does not boot with that env, report it — do not change application config to make it boot.

## 8. Rollback

- **Irreversible part (bold, R5): the values in `users.expires_at`, `"DemoExtended"`, `"DemoCommentCapOverride"`, `"DemoTtlHoursOverride"` are destroyed.**
  `Down()` recreates the columns and the index **empty** (`DemoExtended = false`, others `NULL`). The only copy of the old values is the `pre-db11e` dump
  that the contract deploy takes immediately before the migration (R7). This is acceptable because P1/P2 prove they are copies of `workspaces.demo_*`.
- **Migration fails while applying:** Postgres DDL is transactional and EF runs the migration in one transaction → nothing applied; the API exits
  (DB-09). `git checkout <commit before the DB-11e merge>` on the VM and `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api`
  (nothing pending → ordinary boot).
- **Migration applied, new code misbehaves** — the previous (DB-17) code maps the four columns, so it cannot run against the contracted schema
  (every `users` query would fail with 42703). Two paths, in order of preference:
  - **A (lossless for live data, rehearsed in §7.7):** on a workstation generate `/tmp/db11e-down.sql` (command in §7.7), read it (4 `ADD COLUMN`, 1 `CREATE INDEX`,
    one `DELETE FROM "__EFMigrationsHistory"` for the DB-11e id), copy it to the VM; `docker compose -f docker-compose.prod.yml stop api`;
    `bash scripts/backup-db.sh pre-db11e-rollback`; `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -v ON_ERROR_STOP=1 -1 < db11e-down.sql`;
    `git checkout <commit before the DB-11e merge>`; `up -d --build api`. The DB-17 code reads only `workspaces.demo_*`, so demos keep their TTL; it resumes
    dual-writing new demos into the empty columns.
  - **B (if A fails):** restore `pre-db11e` (`DEPLOY.md` § Restore, API stopped, `pre-restore` dump first), `git checkout <commit before the DB-11e merge>`, `up -d --build api`.
    Loses every write since the deploy.
- **Never roll back past DB-17 by `Down()`** after this doc is live: `20260923155947_BackfillWorkspacesDemoState.Down()` nulls `workspaces.demo_*` on the
  assumption that the `users` columns still hold the source — they would be empty, and every live demo would lose its TTL under the pre-DB-17 code. A
  rollback to before DB-17 is a restore of `pre-db17`, nothing else.

## 9. Release steps

0. Owner approval line (header) present verbatim in the PR description; marker in the migration matches it.
1. **Prod pre-checks (read-only; paste output into the PR).** Run on the VM as
   `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "<query>"`:
   - **P1 — no live row relies on the old columns (blocking, must print 0 rows):** every live identity whose legacy `expires_at` is set has its own workspace as a live demo.
     ```sql
     SELECT u.id, u.public_id, u.owner_id, u.is_demo, u.expires_at, w.demo_expires_at, w.demo_converted_at, w.deleted_at AS ws_deleted_at
     FROM users u LEFT JOIN workspaces w ON w.id = u.owner_id
     WHERE u.deleted_at IS NULL AND u.expires_at IS NOT NULL
       AND (w.id IS NULL OR w.deleted_at IS NOT NULL OR w.demo_expires_at IS NULL);
     ```
     Any row = a demo whose intended TTL exists only on `users` → **stop, report to the owner, ship nothing** (do not hand-fix in this doc).
   - **P2 — drift between the copies on live, unconverted demos (expected 0 rows; informational — the workspace is authoritative):**
     ```sql
     SELECT u.id, w.id AS workspace_id, u.expires_at, w.demo_expires_at, u."DemoExtended", w.demo_extended_at,
            u."DemoCommentCapOverride", w.demo_comment_cap_override, u."DemoTtlHoursOverride", w.demo_ttl_hours_override
     FROM users u JOIN workspaces w ON w.id = u.owner_id
     WHERE u.deleted_at IS NULL AND u.is_demo AND w.deleted_at IS NULL AND w.demo_expires_at IS NOT NULL AND w.demo_converted_at IS NULL
       AND (u.expires_at IS DISTINCT FROM w.demo_expires_at OR u."DemoExtended" <> (w.demo_extended_at IS NOT NULL)
            OR u."DemoCommentCapOverride" IS DISTINCT FROM w.demo_comment_cap_override OR u."DemoTtlHoursOverride" IS DISTINCT FROM w.demo_ttl_hours_override);
     ```
     A row here means the users copy is stale; it does not block (nothing reads it), but paste it and say why (e.g. the current admin changed).
   - **P3 — census of what is destroyed (informational):**
     ```sql
     SELECT count(*) FILTER (WHERE expires_at IS NOT NULL) AS expires_at_set, count(*) FILTER (WHERE "DemoExtended") AS extended,
            count(*) FILTER (WHERE "DemoCommentCapOverride" IS NOT NULL) AS cap_set, count(*) FILTER (WHERE "DemoTtlHoursOverride" IS NOT NULL) AS ttl_set,
            count(*) FILTER (WHERE deleted_at IS NOT NULL AND expires_at IS NOT NULL) AS dead_rows_with_ttl
     FROM users;
     ```
   - **P4 — the schema is what the migration expects (blocking):**
     ```sql
     SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns
     WHERE table_schema = 'public' AND table_name = 'users'
       AND column_name IN ('expires_at','DemoExtended','DemoCommentCapOverride','DemoTtlHoursOverride') ORDER BY 1;          -- 4 rows
     SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'users' AND indexname = 'IX_users_expires_at';  -- 1 row
     SELECT DISTINCT v.relname FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid JOIN pg_class v ON v.oid = r.ev_class
     JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
     WHERE d.refobjid = 'users'::regclass AND a.attname IN ('expires_at','DemoExtended','DemoCommentCapOverride','DemoTtlHoursOverride');  -- 0 rows
     SELECT count(*), max("MigrationId") FROM "__EFMigrationsHistory";   -- 79 | 20260923155947_BackfillWorkspacesDemoState
     ```
   - **P5 — the deployed dashboard is the DB-17 build (blocking):** on the VM
     `git -C ~/pointer-dashboard merge-base --is-ancestor 98056df HEAD && echo dashboard-ok` → `dashboard-ok`, and
     `grep -l "/api/demo/extend" ~/pointer-api/dashboard/react/assets/*.js | wc -l` → ≥ 1 (the DB-17 dashboard is the first build that calls it; its source imports no int-route hook, §2).
2. R11 rehearsal (§7.7) and local e2e gate (§7.8) — both pasted into the PR.
3. Merge; CI green (DB-10 migration job, `MigrationSafetyTests`).
4. **Contract deploy, alone:** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11e bash scripts/deploy-api.sh` (the usual VM invocation, `DEPLOY.md` § Updating).
   The pre-flight must list exactly `…_DropUsersLegacyDemoColumns`; the script stops `api`, writes `~/backups/pointer-<ts>-pre-db11e.dump`, boots.
5. **Verify:** `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|error|42703|does not exist"` → one `Applying migration '…_DropUsersLegacyDemoColumns'`, no errors;
   P4's first two queries → 0 rows each and the history query → `80 | …_DropUsersLegacyDemoColumns`; `curl -s -o /dev/null -w '%{http_code}' -X POST https://api.pointer.moamen.work/api/admin/tenants/1/extend` → `404`;
   smoke on `demo.pointer.moamen.work`: start a demo → banner countdown, "Extend once" → 200, then hidden; on `app.pointer.moamen.work` as super admin, Tenants page lists the demo with its expiry,
   "Extend" disabled after use, demo-config dialog saves.
6. **Watch (first hour):** `DemoCleanupService: … failed`, Npgsql `42703`, 404 bursts on `/api/admin/tenants/*/extend` (a stale dashboard tab — reload it).
7. **Client/dashboard:** the `dashboard-agent` regenerates the client **from production** once (publish workflow). Expected diff: `postApiAdminTenantsIdExtend`,
   `usePostApiAdminTenantsIdExtend`, `patchApiAdminTenantsIdDemoConfig`, `usePatchApiAdminTenantsIdDemoConfig` (and their param/key helpers) disappear; `TenantResponse`
   unchanged. Dashboard: bump `@moamen-ui/pointer-react`, run its typecheck + build — **no source change expected** (0 imports, §2); if the build breaks, a hidden import exists → report.
8. Stamp docs: this header (`Deployed <date/time> UTC, <commit>, 80 migrations`), `DB-REVIEW-2026-09-22.md` §7 DB-11e row status, `SCHEMA.md` (task 11).

## 10. Out of scope

`users.owner_id`, `role_id`, `approval_status`, `ux_users_email_owner_live`, `TenantResponse.Id`/`PublicId` legacy fields (all **DB-11f**, not written);
`users.is_demo`, `users.recipient_email`; the six `workspaces.demo_*` columns and their index; any change to demo behaviour, TTL, extension rules,
`DemoCleanupService`, `HardDeleteAsync`, `AuthService`'s expiry guard; every historical migration file (R10); every other entity's `ExpiresAt`
(invites, quick-access links, device logins, impersonation sessions); DTO shapes (`TenantResponse`, `MeResponse`, `DemoSessionResponse`, `DemoStatusResponse`);
audit action strings; `clients/` (generated); the `pointer-dashboard` repo (no code change — only the package bump by the dashboard-agent); widget, CLI, landing.

## 11. Dashboard / widget / CLI tasks

**Dashboard:** none in source. After the production client regen: bump the package, typecheck + build, deploy with the next dashboard deploy (no urgency —
the deployed build already works against the new API). **Widget:** none. **CLI:** none.
