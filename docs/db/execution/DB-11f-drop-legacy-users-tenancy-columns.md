# DB-11f — Contract: take the DB-11a legacy tenancy columns off `users` (`owner_id`, `approval_status`, `ux_users_email_owner_live`; `role_id` becomes super-admin-only)

Reserved by DB-11a (`DB-11a…md:12,219,734`), DB-11c/d, and renumbered from "DB-11e" by `DB-11e-drop-legacy-demo-state.md:12-17`. This is the
**R2 step 3 (contract)** of the move DB-11a started (`users` → `workspace_memberships`). DB-11a declared the old columns "never read" (`User.cs:12-16`).
That was not true: the inventory in §2 found **~40 live read sites**. So DB-11f ships in **two releases**:

- **Part A (expand/migrate, code only, ordinary deploy).** Every read switches to memberships. The legacy columns keep being written, so rolling back the code is free.
- **Part B (contract, `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11f`).** Two migrations drop the columns and stop the writes.

Rules: **R2** (step 3, marker), **R3** (Part B migration 2 is a guarded, idempotent data change of its own), **R5** (Part B destroys values; labelled dump),
R6, **R7** (Part B only, `[ContractMigration("DB-11f")]` on both migrations), **R7.1** (**not batched**, and Part A and Part B never ship in the same run),
**R8.7** (membership invariants; amended by task A13), **R10** (no migration id, table or surviving column renamed; historical migrations never edited),
**R11** (same-day prod dump, throwaway Postgres 15), **R13** (read the generated migrations and the snapshot diff), R14 (hard-deleting an identity only
inside a workspace hard delete), R15, R17 (no new endpoint and no new audited action; one new failure reason on an existing audit action, §3.6).
**Class: Part A = Code only. Part B = Contract — Destructive (2 columns, 2 indexes, 1 FK dropped; `role_id` values nulled for members).**

**Owner approval (Part B only) — given 2026-09-24 by Moamen (owner), who selected "Approve Part B" whose text was, verbatim:**
`Approved to drop users.owner_id, users.approval_status, ux_users_email_owner_live, IX_users_owner_id, fk_users_workspaces_owner_id and make users.role_id super-admin-only (DB-11f Part B), 2026-09-24.`
Paste it verbatim into the Part B PR and the two markers. Part B still ships only after Part A has been live ≥ 24 h, P1–P5 pass on production, and the R11 rehearsal passes.

**Owner decisions answered 2026-09-24:** D11f.1 = two releases; D11f.2 = keep `role_id`, super-admin-only; D11f.3 = home = earliest membership. D11f.4–D11f.7: the recommended defaults below apply.
→ owner: ________ date: ________

**Status: written 2026-09-24, not implemented.** Verified against `pointer-api` @ `8f97871` (81 migrations, newest
`20260923220301_AddWorkspacesPauseAndDeletionState`) and `pointer-dashboard` @ `c463777` (`react/package.json:13` `"@moamen-ui/pointer-react": "^1.0.50"`).

## 0. Owner decisions (defaults apply unless the owner answers before Part A is merged)

| # | Question | Recommended default (encoded in this doc) | Alternative |
|---|---|---|---|
| **D11f.1** | One release or two? | **Two** (Part A code-only, then Part B contract, at least 24 h apart; §9). The old columns still have ~40 readers, so a single-release drop would make a code rollback lose data. With two releases, rolling back Part B is lossless in behaviour because Part A code reads none of the columns (§8). | One release (drop + code switch together). Not recommended. |
| **D11f.2** | What becomes of `users.role_id`? It is **not** legacy: it is how every super-admin check works today (`identity.Role?.IsSuperAdmin`, ~30 sites, §2). | **Keep the column, make it nullable, and set it NULL for every non-super-admin** (Part B). It becomes the *platform role*: non-null only for super admins, pointing at the global super-admin role. No super-admin code path changes. This also removes a latent FK bug (§2 "Latent bug"). | (B) Replace it with `users.is_super_admin boolean` and drop `role_id` + `FK_users_roles_role_id` + `IX_users_role_id`. That means ~30 read sites, JWT/`/me`/profile sourcing the super role from `roles`, and ~200 test edits. It would be its own doc (DB-11g). |
| **D11f.3** | What is the "home workspace"? It drives the `isHome` badge (dashboard `Shell.tsx:316`, `LoginPage.tsx:146`; widget `templates.ts:71`) and the workspace named in the password-reset / password-changed / e-mail-change mails. | **The workspace of the identity's earliest membership row of any state** (`ORDER BY joined_at, id`). Pre-check **P3** proves this equals `users.owner_id` for every production row. No client change. | (b) A new `workspace_memberships.is_home` column plus backfill: exact, but more moving parts and re-home logic. (c) Drop `IsHome`: a client contract change. |
| **D11f.4** | What happens to an identity with **no membership row anywhere** (not a super admin, not a merged row) whose legacy `owner_id` points at the workspace being deleted? | This shape is unreachable (invariant **I1**, §3.1; **P1** proves 0 rows). Part A: `HardDeleteAsync` **throws** and deletes nothing, loudly, instead of guessing. Part B: the identity no longer references any workspace, so it simply survives. | Delete it on sight. This cannot be expressed without `owner_id`. |
| **D11f.5** | Drop the super-admin `Pending`/`Rejected` login branches that read `users.approval_status`? | **Yes.** `AdminSeeder` forces the super admin to `Approved` on every boot (`AdminSeeder.cs:150,160`), and **P4** proves it. | Keep them: they would need a new column. |
| **D11f.6** | Refuse API-key login with a **null-owner key held by a non-super-admin** (a pre-DB-11a leftover)? Today it signs in with no workspace and the *legacy* identity role. | **Yes, refuse it** (`InvalidApiKey`, audit reason `invalid_credentials`). **P5** must print 0 rows. | Keep today's behaviour. |
| **D11f.7** | Profile page `roleName` (`GET /api/me/profile`, `GET /api/admin/users/{id}/profile`) today shows `users.role_id`, the role at **creation**, even after a role change or in another workspace. | **Show the current role**: the caller-workspace membership's role (no workspace in hand → the home membership's role; super admin → platform role). This is a bug fix. | Keep the stale creation-time role. |

## 1. Goal

DB-11a made `workspace_memberships` the only authority for "who is in which workspace, with which role, approved or not". It left three per-workspace
columns on the identity table: `users.owner_id` (the creation workspace, with a Restrict FK to `workspaces`), `users.role_id` (the creation role, with a
Restrict FK to `roles`, which may be **tenant-owned**), and `users.approval_status`. It also left their index `ux_users_email_owner_live`. These columns still
decide real behaviour: which identities a workspace deletion destroys (and its DB-18 preview count), the `isHome` badge, which workspace the security
mails name, the profile role name, `/me` and JWT role fallbacks, and the null-tenant filter branch. Their FKs are also the source of the FK bug class DB-18
had to patch (`TenantService.cs:774-784`).

This doc moves every read to memberships (Part A), then drops the columns and stops writing them (Part B).

User-visible effect: none by design. Two deliberate corrections come with it: D11f.7 (profile shows the current role) and D11f.6 (a stray non-super
null-owner key stops working). Workspace deletion deletes exactly the same accounts, which P2 proves on production data. That matches the owner's D18.10
wording, "accounts that belong only to this workspace" (`DB-18…md:443`).

## 2. Prerequisites (verified facts, 2026-09-24 @ `8f97871`)

**How the inventory was made (repeatable, used again as acceptance in §7).** A scratch copy from `git archive HEAD` had
`[System.Obsolete("DB11F_<X>")]` added to `User.OwnerId`, `User.RoleId`, `User.Role`, `User.ApprovalStatus` and `Role.Users`, then was built with
`dotnet build Pointer.sln --no-incremental`. Every CS0618 warning is one use of a legacy member **on `User`**. This distinguishes them from
`WorkspaceMembership.OwnerId/RoleId/ApprovalStatus`, `Invite.RoleId`, etc. Result: 541 sites. 70 are outside `Tests/` (below). 471 are in 50 test files.
The script is in §7 criterion 2.

**Schema being changed** (snapshot `Infrastructure/Migrations/AppDbContextModelSnapshot.cs`, `User` entity block `:2119-2266`, relationships `:3245-3266`)
- `approval_status integer NOT NULL DEFAULT 1` — snapshot `:2133-2137`; `User.cs:28-32`; `UserMapping.cs:41-43`; created by `20260624144403_AddUserApprovalStatus.cs:13-18`.
- `owner_id uuid NULL` — snapshot `:2192-2194`; `User.cs:51`; `UserMapping.cs:50`. FK `fk_users_workspaces_owner_id` → `workspaces(id)` Restrict (`UserMapping.cs:51-55`, snapshot `:3253-3257`,
  created by `20260922105922_AddWorkspaceForeignKeys.cs:233`). Index `IX_users_owner_id` (`UserMapping.cs:56`, snapshot `:2253`, created by `20260629130828_AddTenancy.cs:97`).
- `ux_users_email_owner_live UNIQUE (email, owner_id) WHERE deleted_at IS NULL NULLS NOT DISTINCT` (`UserMapping.cs:29-33`, snapshot `:2260-2264`, created by
  `20260922082557_SoftDeleteAwareUniqueIndexes.cs:47-54`). It is **redundant**: `ux_users_email_live UNIQUE (lower(email)) WHERE deleted_at IS NULL` (raw SQL, DB-11a Migration 3,
  comment `UserMapping.cs:25-27`) is strictly stronger. No code names it: `grep -rn ux_users_email_owner_live Application API Infrastructure Tests e2e scripts .github` → only `UserMapping.cs:33`.
- `role_id integer NOT NULL` — snapshot `:2216-2218`; `User.cs:12-20` (`RoleId` + `Role` navigation); `UserMapping.cs:39` (`.IsRequired()`), `:69-72`
  (`HasOne(x => x.Role).WithMany(r => r.Users).HasForeignKey(x => x.RoleId).OnDelete(Restrict)`); FK `FK_users_roles_role_id` (`20260623133436_InitialCreate.cs:114`),
  index `IX_users_role_id` (`InitialCreate.cs:183`, snapshot `:2258`). **Both stay** (D11f.2).
- Kept on `users` unchanged: every other column, `merged_into_user_id` + `fk_users_merged_into_user`, `ux_users_email_live`, `IX_users_public_id`.

**Every non-test use of the legacy members** (file:line → what it is → Part that changes it)

| Site | Member | Kind | Replacement | Part |
|---|---|---|---|---|
| `Application/Services/Implementation/TenantService.cs:680-685` | OwnerId | **read** `usersCreatedHere = Users.Where(u => u.OwnerId == ws)` | §3.2 | A |
| `TenantService.cs:716` | OwnerId | write (re-home) | §3.2 maintenance, deleted in B | A→B |
| `TenantService.cs:767-791` `IdentitiesDeletedWithWorkspace` | OwnerId | **read** (delete set + DB-18 preview, `WorkspaceLifecycleService.cs:666-671`) | §3.2 | A |
| `Infrastructure/AppDbContext.cs:108` | OwnerId | **read** (User filter null-tenant branch; dead in prod: `docker-compose.prod.yml:32` `Tenancy__StrictNullTenantIsolation: "true"`) | `!e.Memberships.Any()` | A |
| `AuthService.cs:243` (reset mail), `:386` (password-changed mail), `:456` (e-mail-change mail) | OwnerId | **read** (workspace named in mail) | `HomeWorkspaceIdAsync` (D11f.3) | A |
| `AuthService.cs:880` (login picker), `:1130` (`BuildMeAsync` → `/me.workspaces`) | OwnerId | **read** (`WorkspaceChoice.IsHome`, `:1160`) | `HomeWorkspaceIdAsync` | A |
| `AuthService.cs:752,761` (password login, super-admin branch), `:1230,1239` (null-owner API key branch) | ApprovalStatus | **read** | branches deleted (D11f.5) | A |
| `AuthService.cs:1108` `membership?.Role ?? identity.Role` | Role | **read** — falls back to the *legacy* role for a tenant token whose membership lookup returned null (ended membership, `/me` within the 60 s cache window) | `UserMapper.SessionRole` | A |
| `AuthService.cs:1192` `Role? role = user.Role` (API key) | Role | **read** — kept for the null-owner branch, which becomes super-admin-only | `SessionRole(user, null)` + D11f.6 guard | A |
| `Application/Services/Implementation/PreferencesService.cs:58,63` | Role | **read** — same fallback shape as `:1108` | `SessionRole` | A |
| `Application/Common/UserMapper.cs:38` `RoleId = role?.Id ?? user.RoleId` | RoleId | **read** | `role?.Id ?? 0` | A |
| `Application/Services/Implementation/ProfileService.cs:249` `RoleName = user.Role?.Name` | Role | **read, for every user** | D11f.7 | A |
| `Infrastructure/Auth/JwtTokenService.cs:80-81` `membership?.Role ?? u.Role`, `membership?.RoleId ?? u.RoleId` | Role, RoleId | **read** fallback | `SessionRole` | A |
| `JwtTokenService.cs:166,172` (`IssueImpersonation`, super admin only) | Role, RoleId | super-admin read | unchanged; `:172` gets `?? 0` in B | B |
| Super-admin checks `identity.Role?.IsSuperAdmin`: `AuthService.cs:223,425,545,749,911,956,1042,1124,1261`; `EmailVerificationService.cs:74,144`; `IdentityEraseService.cs:60,83,99,173`; `MfaService.cs:89,131,211` | Role | super-admin read (platform role) | **unchanged** (D11f.2) | — |
| `.Include(u => u.Role)` / `.ThenInclude(u => u.Role)`: `ApiKeyService.cs:78`, `AuthService.cs:214`, `DemoService.cs:370`, `ImpersonationService.cs:64`, `MembershipService.cs:27,36`, `MfaService.cs:69`, `PreferencesService.cs:39`, `ProfileService.cs:32,45` | Role | loads the platform role | unchanged | — |
| `MembershipService.cs:131-136` (`NewIdentity`) | RoleId, OwnerId, ApprovalStatus | write | removed in B | B |
| `DemoService.cs:151-156` (`ProvisionAsync`), `:292` `demoUser.Role = role;` | RoleId, OwnerId, ApprovalStatus, Role | write | removed in B (`:292` **must** go, §3.5) | B |
| `API/Seed/AdminSeeder.cs:148,150,158,160` | RoleId, ApprovalStatus | write (super admin) | `:150,:160` removed in B; `:148,:158` stay | B |
| `Infrastructure/Mappings/UserMapping.cs:29,39,41,50,53,56,69-71` | all | mapping | B | B |

No other consumer exists. There are no mapper libraries (`grep -l "AutoMapper\|Mapster" */*.csproj` → none) and no `User` entity serialization. There is
no string-based EF access (`EF.Property`, `Include("Role")` → 0). The only raw SQL on these columns outside migrations is `.github/workflows/db-migrations.yml:63-64`
(the DB-11a duplicate-e-mail probe inserts `role_id, … approval_status`; DB-11e's review found the same trap, `DB-11e…md:393`).
`e2e/`, `scripts/`, `cli/src`, `web-component/src`, `extension/`, `API/wwwroot/*.md`, and `docs/ON-DISK-CONTRACT.md` have no hits for the three columns
(`e2e/run-e2e.sh:246` is a shell variable named `owner_id`, unrelated).

**Invariant facts the replacement relies on**
- Memberships are removed **only** by a workspace hard delete: `TenantService.cs:673`. Nothing else calls `Remove`/`RemoveRange` on `WorkspaceMembership`.
  Ending a membership keeps the row (`MembershipService.cs:206-212` `EndAsync` sets `LeftAt`), and erase ends memberships through `EndAsync`
  (`IdentityEraseService.cs:242-243`).
- Every identity is created together with its first membership: `NewIdentity` (`MembershipService.cs:117-138`, 7 callers: `InviteService.cs:760,911,1127`,
  `UserService.cs:168`, `TenantService.cs:227`, `AuthService.cs:1356,1488`) is always followed by `JoinAsync`. `DemoService.ProvisionAsync` creates the
  membership itself. The DB-11a backfill created memberships for **every** workspace-scoped `users` row, live or soft-deleted (`DB-11a…md:308-332`).
- Merged rows (`merged_into_user_id`) come only from the one-time DB-11a Migration 2. No runtime writer exists (`grep -rn MergedIntoUserId Application API Infrastructure` → readers
  `AuthService.cs:723`, `IdentityEraseService.cs:349` only). The production census found **0 merges, 0 aliases** (`DB-REVIEW-2026-09-22.md:137`).
- The super admin has `owner_id` NULL and no membership (`DB-11a…md:561`; seeder `AdminSeeder.cs:132-165` writes no `OwnerId`).
- Membership `Role` is loaded at every `ITokenService.Issue` call site with a membership: `AuthService.cs:927` (from `ListForIdentityAsync`, `.Include(m => m.Role)`
  `MembershipService.cs:64`), `:1069`, `:1268`, `:1687` (`GetMembershipAsync`, `:44`), `DemoService.cs:293`, `:505`, `InviteService.cs:805`, `:1019`.
  `AuthService.cs:1001` passes `null` for the super-admin MFA path.
- `IUnitOfWork.UserAliases` exists (`Application/Abstractions/IUnitOfWork.cs:13`). `UserAlias.SourceWorkspaceId` holds the merged row's workspace (`DB-11a…md:303-304`).
- The only `IMembershipService` implementations are `MembershipService.cs:13` and the test wrapper `Tests/Db17DemoServiceTests.cs:836`.

**Latent bug found by this inventory (fixed in Part A).** An invite may pin a **tenant-owned** role (`RoleService.cs:55` stamps `OwnerId = TenantStamp.OwnerFor(...)`),
and `NewIdentity` copies it into `users.role_id` (`InviteService.cs:760-766`). If that identity also belongs to X and its creation workspace W is hard-deleted,
the re-home at `TenantService.cs:716` moves `owner_id` but leaves `role_id` pointing at W's role. `DeleteOwnedAsync<Role>(x => x.OwnerId == workspaceId)`
(`:721`) then violates `FK_users_roles_role_id` (Restrict) with 23503, and the whole deletion fails. This is the same class as the DB-18 fix. Pre-check **P6** counts rows exposed to it.

**Tests that pin today's behaviour and are touched** (all found by the scan; exact edits in §6)
`Tests/WorkspaceTests.cs:168` (Sqlite `TestDb`, real FKs), `:381` (`Assert.Equal(23, HardDeleteOrder.Length)`), `:505-522` (reflects `GetProperty("OwnerId")!` on every
`HardDeleteOrder` type, which would null-ref once `User` has no `OwnerId`), `:530-618` (5b, DB-18 Gemini BLOCKER, seeds an erased identity **with no membership**);
`Tests/Db18WorkspaceLifecycleTests.cs:2050-2123` (preview = delete parity); `Tests/WorkspaceMembershipTests.cs:527-620,786-794` (multi-workspace survivor re-homed);
`Tests/WorkspaceSwitchTests.cs:260-296,741-742` (`IsHome`); `Tests/TenantQueryFilterTests.cs:144-257` (null-tenant user bucket); `Tests/TokenServiceTests.cs:9-21` (non-super
identity without membership carries its identity role); `Tests/ChangePasswordTests.cs:296-361` (mail names the workspace; `SeedUser` `:178-221` already joins a membership).

**Dashboard / widget / served files**
- `WorkspaceChoice.IsHome` (`Application/DTOs/Auth/WorkspaceChoice.cs:20-21`) is read by `pointer-dashboard/react/src/features/shell/Shell.tsx:316`, `features/login/LoginPage.tsx:146`
  and `web-component/src/templates.ts:71` / `types.ts:201`. **Semantics kept** (D11f.3). No DTO shape changes anywhere in this doc.
- `TenantResponse.Id` / `PublicId` / `ApprovalStatus` (`Application/DTOs/Tenant/TenantResponse.cs:10-11,31`) are **sourced from the admin membership**, not from the legacy
  columns (`TenantService.cs:142-150`: `admin?.User.Id`, `admin?.User.PublicId`, `admin?.ApprovalStatus`). The deployed dashboard reads `publicId` and `approvalStatus`
  (`TenantsPage.tsx:352-353,492-504,579,625`). **Unaffected by DB-11f; keep them.** Removing `Id` is an API-contract cleanup, not a schema change (§10).
- `MeResponse.RoleId` (`MeResponse.cs:8`) changes value only when a session has no role (0 instead of a stale legacy id). The dashboard has no reader of `me.roleId`
  (`grep -rnE "me\??\.roleId|user\??\.roleId" react/src` → only `UsersPage`/`OverviewPage`, which read `UserResponse.roleId`, a membership field).
- `API/wwwroot` served docs and `docs/ON-DISK-CONTRACT.md`: no reference to any of these identifiers → **untouched**.

**Tooling / conventions** (as DB-11e §2)
- `just migrate name="…"` = `dotnet ef migrations add … -p Infrastructure -s API` (`justfile:6`); `just test` = `dotnet test` (`:8`); `just fmt` = `dotnet csharpier .` (`:5`).
- Marker/attribute precedent: `20260923205702_DropUsersLegacyDemoColumns.cs` (DB-11e). Data migration precedent (scaffolded empty + `Sql`): `20260923155947_BackfillWorkspacesDemoState.cs`.
  Guard: `Tests/MigrationSafetyTests.cs:75-83` (risky-op regex includes `DropColumn|DropIndex|DropForeignKey|AlterColumn|Sql`; marker regex accepts `R2 contract`).
- Gate: `scripts/deploy-api.sh:33-74` (refuses a pending `[ContractMigration]` unless `POINTER_APPLY_CONTRACT=1`; then stops `api`, dumps `POINTER_CONTRACT_LABEL` at `:71`).
  Restore: `DEPLOY.md:178-230`. Local e2e gate: `scripts/local-e2e-gate.sh [<worktree>]`.
- Engine `postgres:15`. `users` holds single-digit rows (DB-11e rehearsal census `8|3|6|125` users|workspaces|memberships|comments, `DB-11e…md:395`).
- Nullable reference types are enabled in every project. Warnings are **not** errors (no `TreatWarningsAsErrors` in any `*.csproj`).

## 3. Design

### 3.1 Definitions (the whole doc rests on these)

- **Belongs to W**: the identity has a `workspace_memberships` row with `owner_id = W`, of **any** state (live, ended, disabled, pending, rejected, soft-deleted).
- **Belongs only to W**: it belongs to W and has **no** membership row of any state with `owner_id <> W`.
- **Home workspace** (D11f.3): `owner_id` of the identity's earliest membership row of any state, `ORDER BY joined_at, id`. NULL when there is none (super admins).
- **Platform role** (D11f.2): `users.role_id`, meaningful **only** when it points at a role with `is_super_admin = true`. After Part B it is NULL for everyone else.
- **Invariant I1** (proved on production by P1): every non-super-admin, non-merged `users` row with `owner_id = W` has at least one membership row in W. Consequence:
  "created in W and no membership elsewhere" (today's rule) ≡ "belongs only to W" (new rule) for every such row. P2 proves the two sets are equal per workspace.
- **Invariant I2** (proved by P3): `users.owner_id` = home workspace for every non-super-admin, non-merged row.

### 3.2 The one delete-set rule (Part A; replaces `TenantService.cs:758-791`, used by the delete and the DB-18 preview)

Replace the body **and** doc-comment of `IdentitiesDeletedWithWorkspace` with exactly this:

```csharp
/// <summary>
/// DB-11f. The exact "which accounts are deleted WITH this workspace" rule (owner decision D18.10:
/// accounts that belong only to this workspace) — membership-only, never users.owner_id:
/// an identity with a membership row of ANY state here and NO membership row of any state
/// (ended/soft-deleted included, IgnoreQueryFilters) in another workspace, never a super admin;
/// plus DB-11a merged tombstones whose canonical is deleted here or whose alias records this
/// workspace as their source (0 in production). Soft-deleted/erased identities are included (the
/// DB-18 Gemini BLOCKER). Shared by HardDeleteAsync (the delete set) and WorkspaceLifecycleService's
/// deletion preview (count only) — the two must never diverge
/// (Db18WorkspaceLifecycleTests.Preview_AccountsCount_EqualsRowsActuallyDeleted).
/// </summary>
public static IQueryable<User> IdentitiesDeletedWithWorkspace(IUnitOfWork uow, Guid workspaceId)
{
    var memberships = uow.Repository<WorkspaceMembership>().Query().IgnoreQueryFilters();
    var superAdminRoleIds = uow.Repository<Role>()
        .Query()
        .IgnoreQueryFilters()
        .Where(r => r.IsSuperAdmin)
        .Select(r => r.Id);
    var users = uow.Repository<User>().Query().IgnoreQueryFilters();

    var coreIds = users
        .Where(u =>
            memberships.Any(m => m.UserId == u.Id && m.OwnerId == workspaceId)
            && !memberships.Any(m => m.UserId == u.Id && m.OwnerId != workspaceId)
            && !superAdminRoleIds.Any(id => id == u.RoleId)
        )
        .Select(u => u.Id);

    return users.Where(u =>
        coreIds.Contains(u.Id)
        || (
            u.MergedIntoUserId != null
            && (
                coreIds.Contains(u.MergedIntoUserId.Value)
                || uow.UserAliases.Any(a =>
                    a.AliasPublicId == u.PublicId && a.SourceWorkspaceId == workspaceId
                )
            )
        )
    );
}
```
`id == u.RoleId` compiles for `int` (Part A) and `int?` (Part B) unchanged. `WorkspaceLifecycleService.cs:665-671` is **not** edited.

**`HardDeleteAsync` Part A** — replace `TenantService.cs:669-719` (from the comment `// DB-11a: end/remove every membership…` through the closing `}` of the
`foreach (var u in usersCreatedHere)` loop) with:

```csharp
// DB-11a: end/remove every membership of this workspace BEFORE touching `users`. Staged only — the
// queries below still see them (they run against the store), which the delete rule needs.
await DeleteOwnedAsync<WorkspaceMembership>(x => x.OwnerId == workspaceId);

// DB-11f: the delete set is the ONE shared, membership-based query (also the deletion preview).
var deleteIds = await IdentitiesDeletedWithWorkspace(_unitOfWork, workspaceId)
    .Select(u => u.Id)
    .ToListAsync();
if (deleteIds.Count > 0)
{
    var doomed = await _unitOfWork
        .Repository<User>()
        .Query()
        .IgnoreQueryFilters()
        .Where(u => deleteIds.Contains(u.Id))
        .ToListAsync();
    // api_keys, user_aliases, user_recovery_codes cascade.
    _unitOfWork.Repository<User>().RemoveRange(doomed);
}

// DB-11f PART A ONLY — legacy pointer maintenance (deleted by DB-11f Part B task B6 together with
// users.owner_id). Nothing READS these values for behaviour; this only stops the two legacy FKs
// (fk_users_workspaces_owner_id, FK_users_roles_role_id) from blocking the deletes below. A
// surviving identity that points here is re-pointed to its earliest membership elsewhere — both
// columns, which also fixes the tenant-role 23503 (DB-11f §2 "Latent bug").
var workspaceRoleIds = await _unitOfWork
    .Repository<Role>()
    .Query()
    .IgnoreQueryFilters()
    .Where(r => r.OwnerId == workspaceId)
    .Select(r => r.Id)
    .ToListAsync();
var legacyPointers = await _unitOfWork
    .Repository<User>()
    .Query()
    .IgnoreQueryFilters()
    .Where(u =>
        !deleteIds.Contains(u.Id)
        && (u.OwnerId == workspaceId || workspaceRoleIds.Contains(u.RoleId))
    )
    .ToListAsync();
foreach (var u in legacyPointers)
{
    var other = await _unitOfWork
        .Repository<WorkspaceMembership>()
        .Query()
        .IgnoreQueryFilters()
        .Where(m => m.UserId == u.Id && m.OwnerId != workspaceId)
        .OrderBy(m => m.JoinedAt)
        .ThenBy(m => m.Id)
        .Select(m => new { m.OwnerId, m.RoleId })
        .FirstOrDefaultAsync();
    if (other is null)
        // Invariant I1 (DB-11f §3.1, D11f.4): unreachable — P1 proved 0 such rows in production.
        throw new InvalidOperationException(
            $"DB-11f invariant I1 broken: user {u.Id} references workspace {workspaceId} but has no membership in any other workspace and is not in the delete set."
        );
    if (u.OwnerId == workspaceId)
        u.OwnerId = other.OwnerId;
    if (workspaceRoleIds.Contains(u.RoleId))
        u.RoleId = other.RoleId;
    _unitOfWork.Repository<User>().Update(u);
}
```
Everything after (`DeleteOwnedAsync<Role>`, `AppEnvironment`, the workspace row, `SaveChangesAsync`, audit, files) is unchanged. The throw happens inside
`ExecuteInTransactionAsync`, so nothing is deleted. The DB-18/DB-17 hosted loops log it as an error. They do not catch it: they catch only
`DeletionPreconditionChangedException`.

### 3.3 Every other read, replaced (Part A)

| Site | After |
|---|---|
| `IMembershipService` (+ `MembershipService`, + test wrapper `Tests/Db17DemoServiceTests.cs:836`) | new `Task<Guid?> HomeWorkspaceIdAsync(int userId);`, body in task A2 |
| `AuthService.cs:241-244`, `:384-387`, `:454-457` | second argument of `WorkspaceNameResolver.ResolveForEmailAsync` becomes `await _memberships.HomeWorkspaceIdAsync(user.Id)` (`user`, `user`, `identity` respectively) |
| `AuthService.cs:880`, `:1130` | second argument of `BuildWorkspaceChoicesAsync` becomes `await _memberships.HomeWorkspaceIdAsync(user.Id)` / `(identity.Id)` |
| `AuthService.cs:749-778` super-admin login branch | delete the two `ApprovalStatus` blocks `:752-768`; keep the `IsActive` block `:770-777`; comment `:751` → `// Super admins own no workspace — identity-level IsActive only (DB-11f D11f.5: they are always Approved; AdminSeeder reconciles it every boot).` |
| `AuthService.cs:1108` | `var role = UserMapper.SessionRole(identity, membership);` |
| `AuthService.cs:1192` | `Role? role = UserMapper.SessionRole(user, null);` |
| `AuthService.cs:1227-1256` null-owner key branch | delete the two `ApprovalStatus` blocks `:1230-1246`; insert as the first statement of the `else { … }` the D11f.6 guard (task A5). Keep the `IsActive` and MFA blocks |
| `UserMapper.cs:38` | `RoleId = role?.Id ?? 0,` + new static `SessionRole` (task A6) |
| `PreferencesService.cs:58` / `:63` | `Role? role = UserMapper.SessionRole(user, null);` / `role = UserMapper.SessionRole(user, membership);` |
| `ProfileService.cs:249` | D11f.7 role name (task A8) |
| `JwtTokenService.cs:78-81` | `var role = UserMapper.SessionRole(u, membership); var roleId = membership?.RoleId ?? role?.Id ?? 0;` (comment in task A9) |
| `AppDbContext.cs:108` | `\|\| (currentUser.TenantId == null && !strict && !e.Memberships.Any())` (comment in task A10) |

`UserMapper.SessionRole` is the single rule: **a session's role is its membership's role. Without a membership, it is the identity's own role only for a super admin.** A membership whose
`Role` is not loaded yields no role. It never falls back to the identity. All eight membership call sites load it (§2).

### 3.4 Part B — target shape and migrations

`users` after Part B: **no** `owner_id`, **no** `approval_status`, **no** `ux_users_email_owner_live`, `IX_users_owner_id`, `fk_users_workspaces_owner_id`.
`role_id integer NULL`, still FK `FK_users_roles_role_id` → `roles(id)` Restrict and indexed `IX_users_role_id`; non-null only for super admins. `User` loses `OwnerId`
and `ApprovalStatus`; `RoleId` becomes `int?`, `Role` becomes `Role?`. The relationship is `.IsRequired(false)`. `Role.Users` stays. No other table changes.

**Migration B-1 `DropUsersLegacyTenancyColumns`** (scaffolded). Expected `Up()`: exactly these six operations (EF may order the two `DropIndex` and the two `DropColumn` differently):
```csharp
migrationBuilder.DropForeignKey(name: "fk_users_workspaces_owner_id", table: "users");
migrationBuilder.DropIndex(name: "IX_users_owner_id", table: "users");
migrationBuilder.DropIndex(name: "ux_users_email_owner_live", table: "users");
migrationBuilder.DropColumn(name: "approval_status", table: "users");
migrationBuilder.DropColumn(name: "owner_id", table: "users");
migrationBuilder.AlterColumn<int>(name: "role_id", table: "users", type: "integer", nullable: true,
    oldClrType: typeof(int), oldType: "integer");
```
Expected `Down()`: the scaffolded inverse:
```csharp
migrationBuilder.AlterColumn<int>(name: "role_id", table: "users", type: "integer", nullable: false,
    oldClrType: typeof(int), oldType: "integer", oldNullable: true);
migrationBuilder.AddColumn<int>(name: "approval_status", table: "users", type: "integer", nullable: false, defaultValue: 1);
migrationBuilder.AddColumn<Guid>(name: "owner_id", table: "users", type: "uuid", nullable: true);
migrationBuilder.CreateIndex(name: "IX_users_owner_id", table: "users", column: "owner_id");
migrationBuilder.CreateIndex(name: "ux_users_email_owner_live", table: "users", columns: new[] { "email", "owner_id" },
    unique: true, filter: "deleted_at IS NULL").Annotation("Npgsql:NullsDistinct", false);
migrationBuilder.AddForeignKey(name: "fk_users_workspaces_owner_id", table: "users", column: "owner_id",
    principalTable: "workspaces", principalColumn: "id", onDelete: ReferentialAction.Restrict);
```
Two permitted scaffold differences, each to be noted in the PR:
- If the `Down()` `AlterColumn` carries `defaultValue: 0`, **delete that argument.** A default of 0 is not the pre-B shape, and B-2's `Down()` has already filled every NULL (§8).
- If `Up()`/`Down()` also contain a `DropForeignKey`+`AddForeignKey` pair for `FK_users_roles_role_id` with the same column, principal and `Restrict`, that is acceptable.

Any other operation, or any operation on another table → stop and report (R13).

Class header (copy `20260923205702_DropUsersLegacyDemoColumns.cs`'s shape): `[ContractMigration("DB-11f")]` on the class. On the line directly above `/// <inheritdoc />` of `Up()`:
`// DB-RULES: R2 contract approved <yyyy-mm-dd> by Moamen (owner, verbatim: "<the header approval line>"; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)`

**Migration B-2 `ClearUsersRoleIdForMembers`** (scaffold after B-1 with no model change → empty `Up`/`Down`, snapshot unchanged). Hand-written body, nothing else:
```csharp
[ContractMigration("DB-11f")]
public partial class ClearUsersRoleIdForMembers : Migration
{
    // DB-RULES: R2 contract approved <yyyy-mm-dd> by Moamen (owner, verbatim: "<the header approval line>"; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // DB-11f §3.4: users.role_id is the PLATFORM role — kept only for super admins. Idempotent.
        migrationBuilder.Sql(
            "UPDATE users SET role_id = NULL WHERE role_id IS NOT NULL "
                + "AND role_id NOT IN (SELECT id FROM roles WHERE is_super_admin);"
        );
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Not the original values (they were legacy copies of the creation role; only the
        // pre-db11f dump holds them). Refills every NULL so B-1's Down() can restore NOT NULL: the
        // earliest membership's role, else the lowest-id global non-super role (P9 proves one exists).
        migrationBuilder.Sql(
            "UPDATE users u SET role_id = COALESCE("
                + "(SELECT m.role_id FROM workspace_memberships m WHERE m.user_id = u.id ORDER BY m.joined_at, m.id LIMIT 1), "
                + "(SELECT r.id FROM roles r WHERE r.owner_id IS NULL AND NOT r.is_super_admin ORDER BY r.id LIMIT 1)) "
                + "WHERE u.role_id IS NULL;"
        );
    }
}
```
Keep the scaffolded `using`/namespace lines and the `.Designer.cs`. R3 batching: not applicable (single-digit rows, one guarded `UPDATE`).

Locking: every B-1 operation is catalog-only (`DROP COLUMN`, `DROP INDEX`, `DROP CONSTRAINT`, `DROP NOT NULL`). B-2 updates at most a handful of rows. The API is stopped anyway (R7). R4 concurrent mode: not applicable.
Historical migrations that reference these columns (`InitialCreate`, `AddUserApprovalStatus`, `AddTenancy`, `SoftDeleteAwareUniqueIndexes`, `AddWorkspaceForeignKeys`,
`MergeSameEmailIdentitiesAndBackfillMemberships`, `BackfillWorkspacesDemoState` — its SQL keys on `u.owner_id`) are **not edited** (R10). Applied from empty, the columns still exist when they run (DB-10 CI).

### 3.5 Part B code — stop writing

| Site | Change |
|---|---|
| `MembershipService.NewIdentity :131-136` | delete the comment `:131` and the lines `RoleId = firstRole.Id,`, `OwnerId = firstWorkspaceId,`, `ApprovalStatus = ApprovalStatus.Approved,`. **Signature unchanged** (7 callers + wrapper); add the XML remark in task B3 |
| `DemoService.ProvisionAsync :151-156` | delete `RoleId = role.Id,`, the comment `:152-154`, `OwnerId = workspaceId,`, `ApprovalStatus = ApprovalStatus.Approved,` |
| `DemoService.cs:292` `demoUser.Role = role;` | **delete**. With `RoleId` nullable, setting the navigation on a tracked entity makes EF fix up `RoleId = role.Id`. The `AuditWriter`'s later `SaveChangesAsync` would then persist a member platform role. `:293` (`demoMembership.Role = role;`) stays |
| `AdminSeeder.cs:150`, `:160` | delete the two `ApprovalStatus` lines. `:148`, `:158` (`RoleId = adminRoleId`) stay: that is the platform role |
| `TenantService.cs` | delete the Part A maintenance block (§3.2, from `// DB-11f PART A ONLY` through its `foreach`). `HardDeleteOrder` loses `typeof(User)` (22 entries; §5 B6) |
| `JwtTokenService.cs:172` | `new Claim("role_id", (operatorUser.RoleId ?? 0).ToString()),` |
| `.github/workflows/db-migrations.yml:63-64` | in **both** `INSERT INTO users (…)`: remove `approval_status, ` from the column list and the matching `1, ` from `VALUES` (the value after `true, `). `role_id`/`$rid` stay |

### 3.6 Audit

No new action and no new endpoint. The D11f.6 refusal writes `apikey.login_failed` via the existing `AuditApiKeyLoginFailedAsync(apiKey, "invalid_credentials")`, a reason
string already used at `AuthService.cs:1170,1179,1187`. `Tests/AuditCoverageTests.cs` is unaffected. The `pending`/`rejected` reasons remain in use by the membership branches.

### 3.7 What happens to every existing row

- **Part A:** no row is written by the deploy. On a workspace hard delete, the delete set is identical to today's (P2), and surviving identities keep being re-pointed, now both columns.
  The latent tenant-role 23503 disappears. `isHome` and the mail workspace names are identical where I2 holds (P3).
- **Part B:** every `users` row loses `owner_id` and `approval_status`. Those are copies of the home membership's workspace (I2) and of `Approved`/per-membership state that nothing
  reads since Part A. `role_id` becomes NULL for every non-super-admin row (census P10). Super-admin rows keep theirs. `workspace_memberships`, `roles`,
  `workspaces`, `api_keys`, `user_aliases`, and content are untouched. The one irreversible effect: the dropped and nulled values exist afterwards only in the `pre-db11f` dump.

### 3.8 Deliberately not changed

`TenantResponse` (all fields, including legacy `Id`/`PublicId`), `WorkspaceChoice`, `MeResponse`, `UserResponse` shapes; `users.role_id` column/FK/index; every super-admin
check; `workspace_memberships` schema; audit action strings; `WorkspaceLifecycleService`; DB-18 e-mail wording (E2 already says "accounts that belong only to this workspace");
every historical migration; `NewIdentity`'s signature; `Role.Users`.

## 4. Safety classification

- **Part A: Code only.** No migration. Ordinary `bash scripts/deploy-api.sh` (the `pre-deploy` dump runs anyway). Reversible by `git checkout` of the previous commit.
- **Part B: Contract (R2 step 3) — Destructive.** Two columns, two indexes and one FK are dropped. `role_id` values are nulled for members. Values survive only in `pre-db11f` (R5).
  Both migrations carry marker + `[ContractMigration("DB-11f")]`, so the DB-09 gate refuses an ordinary deploy. They ship **only** as
  `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11f bash scripts/deploy-api.sh`. **R7.1: not batched** with any other doc; Part A is never in the same run.
  **Owner approval required** (header line).
- **Tenancy (R8).** No new entity. R8.7 is strengthened: after Part A no code path derives a workspace from `users`. The mandatory R8.7 tests (tenant B sees no identity of
  A; a membership-ending action in A leaves B untouched) are `Tests/WorkspaceMembershipTests.cs` and stay green unchanged.

## 5. File-level tasks

Work on a branch from `main` per part (`feat/db-11f-a`, then later `feat/db-11f-b` from the `main` that contains Part A). Do **not** touch any file not listed.
After each part: `just fmt`; `just test`; `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` → no changes.

### Part A (reads switch; no migration)

- **A1.** `Application/Services/Interfaces/IMembershipService.cs`: add after the `NewIdentity` declaration (`:51-58`):
  `/// <summary>DB-11f (D11f.3). The identity's HOME workspace: owner of its earliest membership row of ANY state (ended/soft-deleted included; ORDER BY JoinedAt, Id). Replaces the legacy users.owner_id. Null when the identity has no membership (super admins).</summary>`
  `Task<Guid?> HomeWorkspaceIdAsync(int userId);`
- **A2.** `Application/Services/Implementation/MembershipService.cs`: add after `NewIdentity` (after `:138`):
  ```csharp
  /// <inheritdoc />
  public Task<Guid?> HomeWorkspaceIdAsync(int userId) =>
      unitOfWork
          .Repository<WorkspaceMembership>()
          .Query()
          .IgnoreQueryFilters()
          .Where(m => m.UserId == userId)
          .OrderBy(m => m.JoinedAt)
          .ThenBy(m => m.Id)
          .Select(m => (Guid?)m.OwnerId)
          .FirstOrDefaultAsync();
  ```
  Comment `:131` → `// Legacy dual-write, never read since DB-11f Part A (RoleId only, no Role navigation — see JoinAsync); removed by DB-11f Part B.`
  `Tests/Db17DemoServiceTests.cs` (the wrapper class at `:836`): add `public Task<Guid?> HomeWorkspaceIdAsync(int userId) => inner.HomeWorkspaceIdAsync(userId);`.
- **A3.** `Application/Services/Implementation/TenantService.cs`: §3.2, both blocks, verbatim. Check that `using Pointer.Domain.Entity;` already covers `Role` (it does: `Role` is used at `:721`).
- **A4.** `Application/Services/Implementation/AuthService.cs`: the five `HomeWorkspaceIdAsync` substitutions (§3.3 rows 2-3), the super-admin login branch (§3.3 row 4), `:1108`, `:1192`.
- **A5.** `AuthService.cs` null-owner key branch (`else {` at `:1227`): delete `:1230-1246` (both `ApprovalStatus` ifs). The comment `:1229` becomes, followed by the new guard:
  ```csharp
  // Null-owner key = super admin path ONLY (DB-11f D11f.6). Any other identity holding one (a
  // pre-DB-11a leftover — P5 proved none in production) is refused instead of signing in with no
  // workspace and its legacy users.role_id.
  if (user.Role?.IsSuperAdmin != true)
  {
      await AuditApiKeyLoginFailedAsync(apiKey, "invalid_credentials");
      return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);
  }
  ```
- **A6.** `Application/Common/UserMapper.cs`: `:38` → `RoleId = role?.Id ?? 0,`. Replace the `<param name="role">` text `:12-16` with
  `/// DB-11f: the caller's role for THIS session — resolve it with <see cref="SessionRole"/>, never read user.Role/RoleId directly.`. Add before `ToMeResponse`:
  ```csharp
  /// <summary>
  /// DB-11f. The role a session carries: its membership's role when there is a membership (never
  /// the identity's — a membership whose Role is not loaded yields no role); without one, the
  /// identity's own role ONLY for a super admin (users.role_id is the platform role).
  /// </summary>
  public static Role? SessionRole(User identity, WorkspaceMembership? membership) =>
      membership is not null
          ? membership.Role
          : (identity.Role is { IsSuperAdmin: true } platform ? platform : null);
  ```
- **A7.** `Application/Services/Implementation/PreferencesService.cs:58` → `Role? role = UserMapper.SessionRole(user, null);`; `:63` → `role = UserMapper.SessionRole(user, membership);`.
- **A8.** `Application/Services/Implementation/ProfileService.cs`: add `using Pointer.Application.Common;`. In `BuildAsync` (`:98`), directly before the final `return` (`:241`), insert:
  ```csharp
  // DB-11f D11f.7: the CURRENT role — the caller-workspace membership's (or, with no workspace in
  // hand, the home membership's); a super admin shows the platform role. Never users.role_id for a member.
  var roleName = UserMapper.SessionRole(user, null)?.Name;
  if (roleName is null)
  {
      var ms = _unitOfWork
          .Repository<WorkspaceMembership>()
          .Query()
          .IgnoreQueryFilters()
          .Where(m => m.UserId == user.Id);
      roleName = _currentUser.TenantId is Guid tenant
          ? await ms.Where(m => m.OwnerId == tenant && m.LeftAt == null && m.DeletedAt == null)
              .Select(m => m.Role.Name)
              .FirstOrDefaultAsync()
          : await ms.OrderBy(m => m.JoinedAt).ThenBy(m => m.Id)
              .Select(m => m.Role.Name)
              .FirstOrDefaultAsync();
  }
  ```
  and `:249` → `RoleName = roleName ?? string.Empty,`.
- **A9.** `Infrastructure/Auth/JwtTokenService.cs`: add `using Pointer.Application.Common;`. Replace `:78-81` with
  `// DB-11f: UserMapper.SessionRole — the membership's role, or the identity's own role ONLY for a super admin.` /
  `var role = UserMapper.SessionRole(u, membership);` / `var roleId = membership?.RoleId ?? role?.Id ?? 0;`.
- **A10.** `Infrastructure/AppDbContext.cs:108` per §3.3. Append one sentence to the comment `:97-100`:
  `DB-11f: the null-tenant (non-strict) bucket for identities is "belongs to no workspace" (super admins), no longer users.owner_id IS NULL.`
- **A11.** Doc-comments. `Domain/Entity/User.cs:12-16` (`RoleId`) →
  `/// <b>Platform role (DB-11f).</b> Read ONLY for super admins (Role.IsSuperAdmin checks, UserMapper.SessionRole). For every other identity it is a legacy copy of the first membership's role — written at creation, never read — and DB-11f Part B sets it to NULL.`
  `:19` → `/// <summary>See <see cref="RoleId"/> — the platform role (DB-11f).</summary>`. `:28-31` (`ApprovalStatus`) →
  `/// Legacy (DB-11a). Never read since DB-11f Part A (super admins are always Approved — AdminSeeder); dropped by DB-11f Part B. Per-workspace approval is <see cref="WorkspaceMembership.ApprovalStatus"/>.`.
  Above `:51` add `/// <summary><b>Legacy (DB-11a).</b> The creation workspace. Not read since DB-11f Part A (home = IMembershipService.HomeWorkspaceIdAsync); only TenantService's legacy-pointer maintenance writes it. Dropped by DB-11f Part B.</summary>`.
  `Application/Services/Implementation/DemoService.cs:152-154` → `// Legacy (DB-11a) — written once at creation, never read (DB-11f Part A); dropped by DB-11f Part B.`.
  `Application/DTOs/Auth/WorkspaceChoice.cs:20` → `/// <summary>True for the identity's home workspace — its earliest membership of any state (<c>IMembershipService.HomeWorkspaceIdAsync</c>, DB-11f; formerly users.owner_id).</summary>`.
- **A12.** `dotnet build`. The only permitted new errors are in `Tests/`. An error in Application/API/Infrastructure means a reader this doc missed → **stop and report**.
- **A13.** `docs/db/DB-RULES.md` R8, at the end of point 7, append:
  `*(added 2026-09-24, DB-11f)* **"Belongs to W" is membership-only.** An identity belongs to W iff it has a workspace_memberships row of any state in W, and belongs **only** to W iff it has none elsewhere — TenantService.IdentitiesDeletedWithWorkspace is the one implementation (delete set and deletion preview). Its **home** workspace is its earliest membership of any state (IMembershipService.HomeWorkspaceIdAsync). No workspace-scoped fact is read from users: a session's role comes from UserMapper.SessionRole; users.role_id is the platform role (super admins only — DB-11f Part B nulls it for everyone else and drops users.owner_id/approval_status).`
- **A14.** Tests (§6 Part A).

### Part B (contract; only after Part A has been in production ≥ 24 h, §9)

- **B1.** `Domain/Entity/User.cs`: `RoleId` → `public int? RoleId { get; set; }` with summary
  `/// <b>Platform role (DB-11f).</b> Non-null only for super admins (the global is_super_admin role); NULL for every other identity. Read via Role.IsSuperAdmin / UserMapper.SessionRole.`;
  `Role` → `public Role? Role { get; set; }`. Delete the `ApprovalStatus` summary + property (`:28-32`) and the `OwnerId` summary + property (`:51` and the A11 summary).
  If `using Pointer.Domain.Enums;` (`:1`) becomes unused, leave it.
- **B2.** `Infrastructure/Mappings/UserMapping.cs`: delete `:29-33` (the `HasIndex(x => new { x.Email, x.OwnerId })…ux_users_email_owner_live` statement), `:41-43` (`ApprovalStatus`), `:50-56`
  (`OwnerId` property, `HasOne<Workspace>()…fk_users_workspaces_owner_id`, `HasIndex(x => x.OwnerId)`). `:39` → `b.Property(x => x.RoleId).HasColumnName("role_id");`.
  `:69-72` → `b.HasOne(x => x.Role).WithMany(r => r.Users).HasForeignKey(x => x.RoleId).IsRequired(false).OnDelete(DeleteBehavior.Restrict);` with the comment above it
  `// DB-11f: the platform role — optional; non-null only for super admins.`
- **B3.** `MembershipService.cs` per §3.5. Add to `NewIdentity`'s XML doc in `IMembershipService.cs`:
  `/// <remarks>DB-11f: firstRole/firstWorkspaceId are no longer stored on the identity — the caller's JoinAsync records them on the membership. Kept for call-site stability.</remarks>`.
- **B4.** `DemoService.cs` per §3.5 (`:151-156`, `:292`).
- **B5.** `API/Seed/AdminSeeder.cs:150`, `:160` deleted.
- **B6.** `TenantService.cs`: delete the Part A maintenance block. In `HardDeleteOrder` delete `typeof(User),`. Replace its comment (`:808-811`) with
  `// The 22 owner-carrying types, in the same order as the DeleteOwnedAsync<T> calls above. User is not in it since DB-11f (it carries no owner_id): identities are removed by IdentitiesDeletedWithWorkspace, after memberships. Documentation + test input for WorkspaceTests.HardDeleteOrder_CoversEveryOwnerCarryingEntity. Never loop over this in production code.`
  In the comment above `DeleteOwnedAsync<WorkspaceMembership>` delete nothing. It stays accurate.
- **B7.** `JwtTokenService.cs:172` per §3.5.
- **B8.** `.github/workflows/db-migrations.yml:63-64` per §3.5.
- **B9.** `dotnet build`. Permitted errors outside `Tests/`: none. Any error → stop and report. In `Tests/`, fix **only** errors of the form `'User' does not contain a definition for 'OwnerId'`
  / `'ApprovalStatus'` (CS0117 in `new User { … }` initializers: delete that one line; CS1061 in member access: apply §6 recipe T1/T2) and `int?`-conversion errors on `User.RoleId`.
  Never touch `OwnerId`/`ApprovalStatus`/`RoleId` inside `new WorkspaceMembership`, `new Invite`, `new ApiKey`, or any other type's initializer. The compiler names the type.
- **B10.** `just migrate name="DropUsersLegacyTenancyColumns"`. Read it against §3.4 B-1 (R13). **Diff the snapshot:** only the `Pointer.Domain.Entity.User` blocks change
  (`ApprovalStatus` and `OwnerId` property blocks removed; `RoleId` → `b.Property<int?>`; `HasIndex("OwnerId")` and the `HasIndex("Email", "OwnerId")…` + `AreNullsDistinct` lines removed;
  the `Workspace` `HasOne … fk_users_workspaces_owner_id` block removed; the `Role` relationship loses `.IsRequired()`). Anything else → stop (R13, R15). Add marker + attribute.
- **B11.** `just migrate name="ClearUsersRoleIdForMembers"` → must scaffold empty with **no** snapshot change (if not empty → stop). Write the §3.4 B-2 body.
- **B12.** Docs (same PR). `docs/db/SCHEMA.md` `users` row (`:83`): FK cell → `role_id → roles` (Restrict, **nullable — platform role, super admins only**); `merged_into_user_id → users` (self FK, Restrict)`.
  Remove `ux_users_email_owner_live (…) (DB-05, kept — dropped by DB-11f);` from the index cell. Replace the sentence
  `` `owner_id`/`role_id`/`approval_status` are legacy (written once at creation, never read after this doc; dropped by DB-11f). `` with
  `` **DB-11f (<deploy date>):** `owner_id`, `approval_status`, `ux_users_email_owner_live`, `IX_users_owner_id`, `fk_users_workspaces_owner_id` dropped; `role_id` is the platform role (NULL except super admins). Workspace presence = memberships only (DB-RULES R8.7). ``
  The owner cell `nullable (NULL = super admin, legacy elsewhere)` → `none (identity table; workspace presence is workspace_memberships)`. The migration count note → 83 after deploy.
  `docs/db/DB-REVIEW-2026-09-22.md` §7 DB-11f row status (release step 8).

## 6. Tests

Copy patterns from: `Tests/WorkspaceTests.cs:168-205` (Sqlite `TestDb`, real FKs, `db.MakeContext`), `Tests/WorkspaceMembershipTests.cs` (`Ctx(...)`, `SeedTwoWorkspaces`),
`Tests/TestSeed.cs:25-60` (`TestSeed.Join`), `Tests/Db18WorkspaceLifecycleTests.cs:2050-2123` (preview/delete parity).

**Recipes for rewriting assertions on removed members** (used in Part B; T0 also in Part A):
- **T0** A Part A test fails because a seeded `User` has `OwnerId` but **no membership row**. Add `TestSeed.Join(seed, user, <that OwnerId>, <its role>)` right after the user's
  `SaveChanges()`. Any other Part A failure → stop and report.
- **T1** `Assert.Equal(W, x.OwnerId)` → `Assert.Contains(<ctx>.WorkspaceMemberships.IgnoreQueryFilters().Where(m => m.UserId == x.Id).ToList(), m => m.OwnerId == W);`
- **T2** a lookup `w.Id == x.OwnerId` → first `var xHome = <ctx>.WorkspaceMemberships.IgnoreQueryFilters().Where(m => m.UserId == x.Id).OrderBy(m => m.JoinedAt).ThenBy(m => m.Id).Select(m => m.OwnerId).First();`, then `w.Id == xHome`.
- **T3** production-created identity `Assert.Equal(R, x.RoleId)` → `Assert.Null(x.RoleId); Assert.Equal(R, <ctx>.WorkspaceMemberships.IgnoreQueryFilters().Single(m => m.UserId == x.Id && m.LeftAt == null).RoleId);`

### Part A — edit
1. `Tests/WorkspaceTests.cs` 5b (`:530-618`): keep the name. After `seed.Users.Add(erased); await seed.SaveChangesAsync();` add an **ended** membership of `erased` in `ownerPublicId`
   (`RoleId = role.Id, IsActive = false, ApprovalStatus = Approved, JoinedAt = UtcNow.AddDays(-2), LeftAt = UtcNow.AddDays(-1), LeftReason = MembershipEndReason.AccountErased, SecurityStamp = Guid.NewGuid()`).
   This is the shape `IdentityEraseService` actually leaves. Also add a live membership for `admin`. Update the comment `:574-581`: the erased identity "keeps its ended membership (EndAsync), so the membership rule selects it".
2. `Tests/TokenServiceTests.cs:9-21`: pass `new WorkspaceMembership { Id = 1, OwnerId = Guid.NewGuid(), RoleId = role.Id, Role = role, SecurityStamp = Guid.NewGuid() }` instead of `null`. Assertions unchanged.
3. `Tests/TenantQueryFilterTests.cs`: `:189` → `Assert.Equal("a@x", results[0].Email);`. In `NullTenant_NonSuper_DefaultFlag_SeesNullOwnerBucket` (`:200-257`) keep a reference to the first user
   (`var a = new User { … }` then `seed.Users.Add(a)`). After `seed.SaveChanges();` add `TestSeed.Join(seed, a, tenantA, new Role { Id = 1 });`. Replace `:255-256` with
   `Assert.All(results, u => Assert.StartsWith("n", u.Email));` / `Assert.DoesNotContain(results, u => u.Email == "a@x");`.
4. Any other failure → recipe T0 or stop.

### Part A — add `Tests/Db11fMembershipRulesTests.cs` (namespace as siblings)
5. `DeleteSet_IsMembershipOnly_EveryShape` (InMemory `Ctx`). Workspaces W, X; a global super role (`IsSuperAdmin = true, OwnerId = null`) and a W role. Seed each identity with `OwnerId` exactly as production would:
   (a) owner W, live membership W → **in**; (b) owner W, live W + ended X → out; (c) owner X, live X + live W → out; (d) owner W, erased (`DeletedAt`, `ErasedAt`) with ended W membership → **in**;
   (e) owner W, soft-deleted with pending W membership → **in**; (f) super admin (super role, owner null, no membership) → out; (g) super admin with an anomalous live W membership → out;
   (h) merged row (`MergedIntoUserId = (a).Id`, `DeletedAt` set, no membership) → **in**; (i) merged row with alias `SourceWorkspaceId = W` whose canonical is (c) → **in**;
   (j) non-super, owner W, **no membership** → out. Assert `IdentitiesDeletedWithWorkspace(uow, W).Select(u => u.Email)` equals exactly {a, d, e, h, i}.
6. `DeleteSet_EqualsLegacyRule_WhenInvariantI1Holds`. For shapes (a)-(g) only, assert the new set equals `Users.IgnoreQueryFilters().Where(u => u.OwnerId == W && !WorkspaceMemberships.IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.OwnerId != W))`
   (the pre-DB-11f predicate, inlined in the test; Part B deletes this test with a one-line PR note, because the column is gone).
7. `HardDelete_RehomedIdentityWithTenantRole_Succeeds_UnderRealForeignKeys` (Sqlite `TestDb` copied from `WorkspaceTests.cs:168`). The identity is created in W with a **W-owned** role
   (`users.role_id` = it, `OwnerId = W`), with memberships in W (that role) and X (a global role). `HardDeleteAsync(W)` → success. The identity survives with `OwnerId == X`, `RoleId ==` its X membership's role, and the W role is gone.
   On `main` this test fails with a FK error (the §2 latent bug). Say so in the PR.
8. `HardDelete_MembershipLessIdentityReferencingWorkspace_Throws_AndDeletesNothing` (Sqlite). Shape (j) plus a normal admin. `HardDeleteAsync(W)` throws `InvalidOperationException` whose message contains `DB-11f invariant I1`.
   Afterwards the workspace row and both users still exist. Part B deletes this test (shape j then survives; see test 16).
9. `HomeWorkspace_IsEarliestMembershipOfAnyState`. Identity joined A at t0 (then ended), B at t1 → `A`. No memberships → `null`. Equal `JoinedAt` → lower `Id` wins.
10. `Login_Picker_IsHome_FollowsEarliestMembership`: copy `WorkspaceSwitchTests.cs:260-296`, but seed the identity with `OwnerId = workspaceB` and join A **before** B.
    Assert `IsHome` is on A. This proves `owner_id` is no longer read.
11. `PasswordResetMail_NamesHomeWorkspace_NotLegacyOwner`: copy `ChangePasswordTests.RequestPasswordReset_NamedWorkspace_BodyNamesIt` (`:343`). The user has `OwnerId` = a workspace named "Legacy"
    and a single membership in a workspace named "Home". The body contains "Home" and not "Legacy".
12. `SessionRole_NeverFallsBackToIdentityRole_ForMembers`: (i) `JwtTokenService.Issue(nonSuperIdentityWithRoleGrantsAdmin, null)` → claims `role` "", `role_id` "0", `is_admin` "false";
    (ii) `Issue(identity, membershipWithRoleNull)` → `role` "", `role_id` = membership's `RoleId`; (iii) a super admin with `null` → `is_super_admin` "true", `role_id` = super role id.
    (iv) `AuthService.MeAsync` for a tenant token whose membership was ended → `IsAdmin == false` (on `main` the legacy identity role makes it `true`).
13. `ApiKeyLogin_NullOwnerKey_NonSuperAdmin_Refused` (copy `Tests/ApiKeyAuthTests.cs` setup): non-super identity with a null-owner live key → `Failure(InvalidApiKey)`, and one audit row with reason `invalid_credentials`.
    The super admin's null-owner key still signs in (MFA not enrolled).
14. `Profile_RoleName_IsCurrentMembershipRole`: identity created with role "Developer", membership later changed to "PM" → `GET` profile as that tenant → `"PM"`. The super admin's own profile → super role name.
15. **Guards that must pass unchanged:** `Db18WorkspaceLifecycleTests.Preview_AccountsCount_EqualsRowsActuallyDeleted` (`:2050`), `WorkspaceMembershipTests.HardDelete_Workspace_KeepsMultiWorkspaceIdentity_EndsOnlyThatMembership` (`:527`),
    `WorkspaceTests.HardDelete_RemovesEverything_EvenWithSuggestionNotification` (`:388`), `WorkspaceSwitchTests` `IsHome` asserts (`:294-296,741-742`), the whole `Tests/WorkspaceMembershipTests.cs`
    (**R8.7 tenancy proof: tenant B sees no identity of A**), `Tests/TenantQueryFilterTests.cs`, `Tests/AuditCoverageTests.cs`. List them in the PR as run.

### Part B
16. Delete tests 6 and 8 (they read `OwnerId`). Add `HardDelete_MembershipLessIdentity_Survives_AndDoesNotBlock` (Sqlite, shape j without `OwnerId`): `HardDeleteAsync(W)` succeeds and the identity row survives.
17. `User_HasNoLegacyTenancyMembers` (copy `Tests/Db11eLegacyDemoStateRemovedTests.cs`'s model check): `typeof(User).GetProperty("OwnerId")` and `("ApprovalStatus")` are null, and so is `db.Model.FindEntityType(typeof(User))!.FindProperty(...)` for both.
    `FindEntityType(typeof(User))!.GetIndexes()` has none named `ux_users_email_owner_live` and none over `OwnerId`. `FindProperty("RoleId")!.IsNullable` is `true`. `Role`, `IsActive`, `MergedIntoUserId` exist (guards over-deletion).
18. `NewIdentity_And_DemoProvision_WriteNoPlatformRole`: `NewIdentity(...).RoleId` is null. After `DemoService.ProvisionAsync` **and its audit write**, the demo identity's `RoleId` is null in a fresh context. This catches the `:292` fix-up.
    `AdminSeeder` run against an empty InMemory DB → the super admin has `RoleId` = the super role.
19. `Tests/WorkspaceTests.cs:381` → `Assert.Equal(22, TenantService.HardDeleteOrder.Length);`. In `HardDelete_RemovesEverything…` (`:388`), keep a variable for the seeded admin's `Id` and add
    `Assert.Null(verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == <that id>));` (the per-type loop no longer covers `User`).
20. Semantic rewrites (all from §2's scan): `Db17DemoServiceTests.cs:960` T2; `Db18WorkspaceLifecycleTests.cs:2121` T1 (`verify`, `survivor`, `otherWorkspaceId`);
    `DemoUpgradeTests.cs:295` → `Assert.Equal(demoWorkspaceId, row.OwnerId);`; `InviteServiceTests.cs:624-625` T3+T1, `:816` → `Assert.Null(created.RoleId);` (membership asserts `:820-826` already cover the role),
    `:819` delete, `:824` → `Assert.NotEqual(Guid.Empty, membership.OwnerId);`, `:828` delete, `:917` delete, `:919-920` T1+T3, `:991` T3 (`roleId`, `pick@role.com`),
    `:1787` and `:1841` → `Assert.Empty(db.WorkspaceMemberships.IgnoreQueryFilters().Where(m => m.RoleId == clientRoleId));`, `:1884-1886` T3+T1 (+ delete the `ApprovalStatus` assert);
    `WorkspaceAdminOwnershipTests.cs:190-191` T3+T1, `:236` T1, `:285-286` T3+T1; `WorkspaceBeforeIdentityOrderingTests.cs:253` T1, `:294-295` T2; `WorkspaceMembershipTests.cs:619` T1 (`verify`, `survivingX`, `workspaceB`),
    `:793` T1 (`verify2`); `WorkspaceTests.cs:328` T2; `TokenServiceTests.cs:44,49` delete `, OwnerId = tenantId` / `, OwnerId = null`.
    Assertions on the **seeded** `RoleId` of test-created users (`DeletionSemanticsTests.cs:351`, `UserGovernanceTests.cs:641`, `RoleServiceDeleteTests.cs:149`) keep passing unchanged. Leave them.
21. **Existing data survives** — Postgres-only, proven in the R11 rehearsal (§7 criterion B6), pasted into the PR.

## 7. Acceptance criteria

**Part A**
1. `dotnet ef migrations list -p Infrastructure -s API --no-connect | wc -l` → unchanged vs `main` (81 migrations; no new file under `Infrastructure/Migrations/`); `git diff --stat main -- Infrastructure/Migrations` → empty.
2. **Zero legacy readers (scan).** Commit, then:
   ```bash
   rm -rf /tmp/db11f-scan && mkdir -p /tmp/db11f-scan && git archive HEAD | tar -x -C /tmp/db11f-scan && cd /tmp/db11f-scan
   python3 - <<'EOF'
   p='Domain/Entity/User.cs'; s=open(p).read()
   for old,tag in [('    public int RoleId { get; set; }','ROLEID'),('    public Role Role { get; set; } = null!;','ROLE'),
                   ('    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Approved;','APPROVAL'),('    public Guid? OwnerId { get; set; }','OWNERID')]:
       assert old in s, old
       s=s.replace(old,'    [System.Obsolete("DB11F_'+tag+'")]\n'+old)
   open(p,'w').write(s)
   EOF
   dotnet build Pointer.sln -nologo --no-incremental 2>&1 | grep -E "warning CS0618.*DB11F" | sed -E 's#^.*/db11f-scan/##; s/ \[.*\]$//' | sort -u | grep -v '^Tests/' > /tmp/db11f-a.txt
   grep DB11F_OWNERID  /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   grep DB11F_APPROVAL /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   grep DB11F_ROLEID   /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   ```
   Expected occurrence counts (one per member access; `sort -u` keeps line+column, so two accesses on one line count twice). **OWNERID**: `Infrastructure/Mappings/UserMapping.cs` 4,
   `Application/Services/Implementation/MembershipService.cs` 1, `…/DemoService.cs` 1, `…/TenantService.cs` 3 (all inside the `DB-11f PART A ONLY` block: the `Where`, the `if`, the assignment).
   No `AuthService.cs` and no `AppDbContext.cs`. **APPROVAL**: `UserMapping.cs` 1, `MembershipService.cs` 1, `DemoService.cs` 1, `API/Seed/AdminSeeder.cs` 3. No `AuthService.cs`.
   **ROLEID**: `UserMapping.cs` 2, `MembershipService.cs` 1, `DemoService.cs` 1, `AdminSeeder.cs` 3, `Infrastructure/Auth/JwtTokenService.cs` 1 (`IssueImpersonation`, super admin), `TenantService.cs` 4
   (three in the maintenance block and `id == u.RoleId` in `IdentitiesDeletedWithWorkspace`, the super-admin exclusion). No `UserMapper.cs` and no `AuthService.cs`.
   A file missing from, or added to, these lists is a finding → stop and report. **ROLE**:
   `grep "DB11F_ROLE'" /tmp/db11f-a.txt | while IFS='(' read f rest; do l=${rest%%,*}; sed -n "${l}p" "/tmp/db11f-scan/$f"; done | grep -vE 'Include\(u => u\.Role\)|IsSuperAdmin|operatorUser\.Role|HasOne\(x => x\.Role\)|demoUser\.Role = role'` → **no output**.
   Paste all four outputs in the PR. Then `rm -rf /tmp/db11f-scan`.
3. `grep -n "OwnerId == workspaceId" Application/Services/Implementation/TenantService.cs` → only `DeleteOwnedAsync` lines, the `Role` query, and one line inside the maintenance block;
   `grep -c "DB-11f invariant I1" Application/Services/Implementation/TenantService.cs` → 1; `grep -c "HomeWorkspaceIdAsync" Application/Services/Implementation/AuthService.cs` → 5;
   `grep -c "ApprovalStatus.Pending\|ApprovalStatus.Rejected" Application/Services/Implementation/AuthService.cs` → the count on `main` minus 4; `grep -c "SessionRole" Application Infrastructure -r` → ≥ 6.
4. `just test` green (tests 5, 7-14 new; 1-3 edited; 15 unchanged). CI green (DB-10 job, `MigrationSafetyTests`).
5. **Prod pre-checks P1-P7 (§9) run on a same-day dump** (the rehearsal copy is enough for Part A): P1, P2, P4, P5 print the expected results. P3/P6/P7 pasted.
6. `scripts/local-e2e-gate.sh <Part A worktree>` → **PASS**.

**Part B**
1. `dotnet ef migrations list -p Infrastructure -s API --no-connect | tail -3` → `…_AddWorkspacesPauseAndDeletionState`, `…_DropUsersLegacyTenancyColumns`, `…_ClearUsersRoleIdForMembers`; total 83.
2. B-1 file: `grep -c "DropForeignKey(" ` → 1, `grep -c "DropIndex(" ` → 2, `grep -c "DropColumn(" ` → 2, `grep -c "AlterColumn<int>(" ` → 2, `grep -c "AddColumn<" ` → 2, `grep -c "CreateIndex(" ` → 2,
   `grep -c "AddForeignKey(" ` → 1 (each +1 only if the permitted `FK_users_roles_role_id` pair appeared), `grep -c "defaultValue: 0"` → 0, `grep -c 'ContractMigration("DB-11f")'` → 1, `grep -c "R2 contract approved"` → 1, `grep -c "\.Sql("` → 0.
   B-2 file: `grep -c "\.Sql("` → 2, `grep -c "SET role_id = NULL"` → 1, `grep -c 'ContractMigration("DB-11f")'` → 1, `grep -c "R2 contract approved"` → 1; its Designer's model equals B-1's (`diff` of the two `BuildTargetModel` bodies → empty).
3. `grep -cE "OwnerId|ApprovalStatus" Domain/Entity/User.cs` → 0 (the word must not appear, even in comments: write "owner id"/"approval" in prose if needed). `grep -c "ux_users_email_owner_live\|fk_users_workspaces_owner_id" Infrastructure/Mappings/UserMapping.cs` → 0.
   `awk '/modelBuilder.Entity\("Pointer.Domain.Entity.User", b =>/{f=1} f&&/^                }\);/{f=0} f' Infrastructure/Migrations/AppDbContextModelSnapshot.cs | grep -cE '"OwnerId"|"ApprovalStatus"|ux_users_email_owner_live'` → 0 (16-space closing pattern, DB-11e §12 note).
4. `grep -rn "DB-11f PART A ONLY" Application` → nothing; `grep -c "typeof(User)" Application/Services/Implementation/TenantService.cs` → 0; `grep -c "demoUser.Role = role" Application/Services/Implementation/DemoService.cs` → 0;
   `grep -c "approval_status" .github/workflows/db-migrations.yml` → 0.
5. `just test` green; `has-pending-model-changes` → no changes; CI DB-10 green (from empty, then newest `Down()`/`Up()` = B-2 round-trip).
6. **R11 rehearsal on a same-day prod dump in a throwaway Postgres 15** (commands below), output pasted into the PR:
   - before `database update`: P1-P10 of §9 print the production-expected results (P8: 3 columns, 4 indexes, 3 FKs, 0 dependent views, `81 | 20260923220301_AddWorkspacesPauseAndDeletionState`);
   - **existing-data seed on Part A code** (`main` before the Part B PR) booted against the rehearsal DB: log in as the super admin (local `.env` `ADMIN__EMAIL`/`ADMIN__PASSWORD`; the seeder reconciles it)
     → `GET /api/auth/me` `isSuperAdmin: true`, record `roleName`. `POST /api/admin/tenants` `{"email":"db11f-a@example.com","password":"<≥10 chars>","displayName":"A"}` (`CreateTenantRequest`) twice
     (A, B) → record both `workspaceId`s. `POST /api/demo {"email":"db11f-demo@example.com"}` → save credentials, log in, record `/me` (`roleName` "Workspace Admin", `workspaces[0].isHome: true`).
     Census `SELECT (SELECT count(*) FROM users), (SELECT count(*) FROM workspaces), (SELECT count(*) FROM workspace_memberships), (SELECT count(*) FROM comments), (SELECT count(*) FROM users WHERE role_id IS NOT NULL);`;
   - `dotnet ef migrations script --idempotent -p Infrastructure -s API -o /tmp/db11f-pending.sql` — read it: exactly B-1 and B-2 pending, the §3.4 SQL;
   - `database update` applies both; `\d users` shows no `owner_id`/`approval_status`, `role_id` nullable, `IX_users_role_id` + `FK_users_roles_role_id` present, `ux_users_email_live` present;
     census identical except the last number = P4's super-admin count; `SELECT count(*) FROM users u JOIN roles r ON r.id = u.role_id WHERE NOT r.is_super_admin` → 0;
   - **on Part B code**: super admin login → `/me` identical to before (`isSuperAdmin: true`, same `roleName`); demo login → `/me` identical (`roleName`, `isHome`); `GET /api/me/profile` as the demo → `roleName` "Workspace Admin";
     `DELETE /api/admin/tenants/{A}` as super admin → 200, and afterwards `db11f-a@example.com` has no `users` row while B and its admin are intact; `POST /api/demo` again → the new identity has `role_id IS NULL`;
     no `42703`/`does not exist`/`23503` in either API log;
   - **rollback drill (§8 path A):** `dotnet ef migrations script <ts>_ClearUsersRoleIdForMembers 20260923220301_AddWorkspacesPauseAndDeletionState -p Infrastructure -s API -o /tmp/db11f-down.sql`, apply with
     `docker exec -i pointer-db11f-rehearsal psql -U pointer -d pointer_rehearsal -v ON_ERROR_STOP=1 -1 < /tmp/db11f-down.sql` → columns/indexes/FK back, `role_id NOT NULL` with no NULLs, `approval_status` all 1,
     `owner_id` all NULL, 81 history rows. Boot **Part A code** against it → super admin + demo log in, `DELETE /api/admin/tenants/{B}` → 200. Then `database update` again → 83.
7. `scripts/local-e2e-gate.sh <Part B worktree>` → **PASS**.

Rehearsal commands (throwaway container; never the shared dev DB, never production) — same as DB-11e §7 with the names changed:
```bash
# 1. same-day dump: on the VM, if the newest ~/backups/pointer-*.dump is not from today:
#      bash ~/pointer-api/scripts/backup-db.sh rehearsal-db11f        (read-only for prod)
#    fetch it (ssh details: DEPLOY.md / memory "Prod VM deploy access") to /tmp/prod.dump
docker run -d --rm --name pointer-db11f-rehearsal -e POSTGRES_USER=pointer -e POSTGRES_PASSWORD=pointer \
  -e POSTGRES_DB=pointer_rehearsal -p 5439:5432 postgres:15
until docker exec pointer-db11f-rehearsal pg_isready -U pointer >/dev/null; do sleep 1; done
docker exec -i pointer-db11f-rehearsal pg_restore -U pointer -d pointer_rehearsal --no-owner --no-privileges < /tmp/prod.dump
export ConnectionStrings__Default="Host=localhost;Port=5439;Database=pointer_rehearsal;Username=pointer;Password=pointer"
# queries:  docker exec -i pointer-db11f-rehearsal psql -U pointer -d pointer_rehearsal -c "<§9 P…>"
# API (each code version from its own worktree):
#   set -a; . ./.env; set +a; ASPNETCORE_URLS=http://localhost:8095 DBMigrationEnabled=false dotnet run --project API
# throw away:  docker stop pointer-db11f-rehearsal
```
If the rehearsal API does not boot with that env, report it. Do not change application config to make it boot.

## 8. Rollback

**Part A.** Code only. `git checkout <commit before the Part A merge>` on the VM, then `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api`. No data to restore.
The columns were written throughout.

**Part B.**
- **Irreversible part (bold, R5): the values of `users.owner_id`, `users.approval_status`, and `users.role_id` of every non-super-admin identity are destroyed.** `Down()` recreates
  the columns with `owner_id` NULL and `approval_status` = 1. B-2's `Down()` refills `role_id` from the earliest membership, **not** the original creation role. The only copy of the originals is the
  `pre-db11f` dump the contract deploy takes immediately before the migrations (R7). This is acceptable because Part A code reads none of them for behaviour (§7 Part A criterion 2).
- **Migration fails while applying:** each migration runs in its own transaction. If B-1 fails, nothing is applied. If B-2 fails, B-1 stays applied. Either way the API exits (DB-09).
  Answer: path B (restore `pre-db11f`), never a hand-fix of the schema or `__EFMigrationsHistory` (R7.1 point 6 spirit).
- **Migrations applied, new code misbehaves.** Part A code maps `OwnerId`/`ApprovalStatus`, so it cannot run on the contracted schema (42703). Options in order of preference:
  - **A (lossless for live data, rehearsed in §7 B6):** generate `/tmp/db11f-down.sql` on a workstation (command in §7 B6) and read it (B-2 refill `UPDATE`, B-1 inverse, two `DELETE FROM "__EFMigrationsHistory"`).
    Copy it to the VM. `docker compose -f docker-compose.prod.yml stop api`; `bash scripts/backup-db.sh pre-db11f-rollback`;
    `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -v ON_ERROR_STOP=1 -1 < db11f-down.sql`;
    `git checkout <commit before the Part B merge>` (= Part A); `up -d --build api`. Part A code works with `owner_id` NULL: it reads nothing from it. Its maintenance block only
    re-points rows that point at the deleted workspace, and NULL rows point nowhere.
  - **B (if A fails):** restore `pre-db11f` (`DEPLOY.md` § Restore, API stopped, `pre-restore` dump first), `git checkout` Part A, `up -d --build api`. This loses every write since the deploy.
- **Never** roll back past Part A by `Down()` after Part B is live without path A's refill. Pre-DB-11f code (`main` @ `8f97871`) **reads** `owner_id` for the delete set. With `owner_id` NULL
  everywhere, a workspace deletion would delete no identities and would orphan them. Rolling back to before Part A after Part B means restoring `pre-db11f`.

## 9. Release steps

**Part A (ordinary deploy)**
0. Owner decisions §0 answered or defaults accepted. Note which ones in the PR.
1. **Prod pre-checks (read-only; paste output into the PR).** Run on the VM as `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "<query>"`:
   - **P1 — invariant I1 (blocking, must print 0 rows):**
     ```sql
     SELECT u.id, u.owner_id, u.deleted_at, u.erased_at FROM users u
     WHERE u.owner_id IS NOT NULL AND u.merged_into_user_id IS NULL
       AND NOT EXISTS (SELECT 1 FROM workspace_memberships m WHERE m.user_id = u.id AND m.owner_id = u.owner_id);
     ```
   - **P2 — delete-set parity for every workspace (blocking, must print 0 rows):**
     ```sql
     WITH old AS (
       SELECT w.id AS ws, u.id AS uid FROM workspaces w JOIN users u ON u.owner_id = w.id
       WHERE NOT EXISTS (SELECT 1 FROM workspace_memberships m WHERE m.user_id = u.id AND m.owner_id <> w.id)),
     core AS (
       SELECT w.id AS ws, u.id AS uid FROM workspaces w CROSS JOIN users u
       WHERE EXISTS (SELECT 1 FROM workspace_memberships m WHERE m.user_id = u.id AND m.owner_id = w.id)
         AND NOT EXISTS (SELECT 1 FROM workspace_memberships m WHERE m.user_id = u.id AND m.owner_id <> w.id)
         AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = u.role_id AND r.is_super_admin)),
     new AS (
       SELECT ws, uid FROM core
       UNION
       SELECT w.id, u.id FROM workspaces w CROSS JOIN users u
       WHERE u.merged_into_user_id IS NOT NULL
         AND (EXISTS (SELECT 1 FROM core c WHERE c.ws = w.id AND c.uid = u.merged_into_user_id)
              OR EXISTS (SELECT 1 FROM user_aliases a WHERE a.alias_public_id = u.public_id AND a.source_workspace_id = w.id)))
     SELECT 'old_only' AS side, * FROM (SELECT * FROM old EXCEPT SELECT * FROM new) x
     UNION ALL
     SELECT 'new_only', * FROM (SELECT * FROM new EXCEPT SELECT * FROM old) y;
     ```
     Any row = the deletion rule would change for a real account → **stop, report to the owner, ship nothing**.
   - **P3 — invariant I2, home = legacy owner (expected 0 rows; a row changes only that identity's `isHome` badge and mail workspace name → paste it and ask the owner, D11f.3):**
     ```sql
     SELECT u.id, u.owner_id, h.owner_id AS derived_home FROM users u
     LEFT JOIN LATERAL (SELECT m.owner_id FROM workspace_memberships m WHERE m.user_id = u.id ORDER BY m.joined_at, m.id LIMIT 1) h ON TRUE
     WHERE u.merged_into_user_id IS NULL
       AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = u.role_id AND r.is_super_admin)
       AND u.owner_id IS DISTINCT FROM h.owner_id;
     ```
   - **P4 — super admins (blocking): every row `owner_id` NULL, `approval_status` 1, 0 memberships; and no non-super identity has `owner_id` NULL:**
     ```sql
     SELECT u.id, u.owner_id, u.approval_status, u.is_active, u.deleted_at,
            (SELECT count(*) FROM workspace_memberships m WHERE m.user_id = u.id) AS memberships
     FROM users u JOIN roles r ON r.id = u.role_id WHERE r.is_super_admin;
     SELECT count(*) FROM users u WHERE u.owner_id IS NULL AND u.merged_into_user_id IS NULL
       AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = u.role_id AND r.is_super_admin);   -- 0
     ```
   - **P5 — null-owner live API keys belong only to super admins (blocking, 0 rows; D11f.6):**
     ```sql
     SELECT k.id, k.user_id FROM api_keys k JOIN users u ON u.id = k.user_id
     WHERE k.owner_id IS NULL AND k.revoked_at IS NULL AND k.deleted_at IS NULL
       AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id = u.role_id AND r.is_super_admin);
     ```
   - **P6 — exposure to the latent tenant-role FK bug (informational; Part A fixes it):**
     ```sql
     SELECT u.id, u.owner_id, r.id AS role_id, r.owner_id AS role_owner FROM users u JOIN roles r ON r.id = u.role_id
     WHERE r.owner_id IS NOT NULL AND r.owner_id IS DISTINCT FROM u.owner_id;
     SELECT count(*) FROM workspace_memberships m JOIN roles r ON r.id = m.role_id
     WHERE r.owner_id IS NOT NULL AND r.owner_id <> m.owner_id;   -- expected 0; non-zero = a separate bug, report it
     ```
   - **P7 — merged/aliases census (expected `0 | 0`):** `SELECT (SELECT count(*) FROM users WHERE merged_into_user_id IS NOT NULL), (SELECT count(*) FROM user_aliases);`
2. Local e2e gate (§7 A6) PASS.
3. Merge; CI green; ordinary `bash scripts/deploy-api.sh` (`DEPLOY.md` § Updating). The pre-flight must show **no** pending migration.
4. **Verify:** `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "error|DB-11f invariant"` → nothing. Log in to `app.pointer.moamen.work` as super admin (Tenants page lists every workspace)
   and as a workspace admin (the picker/switcher shows the same "Home" badge as before). The DB-18 settings "Delete workspace" preview shows the same account count as before the deploy (compare with P2's `old` set).
5. **Watch (≥ 24 h, this is the gate for Part B):** no `DB-11f invariant I1` line, no `23503`, no `42703`; `DemoCleanupService`/`WorkspaceDeletionService` sweeps log normally.

**Part B (contract deploy, alone, ≥ 24 h after Part A)**
0. Owner approval line (header) filled in and pasted verbatim into the PR; both markers match it.
1. Re-run P1, P2, P4, P5 (must still pass) and additionally:
   - **P8 — schema is what B-1 expects (blocking):**
     ```sql
     SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns
     WHERE table_schema = 'public' AND table_name = 'users' AND column_name IN ('owner_id','role_id','approval_status') ORDER BY 1;  -- approval_status integer NO 1 | owner_id uuid YES | role_id integer NO
     SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'users'
       AND indexname IN ('ux_users_email_owner_live','IX_users_owner_id','IX_users_role_id','ux_users_email_live') ORDER BY 1;          -- 4 rows
     SELECT conname FROM pg_constraint WHERE conrelid = 'users'::regclass AND contype = 'f' ORDER BY 1;                                 -- FK_users_roles_role_id, fk_users_merged_into_user, fk_users_workspaces_owner_id
     SELECT DISTINCT v.relname FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid JOIN pg_class v ON v.oid = r.ev_class
     JOIN pg_attribute a ON a.attrelid = d.refobjid AND a.attnum = d.refobjsubid
     WHERE d.refobjid = 'users'::regclass AND a.attname IN ('owner_id','role_id','approval_status');                                   -- 0 rows
     SELECT count(*), max("MigrationId") FROM "__EFMigrationsHistory";                                                                 -- 81 | 20260923220301_AddWorkspacesPauseAndDeletionState
     ```
   - **P9 — B-2 `Down()` has a fallback role (blocking, ≥ 1):** `SELECT count(*) FROM roles WHERE owner_id IS NULL AND NOT is_super_admin;`
   - **P10 — census of what B-2 nulls (informational):**
     `SELECT count(*) FILTER (WHERE r.is_super_admin) AS kept, count(*) FILTER (WHERE NOT r.is_super_admin) AS nulled FROM users u JOIN roles r ON r.id = u.role_id;`
2. R11 rehearsal (§7 B6) and local e2e gate (§7 B7), both pasted into the PR.
3. Merge; CI green (DB-10, `MigrationSafetyTests`).
4. **Contract deploy, alone:** `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11f bash scripts/deploy-api.sh`. The pre-flight must list exactly `…_DropUsersLegacyTenancyColumns` and
   `…_ClearUsersRoleIdForMembers`. The script stops `api`, writes `~/backups/pointer-<ts>-pre-db11f.dump`, and boots.
5. **Verify:** `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|error|42703|23503|does not exist"` → two `Applying migration` lines, no errors. P8's first query → 1 row
   (`role_id integer YES`); the index query → 2 rows (`IX_users_role_id`, `ux_users_email_live`); the FK query → 2 rows; history → `83 | …_ClearUsersRoleIdForMembers`; P10 → `nulled 0`.
   Smoke: super admin logs in (MFA if enrolled) and sees the Tenants page. A workspace admin logs in and sees the switcher "Home" badge and a profile role name. A new demo on `demo.pointer.moamen.work` works
   (banner, extend once); its identity has `role_id IS NULL` (`SELECT role_id FROM users WHERE email LIKE 'demo-%' ORDER BY id DESC LIMIT 1;`).
6. **Watch (first hour):** Npgsql `42703`, `23503` from `HardDeleteAsync`, `AUDIT GAP`, 401 bursts on `/api/auth/login-with-key` (a stray non-super null-owner key; P5 said none).
7. **Client/dashboard:** no DTO shape change in either part. The `dashboard-agent` regenerates from production at its next phase run. Expected diff: at most the `WorkspaceChoice.isHome` JSDoc text.
   No dashboard or widget source change. The `rebranding-agent` records the dropped `users` columns/index in its inventory (project rule 6).
8. Stamp docs: this header (`Part A deployed <date/time> UTC, <commit>`; `Part B deployed <date/time> UTC, <commit>, 83 migrations`), `DB-REVIEW-2026-09-22.md` §7 DB-11f row status, `SCHEMA.md` (task B12).

## 10. Out of scope

`TenantResponse.Id` / `PublicId` (membership-sourced legacy DTO fields; the dashboard reads `publicId` — removing `Id` is a separate API-contract change for the dashboard-agent, not a schema change);
replacing `users.role_id` with a boolean (D11f.2 alternative → DB-11g if chosen); `NewIdentity`'s now-unused parameters; FKs from content `author_id` columns (Q5);
`workspace_memberships` schema or states; the DB-18 deletion e-mail/i18n wording; `DemoCleanupService`, `WorkspaceDeletionService`, `WorkspaceLifecycleService` (they call the shared rule unchanged);
every historical migration file (R10); `clients/` (generated); the `pointer-dashboard` repo; widget, CLI, extension, landing; `API/wwwroot` served docs and `docs/ON-DISK-CONTRACT.md` (nothing in them names these columns).

## 11. Dashboard / widget / CLI tasks

**Dashboard:** none in source. `isHome`, `roleName`, `approvalStatus`, `publicId` keep their meaning. **Widget:** none (`templates.ts:71` reads `isHome`, semantics kept). **CLI:** none. Only a stray
non-super **null-owner** key stops working (D11f.6). CLI keys minted since DB-11a carry the workspace (`owner_id`), so they are unaffected.
