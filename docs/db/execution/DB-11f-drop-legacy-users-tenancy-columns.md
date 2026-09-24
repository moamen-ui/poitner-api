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
Paste it verbatim into the Part B PR and the two markers. Part B still ships only after Part A has been live ≥ 24 h, P1–P5 pass on production, and the R11 rehearsal passes (plus the blocking checks the cross-review added: P6b, P9, P11 — §9).

**Owner decisions answered 2026-09-24:** D11f.1 = two releases; D11f.2 = keep `role_id`, super-admin-only; D11f.3 = home = earliest membership. **Later the same day:** D11f.7 = profile role with no workspace in hand = earliest **live** membership; D11f.8 = AdminSeeder refuses (log + skip) to promote an identity that has any membership; follow-up **F1 is IN SCOPE for Part A** — every membership role write refuses the super-admin role and any role owned by another workspace (403 / validation error), with tests. D11f.4–D11f.7: the recommended defaults below apply.
→ owner: Moamen, date: 2026-09-24 (recorded above).

**Status: written 2026-09-24; cross-reviewed 2026-09-24 (Gemini 3.8 Flash + Opus, `docs/db/reviews/REVIEW-DB11F-2026-09-24.md`), every finding verified and adjudicated in §12. Part A implemented and deployed 2026-09-24 05:05 UTC
(§14; second review round adjudicated in §13). Part B implemented 2026-09-24 (code, migrations and tests on `feat/db-11f-part-b`, merged with the deployed Part A main; code review adjudicated in §15) — `dotnet build` and
`dotnet test Tests` green, migrations verified up/down/negative-case on a throwaway `postgres:15` (§15) — but NOT YET DEPLOYED: the mandatory ≥ 24 h Part A production soak (§9 step 5) has not elapsed (Part A deployed
2026-09-24 05:05 UTC; Part B may not merge/deploy before 2026-09-25 05:05 UTC, and only after the post-window P1/P11 re-checks — code-review BLOCKER, §15).**
Re-verified against `pointer-api` @ `0d5f75a` (81 migrations, newest `20260923220301_AddWorkspacesPauseAndDeletionState`). Between the first
verification base `8f97871` and `0d5f75a` the only production-code change is `Application/Common/Email/EmailLayout.cs` (unrelated), so every
Application/Infrastructure/API/Domain line number below is unchanged. Test and CI line numbers were re-anchored at `0d5f75a`.
`pointer-dashboard` @ `c463777` (`react/package.json:13` `"@moamen-ui/pointer-react": "^1.0.50"`).

## 0. Owner decisions (defaults apply unless the owner answers before Part A is merged)

| # | Question | Recommended default (encoded in this doc) | Alternative |
|---|---|---|---|
| **D11f.1** | One release or two? | **Two** (Part A code-only, then Part B contract, at least 24 h apart; §9). The old columns still have ~40 readers, so a single-release drop would make a code rollback lose data. With two releases, rolling back Part B is lossless in behaviour because Part A code reads none of the columns (§8). | One release (drop + code switch together). Not recommended. |
| **D11f.2** | What becomes of `users.role_id`? It is **not** legacy: it is how every super-admin check works today (`identity.Role?.IsSuperAdmin`, ~30 sites, §2). | **Keep the column, make it nullable, and set it NULL for every non-super-admin** (Part B). It becomes the *platform role*: non-null only for super admins, pointing at the global super-admin role. No super-admin code path changes. This also removes a latent FK bug (§2 "Latent bug"). | (B) Replace it with `users.is_super_admin boolean` and drop `role_id` + `FK_users_roles_role_id` + `IX_users_role_id`. That means ~30 read sites, JWT/`/me`/profile sourcing the super role from `roles`, and ~200 test edits. It would be its own doc (DB-11g). |
| **D11f.3** *(owner-answered — kept)* | What is the "home workspace"? It drives the `isHome` badge (dashboard `Shell.tsx:316`, `LoginPage.tsx:146`; widget `templates.ts:71`) and the workspace named in the password-reset / password-changed / e-mail-change mails. | **The workspace of the identity's earliest membership row of any state** (`ORDER BY joined_at, id`). Pre-check **P3** proves this equals `users.owner_id` for every production row. No client change. The cross-review proposed "prefer a live membership" (Gemini LOW, §12 G14). **Rejected for home**: home never picks the session workspace (the picker lists only live candidates, and `IsHome` is only a flag on them), and "any state" is exactly today's `owner_id` semantics. A live-first rule is a possible later owner change, not part of this doc. | (b) A new `workspace_memberships.is_home` column plus backfill: exact, but more moving parts and re-home logic. (c) Drop `IsHome`: a client contract change. |
| **D11f.4** | What happens to an identity with **no membership row anywhere** (not a super admin, not a merged row) whose legacy `owner_id` points at the workspace being deleted? | **P1** must print 0 rows before Part A ships (blocking). Part A also makes the shape impossible to create going forward: each of the 8 identity-creating sites saves the identity and its first membership in **one** `SaveChanges` (task A15; cross-review Opus). If such a row still exists, Part A's `HardDeleteAsync` **refuses with `Result.Failure` before any side effect** (read-only pre-flight, §3.2), and an in-transaction throw remains as a backstop. Either way nothing is deleted and the failure is loud. Part B: the identity references no workspace, so it simply survives. | Delete it on sight. This cannot be expressed without `owner_id`. |
| **D11f.5** | Drop the super-admin `Pending`/`Rejected` login branches that read `users.approval_status`? | **Yes.** `AdminSeeder` forces the super admin to `Approved` on every boot (`AdminSeeder.cs:150,160`), and **P4** proves it. | Keep them: they would need a new column. |
| **D11f.6** | Refuse API-key login with a **null-owner key held by a non-super-admin** (a pre-DB-11a leftover)? Today it signs in with no workspace and the *legacy* identity role. | **Yes, refuse it** (`InvalidApiKey`, audit reason `invalid_credentials`). **P5** must print 0 rows. | Keep today's behaviour. |
| **D11f.7** *(default refined by the cross-review; not an owner-answered decision; owner may confirm)* | Profile page `roleName` (`GET /api/me/profile`, `GET /api/admin/users/{id}/profile`) today shows `users.role_id`, the role at **creation**, even after a role change or in another workspace. | **Show the current role**: super admin → platform role; caller has a workspace → that workspace's **live** membership role; no workspace in hand → the earliest **live** membership's role, else the earliest of any state (Gemini MEDIUM, §12 G4). This is a bug fix. It also removes the `Include(u => u.Role)` that today 404s the profile of a member whose creation role belongs to another workspace (Opus MEDIUM, §12 O6). | Keep the stale creation-time role. |
| **D11f.8** *(new, cross-review Opus LOW)* | `AdminSeeder` promotes whichever identity matches `ADMIN__EMAIL`, including a workspace member. After DB-11f, super admins must hold no memberships, because the delete rule excludes them. | **Refuse the promotion** (the seeder's reconcile step logs `super-admin reconcile skipped: … (DB-11f)` and boot continues; task A17). | Keep promoting (then the promoted member's workspace deletion is refused by the I1 pre-flight). |

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

## 2. Prerequisites (verified facts, 2026-09-24 @ `8f97871`, re-verified @ `0d5f75a`)

**How the inventory was made (repeatable, used again as acceptance in §7).** A scratch copy from `git archive HEAD` had
`[System.Obsolete("DB11F_<X>")]` added to `User.OwnerId`, `User.RoleId`, `User.Role`, `User.ApprovalStatus` and `Role.Users`, then was built with
`dotnet build Pointer.sln --no-incremental`. Every CS0618 warning is one use of a legacy member **on `User`**. This distinguishes them from
`WorkspaceMembership.OwnerId/RoleId/ApprovalStatus`, `Invite.RoleId`, etc. First count (at `8f97871`): 541 sites. The corrected count is in the note below.
The script is in §7 criterion 2. *(Re-run at `0d5f75a` with the corrected script, §12 O7, with `Role.Users` also marked: **543 occurrences, 73 outside `Tests/`** (72 on `User` + 1 `Role.Users`, counted per line+column: `AdminSeeder.cs:158` and `:160` carry two each), **470 in 50 test files**.)*

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
| `Infrastructure/AppDbContext.cs:108` | OwnerId | **read** (User filter null-tenant branch; dead in prod: `docker-compose.prod.yml:32` `Tenancy__StrictNullTenantIsolation: "true"`) | platform role — shipped as `Set<Role>().Any(r => r.Id == e.RoleId && r.IsSuperAdmin)`, not the originally-written `e.Role != null && e.Role.IsSuperAdmin` (§3.3; not `!e.Memberships.Any()`, §12 O1; Part A review amendment, §13) | A |
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
| `API/Seed/AdminSeeder.cs:148,150,158,160` | RoleId, ApprovalStatus | write at `:148,:150`; **read-then-write** reconcile at `:158,:160` (`if (user.X != …) user.X = …`) — super admin only | `:150,:160` removed in B; `:148,:158` stay; D11f.8 guard added in A (task A17) | A (guard) / B |
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
- Every identity is created **in the same request** as its first membership, but **not atomically** (cross-review Opus MEDIUM, verified). All 8 creator sites
  save the identity in one `SaveChangesAsync` and the membership in a later one, often with e-mail or notification work in between. A crash in that window leaves
  a membership-less identity, which is I1's forbidden shape:
  `TenantService.cs:236-237` → `:248`; `UserService.cs:175-176` → `SendAsync :179` → `:190`; `InviteService.cs:775-776` → notify/`SendAsync :788-792` → `:803`;
  `InviteService.cs:924,941` → notify/`SendAsync :956-960` → `:970`; `InviteService.cs:1162-1164` → notify `:1170` → `:1180` (needs `invite.Id`);
  `AuthService.cs:1363-1364` → `:1374`; `AuthService.cs:1498-1499` → `SendAsync :1502` → `:1513`; `DemoService.cs:172,183` → comments `:238` → `:249`.
  Part A task A15 makes each one a single save. The DB-11a backfill created memberships for **every** workspace-scoped `users` row, live or soft-deleted (`DB-11a…md:308-332`).
- Merged rows (`merged_into_user_id`) come only from the one-time DB-11a Migration 2. No runtime writer exists (`grep -rn MergedIntoUserId Application API Infrastructure` → readers
  `AuthService.cs:723`, `IdentityEraseService.cs:349` only). The production census found **0 merges, 0 aliases** (`DB-REVIEW-2026-09-22.md:137`).
- The super admin has `owner_id` NULL and no membership (`DB-11a…md:561`; seeder `AdminSeeder.cs:132-165` writes no `OwnerId`).
- Membership `Role` is loaded at every `ITokenService.Issue` call site with a membership. Via `.Include(m => m.Role)` (`MembershipService.cs:44,64`) for Issue at `AuthService.cs:927`, `:1069`, `:1268`, `:1687` and `DemoService.cs:505`.
  Via explicit assignment before Issue at `DemoService.cs:293` (Issue `:294`), `InviteService.cs:805` (Issue `:814`) and `:1019` (Issue `:1020`). `AuthService.cs:1001` passes `null` for the super-admin MFA path.
- **`Include(u => u.Role)` gates visibility where the query is filtered** (cross-review Opus MEDIUM, verified). `User.Role` is a required navigation in Part A, so `Include` is an
  INNER JOIN against the **filtered** `Role` set (`AppDbContext.cs:196-204`: own-workspace + global roles only). Three queries run under the filters: `ProfileService.cs:28-33` (`GetByIdAsync`),
  `:41-46` (`GetByPublicIdAsync`) and `PreferencesService.cs:38-41`. A member whose creation role (`users.role_id`) is owned by workspace W, viewed or acting from workspace X, is dropped by the join,
  giving 404 for the profile and 404 for a preferences update. Pre-existing; fixed in Part A (tasks A7/A8). Every other `Include(u => u.Role)` uses `IgnoreQueryFilters()`
  (`AuthService.cs:212-214`, `DemoService.cs:369-370`, `ImpersonationService.cs:63-64`, `MfaService.cs:68-69`, `MembershipService.cs:26-27,35-36`, `ApiKeyService.cs:76-78`).
- **The User filter's null-tenant branch cannot use memberships** (cross-review Opus HIGH, verified). The `WorkspaceMembership` filter (`AppDbContext.cs:335-339`) is
  `IsSuperAdmin || (TenantId != null && OwnerId == TenantId)`, and EF applies it to the `Memberships` navigation inside the `User` filter. For a null-tenant non-super caller every
  membership is hidden, so `!e.Memberships.Any()` would be true for **every** identity.
- **Role assignment is not workspace-bounded for an operator** (cross-review Opus MEDIUM, verified). `UserService.GetActiveRoleAsync` (`UserService.cs:910-915`) reads roles
  under the `Role` filter, which a super admin bypasses (`AppDbContext.cs:198`). The escalation guard only restricts non-super callers (`:267-272`). So a super admin can give a membership in X
  (a) a role **owned by W**, whose `workspace_memberships.role_id` Restrict FK then makes W's hard delete fail at `DeleteOwnedAsync<Role>` (`TenantService.cs:721`) with 23503, or
  (b) the **`is_super_admin` role**, which a naive copy into `users.role_id` would turn into a platform super admin. Blocking pre-checks P6b and P11 (§9) and the non-escalating re-point (§3.2)
  handle it inside DB-11f. The root guard at role assignment is follow-up F1 (§10).
- **Both hosted deletion loops catch every exception** (`DemoCleanupService.cs:194` `catch (Exception)`, `WorkspaceDeletionService.cs:179` after the dedicated `DeletionPreconditionChangedException`
  catch at `:166`). They log and retry on the next sweep. `WorkspaceDeletionService` logs a `Result.Failure` at Information as "skipped (cancelled)" (`:158-163`), so an I1 failure must be grepped by its message (§9).
- Seeded global roles (`AdminSeeder.cs:16-25`): `Admin` (super), `Workspace Admin`, `Workspace Admin Deputy` (admin-tier), `Developer`, `PM`, `Tester`, `Client` (quick-access). The **least-privilege
  global role** used by the Part A re-point and by B-2's `Down()` is the lowest-id global role with none of `is_super_admin`/`grants_admin`/`quick_access` (= `Developer` on a seeded database; P9 proves one exists).
- `IUnitOfWork.UserAliases` exists (`Application/Abstractions/IUnitOfWork.cs:13`). `UserAlias.SourceWorkspaceId` holds the merged row's workspace (`DB-11a…md:303-304`).
- The only `IMembershipService` implementations are `MembershipService.cs:13` and the test wrapper `Tests/Db17DemoServiceTests.cs:836`.

**Latent bug found by this inventory (the `users.role_id` half is fixed in Part A; the membership half is guarded by P6b and follow-up F1).** An invite may pin a **tenant-owned** role (`RoleService.cs:55` stamps `OwnerId = TenantStamp.OwnerFor(...)`),
and `NewIdentity` copies it into `users.role_id` (`InviteService.cs:760-766`). If that identity also belongs to X and its creation workspace W is hard-deleted,
the re-home at `TenantService.cs:716` moves `owner_id` but leaves `role_id` pointing at W's role. `DeleteOwnedAsync<Role>(x => x.OwnerId == workspaceId)`
(`:721`) then violates `FK_users_roles_role_id` (Restrict) with 23503, and the whole deletion fails. This is the same class as the DB-18 fix. Pre-check **P6** counts rows exposed to it.

**Tests that pin today's behaviour and are touched** (all found by the scan; exact edits in §6)
`Tests/WorkspaceTests.cs:168` (Sqlite `TestDb`, real FKs), `:382` (`Assert.Equal(23, HardDeleteOrder.Length)`), `:505-522` (reflects `GetProperty("OwnerId")!` on every
`HardDeleteOrder` type, which would null-ref once `User` has no `OwnerId`), `:530-618` (5b, DB-18 Gemini BLOCKER, seeds an erased identity **with no membership**);
`Tests/Db18WorkspaceLifecycleTests.cs:2118-2190` (preview = delete parity); `Tests/WorkspaceMembershipTests.cs:527-620,786-794` (multi-workspace survivor re-homed);
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
  Part A task A15 makes I1 true **by construction** for new identities (identity + first membership in one `SaveChanges`). Until then it held only by convention (§2).
- **What each pre-check proves** (cross-review Gemini HIGH, §12 G2): **P1** = I1 (no membership-less legacy pointer). **P2** = delete-set parity, old rule vs new, per workspace, on production data (the only parity proof).
  **P3** = I2 (home = legacy owner). **P4** = super-admin shape. **P5** = no stray non-super null-owner keys. **P6/P6b** = role-FK exposure. **P9** = least-privilege fallback role exists. **P11** = no membership holds the platform role.
  A P2 `new_only` row whose user has `merged_into_user_id IS NOT NULL` would be a merged tombstone the old rule missed (it would have raised 23503 on `fk_users_merged_into_user`). That is an intended fix, not a parity failure. Paste it and explain. P7 (0 merged rows) makes this moot in production.
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
// (fk_users_workspaces_owner_id, FK_users_roles_role_id) from blocking the deletes below. A surviving
// identity that points here is re-pointed: owner_id → its earliest membership elsewhere; role_id →
// the least-privilege global role (never an admin-tier or platform role, so the re-point can never
// grant anything — cross-review Opus MEDIUM). The read-only pre-flight at the top of this method has
// already refused every case the backstop throws below can hit.
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
var fallbackRoleId = legacyPointers.Any(u => workspaceRoleIds.Contains(u.RoleId))
    ? await LeastPrivilegeGlobalRoleIdAsync()
    : null;
foreach (var u in legacyPointers)
{
    if (u.OwnerId == workspaceId)
    {
        var otherOwner = await _unitOfWork
            .Repository<WorkspaceMembership>()
            .Query()
            .IgnoreQueryFilters()
            .Where(m => m.UserId == u.Id && m.OwnerId != workspaceId)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Guid?)m.OwnerId)
            .FirstOrDefaultAsync();
        if (otherOwner is null)
            // Backstop only (invariant I1, D11f.4) — the pre-flight refuses this before any side effect.
            throw new InvalidOperationException(
                $"DB-11f invariant I1 broken: user {u.Id} references workspace {workspaceId} but has no membership in any other workspace and is not in the delete set."
            );
        u.OwnerId = otherOwner;
    }
    if (workspaceRoleIds.Contains(u.RoleId))
    {
        if (fallbackRoleId is not int fallback)
            // Backstop only — the pre-flight refuses this before any side effect (P9).
            throw new InvalidOperationException(
                "DB-11f: no global least-privilege role to re-point a surviving identity's legacy role_id."
            );
        u.RoleId = fallback;
    }
    _unitOfWork.Repository<User>().Update(u);
}
```
Everything after (`DeleteOwnedAsync<Role>`, `AppEnvironment`, the workspace row, `SaveChangesAsync`, audit, files) is unchanged.

**Read-only pre-flight (Part A; cross-review Opus MEDIUM, §12 O2).** For an ordinary (operator) reason, `HardDeleteAsync` writes the `tenant.hard_deleted` audit row and deletes the owner's
files **before** the transaction (`TenantService.cs:608-620`). A failure discovered inside the transaction would then leave a live workspace with no screenshots and an audit row claiming
it was deleted. So insert, directly after the `if (isGuardedReason) { var stillDue … }` block (`:572-581`) and before `var commentCount` (`:585`):
```csharp
// DB-11f PART A ONLY (deleted by Part B task B6): read-only invariant pre-flight BEFORE any side
// effect — the audit row and the file delete below run before the transaction for an ordinary
// reason (cross-review Opus MEDIUM). Same predicates as the in-transaction backstop.
var preflightDeleteSet = IdentitiesDeletedWithWorkspace(_unitOfWork, workspaceId).Select(u => u.Id);
var preflightRoleIds = _unitOfWork
    .Repository<Role>()
    .Query()
    .IgnoreQueryFilters()
    .Where(r => r.OwnerId == workspaceId)
    .Select(r => r.Id);
var membershipsElsewhere = _unitOfWork
    .Repository<WorkspaceMembership>()
    .Query()
    .IgnoreQueryFilters()
    .Where(m => m.OwnerId != workspaceId);
var i1Broken = await _unitOfWork
    .Repository<User>()
    .Query()
    .IgnoreQueryFilters()
    .Where(u =>
        (u.OwnerId == workspaceId || preflightRoleIds.Contains(u.RoleId))
        && !preflightDeleteSet.Contains(u.Id)
        && !membershipsElsewhere.Any(m => m.UserId == u.Id)
    )
    .Select(u => u.Id)
    .ToListAsync();
if (i1Broken.Count > 0)
    return Result.Failure(
        $"DB-11f invariant I1 broken: user(s) {string.Join(",", i1Broken)} reference workspace {workspaceId} but belong to no other workspace; nothing was deleted."
    );
var needsRoleRepoint = await _unitOfWork
    .Repository<User>()
    .Query()
    .IgnoreQueryFilters()
    .AnyAsync(u => !preflightDeleteSet.Contains(u.Id) && preflightRoleIds.Contains(u.RoleId));
if (needsRoleRepoint && await LeastPrivilegeGlobalRoleIdAsync() is null)
    return Result.Failure(
        "DB-11f: no global least-privilege role to re-point a surviving identity's legacy role_id; nothing was deleted."
    );
```
and add this private helper next to `DeleteOwnedAsync` (`:799`):
```csharp
// DB-11f PART A ONLY (deleted by Part B task B6). The role a surviving identity's legacy
// users.role_id is re-pointed to: global, live, and none of super/admin/quick-access — never grants
// anything (cross-review Opus MEDIUM/LOW; same predicate as ClearUsersRoleIdForMembers.Down and P9).
private Task<int?> LeastPrivilegeGlobalRoleIdAsync() =>
    _unitOfWork
        .Repository<Role>()
        .Query()
        .IgnoreQueryFilters()
        .Where(r =>
            r.OwnerId == null
            && r.DeletedAt == null
            && !r.IsSuperAdmin
            && !r.GrantsAdmin
            && !r.QuickAccess
        )
        .OrderBy(r => r.Id)
        .Select(r => (int?)r.Id)
        .FirstOrDefaultAsync();
```
The pre-flight returns `Result.Failure`, so nothing is audited, no file is touched and no row changes. `TenantsController.Delete` surfaces the message to the operator.
`DemoCleanupService` logs it as a Warning (`:186-190`). `WorkspaceDeletionService` logs it at Information as "skipped (cancelled): <message>" (`:158-163`) and retries every sweep.
So §9 greps every level for `DB-11f invariant I1`. The in-transaction throws stay as backstops (a row changed between pre-flight and transaction). Both loops catch them as generic exceptions and retry (§2).

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
| `PreferencesService.cs:39`, `:58` / `:63` | delete `.Include(u => u.Role)` (INNER-JOIN visibility gate, §2); `:58` → `var platformRole = await _memberships.PlatformRoleAsync(user.Id); Role? role = platformRole;`; `:63` → `role = membership is not null ? membership.Role : platformRole;` |
| `ProfileService.cs:32`, `:45`, `:249` | delete both `.Include(u => u.Role)` (visibility gate, §2); D11f.7 role name (task A8) |
| `JwtTokenService.cs:78-81` | `var role = UserMapper.SessionRole(u, membership); var roleId = membership?.RoleId ?? role?.Id ?? 0;` (comment in task A9) |
| `AppDbContext.cs:108` | **Shipped form (Part A review, 2026-09-24):** `\|\| (currentUser.TenantId == null && !strict && Set<Role>().Any(r => r.Id == e.RoleId && r.IsSuperAdmin))` — the platform role, i.e. exactly today's `owner_id IS NULL` set (P4). **Not** `!e.Memberships.Any()` (cross-review Opus HIGH: the membership filter hides every membership from a null-tenant caller, so every identity would match). The doc's originally-written literal form, `e.Role != null && e.Role.IsSuperAdmin` (navigating the REQUIRED `User.Role` reference alongside the `Memberships.Any` disjunct in the same filter), silently returns zero rows under this project's EF Core/InMemory combination — confirmed by a throwaway repro comparing the compiled C# predicate (true) against the executed query (empty). The EXISTS-style subquery above is logically equivalent and was verified identical to the navigation form on real Postgres 15, for 8 caller types (super admin; tenant member, strict and non-strict; null-tenant super admin; null-tenant non-super admin, strict and non-strict; a caller with an ended-only membership; a caller with no membership at all) × strict/non-strict — see §13. Comment in task A10 |
| `DemoService.cs:292` `demoUser.Role = role;` | **deleted in Part A** (Gemini MEDIUM, §12 G5): after `SessionRole` the token takes the membership's role, so nothing reads the identity navigation. In Part B it would also fix up `RoleId` (§3.5) |
| 8 identity-creating sites (§2) | identity + first membership in **one** `SaveChangesAsync`; side effects after it (task A15; Opus MEDIUM) |
| `AdminSeeder.cs:156` (reconcile `else`) | refuse to promote an identity that holds a membership (D11f.8, task A17) |

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
`// DB-RULES: R2 contract approved 2026-09-24 by Moamen (owner, verbatim: "Approved to drop users.owner_id, users.approval_status, ux_users_email_owner_live, IX_users_owner_id, fk_users_workspaces_owner_id and make users.role_id super-admin-only (DB-11f Part B), 2026-09-24."; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)`

**Migration B-2 `ClearUsersRoleIdForMembers`** (scaffold after B-1 with no model change → empty `Up`/`Down`, snapshot unchanged). Hand-written body, nothing else:
```csharp
[ContractMigration("DB-11f")]
public partial class ClearUsersRoleIdForMembers : Migration
{
    // DB-RULES: R2 contract approved 2026-09-24 by Moamen (owner, verbatim: "Approved to drop users.owner_id, users.approval_status, ux_users_email_owner_live, IX_users_owner_id, fk_users_workspaces_owner_id and make users.role_id super-admin-only (DB-11f Part B), 2026-09-24."; docs/db/execution/DB-11f-drop-legacy-users-tenancy-columns.md)
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
        // Not the original values (legacy copies of the creation role; only the pre-db11f dump holds
        // them). Refills every NULL so B-1's Down() can restore NOT NULL, with the least-privilege
        // global role ONLY — never a membership's role, which may be the platform role or another
        // workspace's (cross-review Opus MEDIUM/LOW: privilege escalation). Nothing in the Part A code
        // this rolls back to reads users.role_id for a non-super-admin. P9 proves the role exists.
        migrationBuilder.Sql(
            "UPDATE users SET role_id = (SELECT r.id FROM roles r WHERE r.owner_id IS NULL "
                + "AND r.deleted_at IS NULL AND NOT r.is_super_admin AND NOT r.grants_admin "
                + "AND NOT r.quick_access ORDER BY r.id LIMIT 1) WHERE role_id IS NULL;"
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
| `DemoService.cs:292` `demoUser.Role = role;` | already deleted by Part A (task A16). Verify it is absent. With `RoleId` nullable, that assignment would make EF fix up `RoleId = role.Id`, and the `AuditWriter`'s later `SaveChangesAsync` would persist a member platform role |
| `AdminSeeder.cs:150`, `:160` | delete the two `ApprovalStatus` lines. `:148`, `:158` (`RoleId = adminRoleId`) stay: that is the platform role |
| `TenantService.cs` | delete **all three** Part A-only pieces: the read-only pre-flight, the maintenance block (from `// DB-11f PART A ONLY — legacy pointer maintenance` through its `foreach`), and the `LeastPrivilegeGlobalRoleIdAsync` helper. `HardDeleteOrder` loses `typeof(User)` (22 entries; §5 B6) |
| `JwtTokenService.cs:172` | `new Claim("role_id", (operatorUser.RoleId ?? 0).ToString()),` |
| `.github/workflows/db-migrations.yml:63-64` | in **both** `INSERT INTO users (…)`: remove `approval_status, ` from the column list and the matching `1, ` from `VALUES` (the value after `true, `). `role_id`/`$rid` stay. **Same commit as migration B-1** (Gemini MEDIUM): the CI job applies B-1 and then runs this probe, so a split commit fails CI with 42703 |

### 3.6 Audit

No new action and no new endpoint. The D11f.6 refusal writes `apikey.login_failed` via the existing `AuditApiKeyLoginFailedAsync(apiKey, "invalid_credentials")`, a reason
string already used at `AuthService.cs:1170,1179,1187`. `Tests/AuditCoverageTests.cs` is unaffected. The `pending`/`rejected` reasons remain in use by the membership branches.

### 3.7 What happens to every existing row

- **Part A:** no row is written by the deploy. On a workspace hard delete, the delete set is identical to today's (P2), and surviving identities keep being re-pointed, now both columns.
  The `users.role_id` half of the latent tenant-role 23503 disappears. The `workspace_memberships.role_id` half (a membership elsewhere holding a role owned by the deleted workspace, §2) is **not** fixed here:
  P6b must print 0 before Part A ships, and follow-up F1 closes the assignment path. `isHome` and the mail workspace names are identical where I2 holds (P3). Profile and preferences stop
  404-ing for members whose creation role belongs to another workspace. New identities are created atomically with their first membership.
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
  and (cross-review Opus MEDIUM, §12 O6):
  `/// <summary>DB-11f. The identity's platform role — the is_super_admin role its users.role_id points at — loaded without query filters (never an INNER JOIN visibility gate). Null for every non-super-admin.</summary>`
  `Task<Role?> PlatformRoleAsync(int userId);`
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
  Directly after it:
  ```csharp
  /// <inheritdoc />
  public Task<Role?> PlatformRoleAsync(int userId) =>
      unitOfWork
          .Repository<Role>()
          .Query()
          .IgnoreQueryFilters()
          .AsNoTracking()
          .Where(r =>
              r.IsSuperAdmin
              && unitOfWork
                  .Repository<User>()
                  .Query()
                  .IgnoreQueryFilters()
                  .Any(u => u.Id == userId && u.RoleId == r.Id)
          )
          .FirstOrDefaultAsync();
  ```
  `Tests/Db17DemoServiceTests.cs` (the wrapper class at `:836`): add `public Task<Guid?> HomeWorkspaceIdAsync(int userId) => inner.HomeWorkspaceIdAsync(userId);` and
  `public Task<Role?> PlatformRoleAsync(int userId) => inner.PlatformRoleAsync(userId);`.
- **A3.** `Application/Services/Implementation/TenantService.cs`: §3.2 verbatim. That is four pieces: the new `IdentitiesDeletedWithWorkspace`, the replacement for `:669-719`, the read-only pre-flight after `:581`, and the `LeastPrivilegeGlobalRoleIdAsync` helper. `using Pointer.Domain.Entity;` already covers `Role` (used at `:721`).
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
- **A7.** `Application/Services/Implementation/PreferencesService.cs`: delete the line `.Include(u => u.Role)` (`:39`). Replace `:58` `var role = user.Role;` with
  `var platformRole = await _memberships.PlatformRoleAsync(user.Id);` / `Role? role = platformRole;`, and `:63` with `role = membership is not null ? membership.Role : platformRole;`
  (the `SessionRole` rule, without needing the identity navigation loaded).
- **A8.** `Application/Services/Implementation/ProfileService.cs`: delete the line `.Include(u => u.Role)` in `GetByIdAsync` (`:32`) and in `GetByPublicIdAsync` (`:45`)
  (INNER-JOIN visibility gate, §2). In `BuildAsync` (`:98`), directly before the final `return` (`:241`), insert:
  ```csharp
  // DB-11f D11f.7 (+ cross-review): the CURRENT role, never users.role_id for a member —
  // the platform role for a super admin; else the caller-workspace's LIVE membership; with no
  // workspace in hand, the earliest LIVE membership, else the earliest of any state.
  var roleName = await _unitOfWork
      .Repository<Role>()
      .Query()
      .IgnoreQueryFilters()
      .Where(r => r.IsSuperAdmin && r.Id == user.RoleId)
      .Select(r => r.Name)
      .FirstOrDefaultAsync();
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
          : await ms.OrderBy(m => m.LeftAt != null || m.DeletedAt != null)
              .ThenBy(m => m.JoinedAt)
              .ThenBy(m => m.Id)
              .Select(m => m.Role.Name)
              .FirstOrDefaultAsync();
  }
  ```
  and `:249` → `RoleName = roleName ?? string.Empty,`. No new `using` is needed (`Microsoft.EntityFrameworkCore`, `Pointer.Domain.Entity` are present, `:2,8`).
- **A9.** `Infrastructure/Auth/JwtTokenService.cs`: add `using Pointer.Application.Common;`. Replace `:78-81` with
  `// DB-11f: UserMapper.SessionRole — the membership's role, or the identity's own role ONLY for a super admin.` /
  `var role = UserMapper.SessionRole(u, membership);` / `var roleId = membership?.RoleId ?? role?.Id ?? 0;`.
- **A10.** `Infrastructure/AppDbContext.cs:108` per §3.3. **Shipped as the EXISTS-style subquery**
  `Set<Role>().Any(r => r.Id == e.RoleId && r.IsSuperAdmin)`, not this doc's originally-written
  literal navigation form `e.Role != null && e.Role.IsSuperAdmin` — that form, combined with the
  `Memberships.Any` disjunct in the same filter, silently returns zero rows under this project's EF
  Core/InMemory combination (confirmed by a throwaway repro: compiled C# predicate true, executed
  query empty), but was verified identical to the subquery form on real Postgres 15 for 8 caller
  types × strict/non-strict (Part A review, 2026-09-24 — §13). Append one sentence to the comment
  `:97-100` (deliberately avoiding the literal text `e.Memberships.Any()`, which a later acceptance
  grep checks is absent from this file):
  `DB-11f: the null-tenant (non-strict) bucket for identities is the platform role (super admins) — exactly today's users.owner_id IS NULL set. It is deliberately NOT an emptiness check on the Memberships collection: the WorkspaceMembership filter hides every membership from a null-tenant caller, so that form would match every identity (cross-review Opus HIGH).`
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
- **A15.** **Identity + first membership in ONE `SaveChangesAsync`** at all 8 creator sites (cross-review Opus MEDIUM, §12 O3). `JoinAsync` sets the `User` navigation
  (`MembershipService.cs:103`), so EF inserts the identity first and fixes up `UserId` inside the same save. No save is added. Each change only deletes or moves one. Exactly:
  1. `TenantService.cs` `CreateAsync`: delete `await _unitOfWork.SaveChangesAsync();` at `:237` (inside `if (isNewIdentity)`, right after `AddAsync(identity)`). The save at `:248` now inserts both.
  2. `UserService.cs` `CreateAsync`: delete the save at `:176`. Move the comment `:178` and `await _emailVerification.SendAsync(identity);` (`:179`) to directly after the save at `:190`,
     wrapped as `if (isNewIdentity) { … SendAsync(identity!); }` (`isNewIdentity` is declared at `:165`).
  3. `InviteService.cs` join-existing accept: replace the `try { AddAsync(identity); SaveChangesAsync(); } catch (DbUpdateException) { return … Conflict(AccountExists); }` block (`:773-783`)
     with the single line `await _unitOfWork.Repository<User>().AddAsync(identity);`. Cut the two notification statements with their comments (`:785-792`). Replace the save at `:803` with
     `try { await _unitOfWork.SaveChangesAsync(); } catch (Microsoft.EntityFrameworkCore.DbUpdateException) when (isNewIdentity) { return Result<LoginResponse>.Conflict(MessageKeys.Auth.AccountExists); }`
     (keep the L2 comment above the catch). After `membership.Role = role;` (`:805`) paste the two notification statements as
     `if (isNewIdentity && invite.Email != null) await NotifyDemoEmailVerifiedAsync(identity!);` and `if (isNewIdentity && invite.Email == null) await _emailVerification.SendAsync(identity!);`.
  4. `InviteService.cs` new-workspace accept: declare `WorkspaceMembership membership;` directly before `try` (`:907`). Inside the `try`, between `AddAsync(newWorkspace)` (`:940`) and the save (`:941`), insert
     `membership = await _memberships.JoinAsync(identity!, workspaceId, workspaceAdminRole, ApprovalStatus.Approved, isActive: true, inviteId: invite.Id);`.
     Delete the old `var membership = await _memberships.JoinAsync(…);` statement (`:962-969`) and the save after it (`:970`). The notification lines `:948-960` stay where they are (now after the combined save).
  5. `InviteService.cs` quick-access provisioning (`try` at `:1159`): reorder the body's start to `await _unitOfWork.Repository<Invite>().AddAsync(invite);` / `await _unitOfWork.SaveChangesAsync();` (the invite gets its id; an invite row without an identity is harmless history) /
     `if (isNewIdentity) await _unitOfWork.Repository<User>().AddAsync(identity!);` / the existing `membership = await _memberships.JoinAsync(… inviteId: invite.Id);` / `await _unitOfWork.SaveChangesAsync();` (identity + membership) /
     then the moved `if (isNewIdentity) await NotifyDemoEmailVerifiedAsync(identity!);` with its comment. Everything after (`QuickAccessLink` …) and the `catch` (`:1200`) are unchanged.
  6. `AuthService.cs` `RegisterAsync`: delete the save at `:1364`.
  7. `AuthService.cs` `RegisterAdminAsync`: add `var isNewIdentity = identity == null;` directly before `if (identity == null)` (`:1486`). Delete the save at `:1499`. Move the comment `:1501` and `SendAsync` (`:1502`)
     to directly after the save at `:1513`, wrapped `if (isNewIdentity) { … SendAsync(identity); }`.
  8. `DemoService.cs` `ProvisionAsync`: move the comment `:240` and the `var demoMembership = await _memberships.JoinAsync(…);` statement (`:241-248`) to directly after `AddAsync(project)` (`:182`), before the save at `:183`. Delete the save at `:249`.
  After this task: `grep -n "AddAsync(identity\|AddAsync(demoUser\|JoinAsync(\|SaveChangesAsync()" <each file>` shows, for every creator site, the `JoinAsync` **between** the identity `AddAsync` and the first `SaveChangesAsync` after it. Paste the output into the PR.
- **A16.** `DemoService.cs:292`: delete `demoUser.Role = role;` (Gemini MEDIUM). `:293` `demoMembership.Role = role;` stays.
- **A17.** `API/Seed/AdminSeeder.cs` (D11f.8): as the first statement of the reconcile `else {` (`:157`), insert
  ```csharp
  // DB-11f (cross-review Opus LOW): never promote a workspace member to super admin — super admins
  // hold no memberships (P4) and the membership-based workspace delete rule excludes them. Throwing
  // lands in the catch below: this reconcile step is skipped and logged; boot continues.
  if (
      user.RoleId != adminRoleId
      && await db.WorkspaceMemberships.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id)
  )
      throw new InvalidOperationException(
          $"ADMIN__EMAIL matches workspace member identity {user.Id}; not promoted to super admin (DB-11f). Use an address that belongs to no workspace."
      );
  ```
  Do **not** `return`: the plan seeding after the catch (`:180`) must still run.
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
- **B4.** `DemoService.cs` per §3.5 (`:151-156`; confirm `:292`'s `demoUser.Role = role;` is gone since A16).
- **B5.** `API/Seed/AdminSeeder.cs:150`, `:160` deleted.
- **B6.** `TenantService.cs`: delete the three Part A-only pieces (pre-flight, maintenance block, `LeastPrivilegeGlobalRoleIdAsync`; §3.5). In `HardDeleteOrder` delete `typeof(User),`. Replace its comment (`:808-811`) with
  `// The 22 owner-carrying types, in the same order as the DeleteOwnedAsync<T> calls above. User is not in it since DB-11f (it carries no owner_id): identities are removed by IdentitiesDeletedWithWorkspace, after memberships. Documentation + test input for WorkspaceTests.HardDeleteOrder_CoversEveryOwnerCarryingEntity. Never loop over this in production code.`
  In the comment above `DeleteOwnedAsync<WorkspaceMembership>` delete nothing. It stays accurate.
- **B7.** `JwtTokenService.cs:172` per §3.5.
- **B8.** `.github/workflows/db-migrations.yml:63-64` per §3.5, **in the same commit as B-1** (Gemini MEDIUM).
- **B9.** `dotnet build`. Permitted errors outside `Tests/`: none. Any error → stop and report. In `Tests/`, fix **only** errors of the form `'User' does not contain a definition for 'OwnerId'`
  / `'ApprovalStatus'` (CS0117 in `new User { … }` initializers: delete that one line; CS1061 in member access: apply §6 recipe T1/T2) and `int?`-conversion errors on `User.RoleId`.
  Never touch `OwnerId`/`ApprovalStatus`/`RoleId` inside `new WorkspaceMembership`, `new Invite`, `new ApiKey`, or any other type's initializer. The compiler names the type **and the line**:
  the line numbers in §6 item 20 are as of `0d5f75a`, so trust the compiler if they drifted.
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
3. `Tests/TenantQueryFilterTests.cs`: `:189` → `Assert.Equal("a@x", results[0].Email);`. In `NullTenant_NonSuper_DefaultFlag_SeesNullOwnerBucket` (`:200-257`, name kept), the bucket is now the platform role (§3.3, Opus HIGH).
   At the top of the seed block add `var superRole = new Role { Name = "SA", IsSuperAdmin = true, GrantsAdmin = true, IsActive = true };` / `var memberRole = new Role { Name = "M", IsActive = true };` /
   `seed.Roles.AddRange(superRole, memberRole); seed.SaveChanges();`. Set `RoleId = memberRole.Id` on `a@x`, and `RoleId = superRole.Id` on `n1@x` and `n2@x`. Keep a reference to `a@x`
   (`var a = new User { … }`), and after the users' `seed.SaveChanges();` add `TestSeed.Join(seed, a, tenantA, memberRole);`. Replace `:255-256` with
   `Assert.All(results, u => Assert.StartsWith("n", u.Email));` / `Assert.DoesNotContain(results, u => u.Email == "a@x");`. This test **must fail** with the rejected `!e.Memberships.Any()` form
   (it would return all three users). That is the regression proof for Opus HIGH. `NullTenant_NonSuper_StrictFlag_SeesNothing` stays unchanged.
4. Any other failure → recipe T0 or stop.

### Part A — add `Tests/Db11fMembershipRulesTests.cs` (namespace as siblings)
5. `DeleteSet_IsMembershipOnly_EveryShape` (InMemory `Ctx`). Workspaces W, X; a global super role (`IsSuperAdmin = true, OwnerId = null`) and a W role. Seed each identity with `OwnerId` exactly as production would:
   (a) owner W, live membership W → **in**; (b) owner W, live W + ended X → out; (c) owner X, live X + live W → out; (d) owner W, erased (`DeletedAt`, `ErasedAt`) with ended W membership → **in**;
   (e) owner W, soft-deleted with pending W membership → **in**; (f) super admin (super role, owner null, no membership) → out; (g) super admin with an anomalous live W membership → out;
   (h) merged row (`MergedIntoUserId = (a).Id`, `DeletedAt` set, no membership) → **in**; (i) merged row with alias `SourceWorkspaceId = W` whose canonical is (c) → **in**;
   (j) non-super, owner W, **no membership** → out. Assert `IdentitiesDeletedWithWorkspace(uow, W).Select(u => u.Email)` equals exactly {a, d, e, h, i}.
6. `DeleteSet_EqualsLegacyRule_WhenInvariantI1Holds`. For shapes (a)-(g) only, assert the new set equals `Users.IgnoreQueryFilters().Where(u => u.OwnerId == W && !WorkspaceMemberships.IgnoreQueryFilters().Any(m => m.UserId == u.Id && m.OwnerId != W))`
   (the pre-DB-11f predicate, inlined in the test; Part B deletes this test with a one-line PR note, because the column is gone).
7. `HardDelete_RehomedIdentityWithTenantRole_Succeeds_UnderRealForeignKeys` (Sqlite `TestDb` copied from `WorkspaceTests.cs:168`). Seed the global roles `Developer` (no flags) and a super role.
   The identity is created in W with a **W-owned** role (`users.role_id` = it, `OwnerId = W`), with memberships in W (that role) and X (the **super** role, to prove the re-point never copies a membership role).
   `HardDeleteAsync(W)` → success. The identity survives with `OwnerId == X` and `RoleId == Developer.Id` (**not** the super role; Opus privilege-escalation finding). The W role is gone.
   On `main` this test fails with a FK error (the §2 latent bug). Say so in the PR.
8. `HardDelete_MembershipLessIdentityReferencingWorkspace_RefusedBeforeAnySideEffect` (Sqlite; Opus MEDIUM). Shape (j) plus a normal admin, a project, and a spy `IFileStorage` and spy `IAuditWriter`
   (copy `RecordingFileStorage` from `Tests/ScreenshotPurgeTests.cs:78`, which records `DeleteOwnerFilesAsync` at `:114`, and `RecordingAuditWriter` from `Tests/Db17DemoServiceTests.cs:150`). Call `HardDeleteAsync(W, "admin")`, the ordinary operator reason whose audit and file delete run before the transaction.
   Assert: `IsSuccess == false`, `Message` contains `DB-11f invariant I1`, **no** `tenant.hard_deleted` audit entry, `DeleteOwnerFilesAsync` **not** called, and the workspace row, project and both users still exist.
   Part B deletes this test (shape j then survives; see test 16).
8b. `HardDelete_NoLeastPrivilegeGlobalRole_RefusedBeforeAnySideEffect` (Sqlite): as test 7 but with no global role free of all three flags. Result is a failure with a message containing `no global least-privilege role`, and there are no side effects. Part B deletes it.
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
    No tenant in hand (super-admin caller viewing a member) → the earliest **live** membership's role, even when an earlier **ended** membership exists (Gemini MEDIUM).
14b. `Profile_And_Preferences_Visible_WhenCreationRoleBelongsToAnotherWorkspace` (Opus MEDIUM). The identity has `users.role_id` = a role **owned by W** and a live membership in X.
    As tenant X: `ProfileService.GetByIdAsync(id)` and `GetByPublicIdAsync(publicId)` → success; `PreferencesService` update (e.g. `Theme = "dark"`) → success. On `main` all three return NotFound (the INNER JOIN).
14c. `Creators_SaveIdentityAndFirstMembership_InOneSaveChanges` (Opus MEDIUM, task A15). Subscribe to the context's `SavingChanges` event (precedent: the interceptor in `Tests/DemoServiceAnalyticsFailureTests.cs:156-170`;
    the event needs no options change): `ctx.SavingChanges += (o, _) => saves.Add(((DbContext)o!).ChangeTracker.Entries().Where(e => e.State == EntityState.Added).Select(e => e.Entity.GetType()).ToHashSet());`.
    For each of the 8 creator paths, build the service exactly as its existing test file does and create a **new** identity. The builders are in `WorkspaceTests.cs:601-608` (TenantService), `WorkspaceAdminOwnershipTests.cs` (UserService), `InviteServiceTests.cs` `BuildService` (three invite paths),
    `ChangePasswordTests.cs:160-173` (AuthService: register, register-admin) and `Db17DemoServiceTests.cs` (DemoService). Assert that the **first** recorded save containing `typeof(User)` also contains `typeof(WorkspaceMembership)`.
14d. `AdminSeeder_DoesNotPromoteAWorkspaceMember` (D11f.8; copy the `AdminSeeder.SeedAsync` harness of `Tests/PlanSeederTests.cs`). An identity with `ADMIN__EMAIL`'s address and a live membership exists.
    After `SeedAsync` its `RoleId` is unchanged and it is not a super admin, and the plan seeding still ran (plans exist). The same harness with a membership-less address still promotes (regression guard).
15. **Guards that must pass unchanged:** `Db18WorkspaceLifecycleTests.Preview_AccountsCount_EqualsRowsActuallyDeleted` (`:2118`), `WorkspaceMembershipTests.HardDelete_Workspace_KeepsMultiWorkspaceIdentity_EndsOnlyThatMembership` (`:527`),
    `WorkspaceTests.HardDelete_RemovesEverything_EvenWithSuggestionNotification` (`:388`), `WorkspaceSwitchTests` `IsHome` asserts (`:294-296,741-742`), the whole `Tests/WorkspaceMembershipTests.cs`
    (**R8.7 tenancy proof: tenant B sees no identity of A**), `Tests/TenantQueryFilterTests.cs`, `Tests/AuditCoverageTests.cs`. List them in the PR as run.

### Part B
16. Delete tests 6, 8 and 8b (they read `OwnerId` or exercise Part A-only code). Add `HardDelete_MembershipLessIdentity_Survives_AndDoesNotBlock` (Sqlite, shape j without `OwnerId`): `HardDeleteAsync(W)` succeeds and the identity row survives.
17. `User_HasNoLegacyTenancyMembers` (copy `Tests/Db11eLegacyDemoStateRemovedTests.cs`'s model check): `typeof(User).GetProperty("OwnerId")` and `("ApprovalStatus")` are null, and so is `db.Model.FindEntityType(typeof(User))!.FindProperty(...)` for both.
    `FindEntityType(typeof(User))!.GetIndexes()` has none named `ux_users_email_owner_live` and none over `OwnerId`. `FindProperty("RoleId")!.IsNullable` is `true`. `Role`, `IsActive`, `MergedIntoUserId` exist (guards over-deletion).
18. `NewIdentity_And_DemoProvision_WriteNoPlatformRole`: `NewIdentity(...).RoleId` is null. After `DemoService.ProvisionAsync` **and its audit write**, the demo identity's `RoleId` is null in a fresh context. This catches the `:292` fix-up.
    `AdminSeeder` run against an empty InMemory DB → the super admin has `RoleId` = the super role.
19. `Tests/WorkspaceTests.cs:382` → `Assert.Equal(22, TenantService.HardDeleteOrder.Length);`. In `HardDelete_RemovesEverything…` (`:388`), keep a variable for the seeded admin's `Id` and add
    `Assert.Null(verify.Users.IgnoreQueryFilters().SingleOrDefault(u => u.Id == <that id>));` (the per-type loop no longer covers `User`).
20. Semantic rewrites (all from §2's scan, line numbers at `0d5f75a`): `Db17DemoServiceTests.cs:960` T2; `Db18WorkspaceLifecycleTests.cs:2189` T1 (`verify`, `survivor`, `otherWorkspaceId`);
    `DemoUpgradeTests.cs:295` → `Assert.Equal(demoWorkspaceId, row.OwnerId);`; `InviteServiceTests.cs:626-627` T3+T1, `:818` → `Assert.Null(created.RoleId);` (membership asserts `:822-828` already cover the role; also delete the `.Include(u => u.Role)` at `:816`),
    `:821` delete, `:826` → `Assert.NotEqual(Guid.Empty, membership.OwnerId);`, `:830` delete, `:919` delete, `:921-922` T1+T3, `:1036` T3 (`roleId`, `pick@role.com`),
    `:1832` and `:1886` → `Assert.Empty(db.WorkspaceMemberships.IgnoreQueryFilters().Where(m => m.RoleId == clientRoleId));`, `:1929-1931` T3+T1 (+ delete the `ApprovalStatus` assert);
    `WorkspaceAdminOwnershipTests.cs:190-191` T3+T1, `:236` T1, `:285-286` T3+T1; `WorkspaceBeforeIdentityOrderingTests.cs:253` T1, `:294-295` T2; `WorkspaceMembershipTests.cs:619` T1 (`verify`, `survivingX`, `workspaceB`),
    `:793` T1 (`verify2`); `WorkspaceTests.cs:328` T2; `TokenServiceTests.cs:44,49` delete `, OwnerId = tenantId` / `, OwnerId = null`.
    Assertions on the **seeded** `RoleId` of test-created users (`DeletionSemanticsTests.cs:351`, `UserGovernanceTests.cs:652`, `RoleServiceDeleteTests.cs:149`) keep passing unchanged. Leave them.
21. **Existing data survives** — Postgres-only, proven in the R11 rehearsal (§7 criterion B6), pasted into the PR.
22. **Test harness deviation (code review NIT, accepted as-is — §15):** `Tests/SoftDeleteUniqueIndexTests.cs`'s `TestDb` bootstrap recreates `ux_users_email_live` with a raw `ExecuteSqlRaw` call (Sqlite `lower(email)`
    form), because the index is unmodelled in EF (`UserMapping.cs`) and `EnsureCreated()` therefore never creates it on the Sqlite in-memory test provider. Its DDL is NOT shared via a constant with the production migration
    (`20260922205015_AddUsersEmailLiveUniqueIndex.cs`, Postgres form with `IF NOT EXISTS`) — historical migrations are never edited (R10), so extracting a shared constant would mean retrofitting a frozen migration file to
    reference it. The two DDL strings are intentionally kept independent and dialect-specific; this note is the "share… or just note it" resolution.

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
   p='Domain/Entity/Role.cs'; s=open(p).read(); old='    public ICollection<User> Users { get; set; }'
   assert old in s, old
   open(p,'w').write(s.replace(old,'    [System.Obsolete("DB11F_ROLEUSERS")]\n'+old))
   EOF
   # strip the trailing "[…csproj]" FIRST, then the path prefix (cross-review Opus: the greedy prefix
   # match used to run inside the csproj bracket and ate the DB11F_* tags)
   dotnet build Pointer.sln -nologo --no-incremental 2>&1 | grep -E "warning CS0618.*DB11F" | sed -E 's/ \[[^]]*\]$//; s#^.*/db11f-scan/##' | sort -u | grep -v '^Tests/' > /tmp/db11f-a.txt
   grep -c . /tmp/db11f-a.txt    # sanity: must be > 0 and every line must start with a repo path (API/, Application/, Infrastructure/)
   grep DB11F_OWNERID  /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   grep DB11F_APPROVAL /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   grep DB11F_ROLEID   /tmp/db11f-a.txt | cut -d'(' -f1 | sort | uniq -c
   ```
   Expected **files** per tag (counts are one per member access; `sort -u` keeps line+column, so two accesses on one line count twice). Baseline before Part A at `0d5f75a`: 73 lines = APPROVAL 10, OWNERID 15, ROLE 37, ROLEID 10, ROLEUSERS 1.
   **OWNERID**: `Infrastructure/Mappings/UserMapping.cs` 4, `Application/Services/Implementation/MembershipService.cs` 1, `…/DemoService.cs` 1, `…/TenantService.cs` 4 (all in `DB-11f PART A ONLY` code:
   one in the pre-flight, three in the maintenance block). **No** `AuthService.cs`, **no** `AppDbContext.cs`.
   **APPROVAL**: `UserMapping.cs` 1, `MembershipService.cs` 1, `DemoService.cs` 1, `API/Seed/AdminSeeder.cs` 3. **No** `AuthService.cs`.
   **ROLEID** (every one a legacy write, Part A-only maintenance, or a platform-role read): `UserMapping.cs` 2, `MembershipService.cs` 2 (`NewIdentity` write + `PlatformRoleAsync`), `DemoService.cs` 1,
   `AdminSeeder.cs` 4 (`:148`, the two at `:158`, the A17 guard), `Infrastructure/Auth/JwtTokenService.cs` 1 (`IssueImpersonation`), `ProfileService.cs` 1 (platform-role lookup),
   `TenantService.cs` 7 (`IdentitiesDeletedWithWorkspace` 1, pre-flight 2, maintenance 4), **`Infrastructure/AppDbContext.cs` 1** (the shipped subquery form's `e.RoleId`, task A10 — added by the
   Part A review, 2026-09-24; absent from the pre-review baseline count above because the doc's originally-written literal navigation form read `e.Role`, tagged ROLE not ROLEID). **No** `UserMapper.cs`, **no** `AuthService.cs`, **no** `PreferencesService.cs`.
   **ROLEUSERS**: `UserMapping.cs` 1. A file missing from, or added to, these lists is a finding → stop and report. **ROLE**:
   `grep "DB11F_ROLE'" /tmp/db11f-a.txt | while IFS='(' read f rest; do l=${rest%%,*}; sed -n "${l}p" "/tmp/db11f-scan/$f"; done | grep -vE 'Include\(u => u\.Role\)|IsSuperAdmin|operatorUser\.Role|HasOne\(x => x\.Role\)'` → **no output**;
   and `grep "DB11F_ROLE'" /tmp/db11f-a.txt | grep -cE "ProfileService|PreferencesService|DemoService.cs\(292"` → 0.
   Paste all four outputs in the PR. Then `rm -rf /tmp/db11f-scan`.
3. `grep -n "OwnerId == workspaceId" Application/Services/Implementation/TenantService.cs` → only `DeleteOwnedAsync` lines, the `Role` query, and one line inside the maintenance block;
   `grep -c "DB-11f invariant I1" Application/Services/Implementation/TenantService.cs` → 1; `grep -c "HomeWorkspaceIdAsync" Application/Services/Implementation/AuthService.cs` → 5;
   `grep -c "ApprovalStatus.Pending\|ApprovalStatus.Rejected" Application/Services/Implementation/AuthService.cs` → the count on `main` minus 4; `grep -rn "SessionRole" Application Infrastructure | wc -l` → ≥ 5;
   `grep -n "e.Memberships.Any()" Infrastructure/AppDbContext.cs` → nothing, and `grep -c "Set<Role>().Any(r => r.Id == e.RoleId && r.IsSuperAdmin)" Infrastructure/AppDbContext.cs` → 1 (the shipped subquery form, task A10 — **not** the doc's originally-written `e.Role != null && e.Role.IsSuperAdmin`, which is absent from the shipped file);
   `grep -c "Include(u => u.Role)" Application/Services/Implementation/ProfileService.cs Application/Services/Implementation/PreferencesService.cs` → 0 each; `grep -c "LeastPrivilegeGlobalRoleIdAsync" Application/Services/Implementation/TenantService.cs` → 3 (definition + pre-flight + maintenance).
4. `just test` green (tests 5, 7-14d new; 1-3 edited; 15 unchanged). CI green (DB-10 job, `MigrationSafetyTests`). Task A15's `grep` output is pasted (JoinAsync between identity `AddAsync` and the first save at all 8 sites).
5. **Prod pre-checks (§9 Part A step 1) run on a same-day dump** (the rehearsal copy is enough for Part A): **blocking** P1, P2, P4, P5, **P6b, P9, P11** print the expected results. P3/P6/P7 pasted.
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
   - **rollback drill (§8 path A).** This is the **only** proof of B-1's `Down()` (Gemini HIGH / Opus LOW): the DB-10 CI job round-trips only the newest migration, B-2. Paste the generated `/tmp/db11f-down.sql` into the PR,
     confirming B-2's `Down` `UPDATE` comes first and B-1's `ALTER COLUMN role_id SET NOT NULL` carries no `DEFAULT 0`. Commands: `dotnet ef migrations script <ts>_ClearUsersRoleIdForMembers 20260923220301_AddWorkspacesPauseAndDeletionState -p Infrastructure -s API -o /tmp/db11f-down.sql`, apply with
     `docker exec -i pointer-db11f-rehearsal psql -U pointer -d pointer_rehearsal -v ON_ERROR_STOP=1 < /tmp/db11f-down.sql` (Part B review: **no `-1`** — the generated script already wraps EACH migration's `Down()` in its own
     `START TRANSACTION; … COMMIT;`, confirmed by reading the actual generated output; `-1` would additionally wrap the whole multi-migration script in one psql-level transaction, which the script's own embedded `COMMIT`s make
     misleading — the first migration's `Down()` is already durably committed by the time psql's outer wrapper would supposedly still be able to roll it back) → columns/indexes/FK back, `role_id NOT NULL` with no NULLs, `approval_status` all 1,
     `owner_id` all NULL, 81 history rows. Boot **Part A code** against it → super admin + demo log in, `DELETE /api/admin/tenants/{B}` → 200. Then `database update` again → 83.
     **Verified on a throwaway `postgres:15`** (2026-09-24, review pass): `database update` to HEAD (83 rows), manually seeded a least-privilege global role + a NULL-`role_id` member, `database update 20260924051145_DropUsersLegacyTenancyColumns`
     (= roll back only B-2) → refill worked (`role_id` set to the seeded role), history 82; `database update` forward again → 83, `role_id` NULL again; then **deleted** the least-privilege role and repeated the B-2-only rollback →
     `P0001: DB-11f: no least-privilege global role — cannot roll back B-2` raised, history **stayed at 83** (the guard runs before the refill `UPDATE`, in the same migration transaction, so nothing partially applied).
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
  the columns with `owner_id` NULL and `approval_status` = 1. B-2's `Down()` refills every NULL `role_id` with the least-privilege global role (never a membership's role, so it cannot escalate; §12 O5/O8), **not** the original creation role. The only copy of the originals is the
  `pre-db11f` dump the contract deploy takes immediately before the migrations (R7). This is acceptable because Part A code reads none of them for behaviour (§7 Part A criterion 2).
- **Migration fails while applying:** each migration runs in its own transaction. If B-1 fails, nothing is applied. If B-2 fails, B-1 stays applied. Either way the API exits (DB-09).
  Answer: path B (restore `pre-db11f`), never a hand-fix of the schema or `__EFMigrationsHistory` (R7.1 point 6 spirit).
- **Migrations applied, new code misbehaves.** Part A code maps `OwnerId`/`ApprovalStatus`, so it cannot run on the contracted schema (42703). The reverse is also true (Gemini LOW, §12 G13):
  Part B code must **not** run against the rolled-back schema, because it never writes the restored NOT NULL `role_id`. So the order is always: API stopped → `Down` script → Part A binary → start. Never run `Down` under a live Part B API. Options in order of preference:
  - **A (lossless for live data, rehearsed in §7 B6):** generate `/tmp/db11f-down.sql` on a workstation (command in §7 B6) and read it (B-2 refill `UPDATE`, B-1 inverse, two `DELETE FROM "__EFMigrationsHistory"`).
    Copy it to the VM. `docker compose -f docker-compose.prod.yml stop api`; `bash scripts/backup-db.sh pre-db11f-rollback`;
    `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -v ON_ERROR_STOP=1 < db11f-down.sql` (Part B review: **no `-1`** — see §7 B6; the script already commits each
    migration's `Down()` separately, `-1` adds nothing and is misleading about the atomicity it appears to promise);
    `git checkout <commit before the Part B merge>` (= Part A); `up -d --build api`. Part A code works with `owner_id` NULL: it reads nothing from it. Its maintenance block only
    re-points rows that point at the deleted workspace, and NULL rows point nowhere.

    **Each migration's `Down()` runs in its own transaction** (confirmed by reading the generated script, §7 B6) — so within ONE migration's `Down()` the DDL is all-or-nothing (a mid-migration statement error rolls that
    migration's own transaction back), but a half-applied rollback ACROSS migrations is still possible: B-2's `Down()` (the refill `UPDATE` + its guard) commits in its own transaction first, and only THEN does B-1's `Down()`
    start (a separate transaction, recreating the legacy columns/index/FK); if B-1's `Down()` then fails — e.g. its `ADD CONSTRAINT fk_users_workspaces_owner_id` step, should a stray `owner_id` value not reference a live
    workspace — B-1's transaction rolls back cleanly, but B-2's has ALREADY committed. The database is left at history 82: `role_id` refilled (no NULLs) but still nullable (B-1's `SET NOT NULL` never ran), the legacy columns
    still absent, and `psql` exits non-zero with `ON_ERROR_STOP=1`. **Recovery:** do not
    hand-edit the schema or `__EFMigrationsHistory`. Inspect `\d users` and `SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1 DESC LIMIT 3;` to see exactly which migrations are applied, then run
    `dotnet ef database update <target-migration> -p Infrastructure -s API` against the SAME connection string, naming either `20260923220301_AddWorkspacesPauseAndDeletionState` (finish rolling back to Part A) or
    `20260924051230_ClearUsersRoleIdForMembers` (push forward again to Part B/HEAD) — EF's migrator is idempotent per-migration and will only apply/revert what the history table says is missing/present. If that
    also fails, fall back to path B (restore `pre-db11f`).
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
   - **P6 — exposure of `users.role_id` to the latent tenant-role FK bug (informational; Part A re-points it):**
     ```sql
     SELECT u.id, u.owner_id, r.id AS role_id, r.owner_id AS role_owner FROM users u JOIN roles r ON r.id = u.role_id
     WHERE r.owner_id IS NOT NULL AND r.owner_id IS DISTINCT FROM u.owner_id;
     ```
   - **P6b — a membership holds another workspace's role (blocking, must print 0 rows; cross-review Opus MEDIUM).** Such a row makes that other workspace's hard delete fail with 23503
     at `DeleteOwnedAsync<Role>`, and DB-11f does not repair memberships:
     ```sql
     SELECT m.id, m.user_id, m.owner_id, r.id AS role_id, r.owner_id AS role_owner FROM workspace_memberships m JOIN roles r ON r.id = m.role_id
     WHERE r.owner_id IS NOT NULL AND r.owner_id <> m.owner_id;
     ```
     Any row → **stop, report to the owner** (repair is a separate, owner-approved data fix; follow-up F1 closes the path).
   - **P9 — the least-privilege global role exists (blocking, ≥ 1; used by the Part A re-point and by B-2's `Down()`; cross-review Opus LOW/Gemini MEDIUM):**
     `SELECT id, name FROM roles WHERE owner_id IS NULL AND deleted_at IS NULL AND NOT is_super_admin AND NOT grants_admin AND NOT quick_access ORDER BY id;` (expected: `Developer`, `PM`, `Tester` on a seeded DB).
   - **P11 — no membership holds the platform role (blocking, must print 0 rows; cross-review Opus MEDIUM, privilege escalation):**
     `SELECT m.id, m.user_id, m.owner_id FROM workspace_memberships m JOIN roles r ON r.id = m.role_id WHERE r.is_super_admin;`
     A row is a member whose sessions in that workspace carry `is_super_admin=true` today. **Stop and report to the owner as a security finding.**
   - **P7 — merged/aliases census (expected `0 | 0`):** `SELECT (SELECT count(*) FROM users WHERE merged_into_user_id IS NOT NULL), (SELECT count(*) FROM user_aliases);`
2. Local e2e gate (§7 A6) PASS.
3. Merge; CI green; ordinary `bash scripts/deploy-api.sh` (`DEPLOY.md` § Updating). The pre-flight must show **no** pending migration.
4. **Verify:** `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "error|DB-11f invariant"` → nothing. Log in to `app.pointer.moamen.work` as super admin (Tenants page lists every workspace)
   and as a workspace admin (the picker/switcher shows the same "Home" badge as before). The DB-18 settings "Delete workspace" preview shows the same account count as before the deploy (compare with P2's `old` set).
5. **Watch (≥ 24 h, this is the gate for Part B):** `docker compose -f docker-compose.prod.yml logs --since 24h api | grep -E "DB-11f invariant I1|no global least-privilege role|23503|42703"` → nothing, **at any log level**.
   `WorkspaceDeletionService` logs a refused delete at Information as "skipped (cancelled): <message>", and both loops retry every sweep (§2). **Any `DB-11f invariant I1` line blocks Part B** until the row is repaired under an owner-approved fix.
   At the end of the window, re-run **P1** and **P11** on production (both must still print 0 rows). That proves A15's atomic creation held and nothing new appeared.

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
   - **P9 again** (Part A step 1 query; blocking, ≥ 1 row), plus **P6b** and **P11** again (0 rows each).
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

**Follow-up F1 (recommended, not in this doc; security):** membership role assignment is not bounded to the membership's workspace or away from the platform role for an
operator. `UserService` create/approve/update (`GetActiveRoleAsync`, `UserService.cs:910-915`, under a filter the super admin bypasses) and `RoleService.DeleteAsync` reassign (`RoleService.cs:292-299`
guards only non-super callers) let a super admin put a membership in X on a role owned by W, which later makes W's hard delete 23503, or on the `is_super_admin` role, which makes that member's
workspace sessions carry `is_super_admin=true`. Fix: refuse `role.IsSuperAdmin` always, and `role.OwnerId != null && role.OwnerId != <membership workspace>`, at every membership role write.
DB-11f only guards it at deploy time (P6b, P11) and never copies such a role (§3.2, §3.4).
`TenantResponse.Id` / `PublicId` (membership-sourced legacy DTO fields; the dashboard reads `publicId` — removing `Id` is a separate API-contract change for the dashboard-agent, not a schema change);
replacing `users.role_id` with a boolean (D11f.2 alternative → DB-11g if chosen); `NewIdentity`'s now-unused parameters; FKs from content `author_id` columns (Q5);
`workspace_memberships` schema or states; the DB-18 deletion e-mail/i18n wording; `DemoCleanupService`, `WorkspaceDeletionService`, `WorkspaceLifecycleService` (they call the shared rule unchanged);
every historical migration file (R10); `clients/` (generated); the `pointer-dashboard` repo; widget, CLI, extension, landing; `API/wwwroot` served docs and `docs/ON-DISK-CONTRACT.md` (nothing in them names these columns).

## 11. Dashboard / widget / CLI tasks

**Dashboard:** none in source. `isHome`, `roleName`, `approvalStatus`, `publicId` keep their meaning. **Widget:** none (`templates.ts:71` reads `isHome`, semantics kept). **CLI:** none. Only a stray
non-super **null-owner** key stops working (D11f.6). CLI keys minted since DB-11a carry the workspace (`owner_id`), so they are unaffected.

## 12. Cross-review adjudication (2026-09-24)

Reviews: `docs/db/reviews/REVIEW-DB11F-2026-09-24.md` (Gemini 3.8 Flash = G*, Opus = O*). Every finding was verified against `0d5f75a`. Several Gemini citations point at sections or files this doc and repo do not have (`§4.1`, `§5.2`, `Tests/IntegrationTestBase.cs`, migration `20260420000000_FixCustomRoleFkOnDelete`). Those were checked for substance, not location.
**Owner decisions touched:** none of the owner-answered ones. D11f.1, D11f.2 and D11f.3 are unchanged, and the Part B approval line and its scope are unchanged. **D11f.7** (a default, not owner-answered) is refined and **D11f.8** is new. Both are flagged for owner confirmation in §0.

| # | Finding (severity) | Verdict + why | Where changed |
|---|---|---|---|
| O1 | A10 `!e.Memberships.Any()` runs under the membership filter; a null-tenant non-super caller sees every identity in non-strict mode (HIGH) | **Accepted.** Verified `AppDbContext.cs:335-339`: the membership filter is `IsSuperAdmin \|\| (TenantId != null && OwnerId == TenantId)`, so every membership is hidden and `Any()` is false for all. Fix: the branch keys on the **platform role** `e.Role != null && e.Role.IsSuperAdmin`. That is exactly today's `owner_id IS NULL` set (P4) and is allowed by D11f.2. It was chosen over dropping the branch because it keeps today's non-strict behaviour exactly. Prod is strict, so there is no prod change either way | §2, §3.3, A10, §6 edit 3 (now also the regression test), §7 A3 |
| O2 | For an operator reason, the audit row and file delete run before the transaction; an I1 throw leaves a live workspace with no files and a "deleted" audit row (MEDIUM) | **Accepted.** Verified `TenantService.cs:608-620` vs `:624`. Added a read-only pre-flight after `:581` returning `Result.Failure` (I1 and missing-fallback-role). The in-transaction throws stay as backstops | §3.2, A3, §6 tests 8, 8b |
| O3 | I1 is not by construction: 8 creator sites save identity and membership separately, with side effects between (MEDIUM) | **Accepted.** Verified all 7 `NewIdentity` sites **plus** `DemoService.ProvisionAsync` (`:183` → `:249`). Each becomes a single save (deletes or moves only; the quick-access site saves the invite first, because the membership needs `invite.Id`). P1 and P11 are re-run at the end of the Part A watch | §2, §3.1, §3.3, A15, §6 14c, §7 A4, §9 step 5 |
| O4 | Latent-FK fix incomplete: `workspace_memberships.role_id` can hold another workspace's role (super admin bypass); the re-point could copy it (MEDIUM) | **Accepted.** Verified `UserService.cs:910-915` (filtered read that super admins bypass) and `:267-272` (guard for non-super only). P6b is now **blocking**. The re-point no longer copies a membership role at all. §3.7 no longer claims the whole 23503 class is gone. Root guard → follow-up F1 | §2, §3.2, §3.7, §9 P6b, §10 F1 |
| O5 | Privilege escalation: a membership on the `is_super_admin` role copied into `users.role_id` by the re-point or by B-2 `Down` (MEDIUM) | **Accepted.** Re-point and B-2 `Down` now use only the least-privilege global role (no super/admin/quick-access flag). New blocking **P11**: no membership holds the platform role (a row = existing security finding). Test 7 seeds exactly this shape | §3.2, §3.4 B-2 Down, §8, §9 P11, §6 test 7, §10 F1 |
| O6 | `Include(u => u.Role)` on a required nav is an INNER JOIN on the filtered Role set, so profile/preferences 404 for a member whose creation role belongs to another workspace (MEDIUM) | **Accepted.** Verified: only `ProfileService.cs:32,45` and `PreferencesService.cs:39` Include under filters; all other Include sites use `IgnoreQueryFilters()`. Those three Includes are removed. The platform role is loaded filter-free (`IMembershipService.PlatformRoleAsync`; inline in ProfileService) | §2, §3.3, A1, A2, A7, A8, §6 14b, §7 A3 |
| O7 | §7 A2 scan script broken (greedy prefix strip eats the tag); `Role.Users` never marked (MEDIUM) | **Accepted.** Reproduced: `s#^.*/db11f-scan/##` matches inside `[…csproj]`. Fixed order (`s/ \[[^]]*\]$//` first), `Role.Users` marked, sanity line added, expected per-file counts recomputed for the amended Part A code (baseline re-run at `0d5f75a`: 543 / 73 non-test) | §2, §7 A2 |
| O8 | B-2 `Down` fallback "lowest-id global non-super" = `Workspace Admin` (admin-tier) (LOW) | **Accepted.** Verified `AdminSeeder.cs:16-25` order. The fallback now also excludes `grants_admin` and `quick_access` and requires `deleted_at IS NULL` (= `Developer`) | §2, §3.4, P9 |
| O9 | The claim that the hosted loops catch only `DeletionPreconditionChangedException` is wrong (LOW) | **Accepted.** Verified `DemoCleanupService.cs:194`, `WorkspaceDeletionService.cs:166,179`. The text is corrected. §9 greps the message at every level, and any hit blocks Part B | §2, §3.2, §9 step 5 |
| O10 | AdminSeeder promotes a workspace member matching `ADMIN__EMAIL`; the new rule excludes super admins, so the member's workspace delete would hit I1 (LOW) | **Accepted** as D11f.8: the seeder refuses (throws inside its own try/catch, `:130-178`, so the reconcile is skipped and logged and the plan seeding still runs) | §0 D11f.8, §3.3, A17, §6 14d, §7 A2 counts |
| O11 | Stale line refs vs HEAD (LOW) | **Accepted.** Production code is unchanged `8f97871`→`0d5f75a` except `EmailLayout.cs`. Test refs re-anchored (Db18 `:2118/:2189`, InviteServiceTests `+2`/`+45`, UserGovernanceTests `:652`, WorkspaceTests `:382`). B9 says to trust the compiler's lines. Issue sites clarified (Role assignment vs Issue lines) | header, §2, §5 B9, §6 items 15, 19, 20 |
| O12 | CI round-trip only exercises B-2; B-1 `Down` proven only in R11 (LOW) | **Accepted** (= G3). The R11 rollback drill is stated as B-1's only proof, and the generated down-script is pasted and checked (`SET NOT NULL`, no `DEFAULT 0`, B-2 refill first). A CI change is out of scope | §7 B6 |
| O13 | "70 sites" should be 72 (+1 `Role.Users`) (NIT) | **Accepted**, corrected to the re-run figure: 73 lines outside `Tests/` (72 `User` + 1 `Role.Users`, per line+column) | §2 |
| O14 | `grep -c … -r` prints per-file counts (NIT) | **Accepted**: `grep -rn … \| wc -l` | §7 A3 |
| G1 | Membership-less identities: new rule excludes them; Part A throws, Part B orphans (HIGH) | **Accepted as clarification.** This was already D11f.4 with P1 blocking. It is now also impossible to create (O3), refused before side effects (O2), and the "survives in Part B" consequence is stated in D11f.4 | §0 D11f.4, §3.1 |
| G2 | False parity claim "P2 and P3 together prove…" (HIGH) | **Rejected as stated**: no such sentence exists in this doc (checked). §1/§3.7 attribute parity to P2 only. A "what each pre-check proves" list was still added, to remove any doubt | §3.1 |
| G3 | CI never rolls back B-1 (HIGH) | **Accepted** (= O12). The staging command Gemini cites names a migration that does not exist; the R11 drill is the proof | §7 B6 |
| G4 | ProfileService fallback picks the earliest membership even if ended (MEDIUM) | **Accepted.** This is D11f.7, a default and not owner-answered. With no workspace in hand, the earliest **live** membership wins, else the earliest of any state. Flagged in §0 for owner confirmation | §0 D11f.7, A8, §6 test 14 |
| G5 | Move the `demoUser.Role = role` removal to Part A (MEDIUM) | **Accepted.** After `SessionRole` nothing reads it (`DemoService.cs:292-294`) | §3.3, §3.5, A16, B4, §7 A2 |
| G6 | B-2 `Down` fails if no global non-super role exists (MEDIUM) | **Accepted.** P9 is blocking in **both** parts (it also guards the Part A re-point) and uses the tightened predicate (O8) | §9 P9 |
| G7 | CI probe must change in the same commit as B-1 (MEDIUM) | **Accepted** | §3.5, B8 |
| G8 | `AdminSeeder.cs:158,160` are read-then-write, not writes (LOW) | **Accepted** | §2 inventory row |
| G9 | Db18 test line drift (LOW) | **Accepted** (= O11) | §2, §6 |
| G10 | Merged rows would surface as P2 `new_only` (LOW) | **Accepted as a note.** A `new_only` merged row is an intended fix, not a parity failure. Moot in prod (P7 = 0/0) | §3.1 |
| G11 | `WorkspaceTests.cs` missing from a "§5.2 files table" (LOW) | **Rejected**: no such table; the edit is already §6 test 19 (now `:382`) | — |
| G12 | `Tests/IntegrationTestBase.cs` `CreateUserAsync` legacy writes (LOW) | **Rejected**: the file does not exist. Every test that seeds or queries the removed members is covered by the compiler-driven B9 and recipes T1–T3 | — |
| G13 | Rollback needs the Part A binary before `Down` (LOW) | **Accepted as clarification.** §8 already stopped the API first; the order is now stated explicitly | §8 |
| G14 | Home should prefer an active membership (LOW) | **Rejected**: it would change owner-answered **D11f.3**. Its premise ("initialize the session in an inactive workspace") is also wrong, because home only sets `IsHome` on live candidates and names the mail workspace; the picker never uses it to choose. Recorded in §0 as a possible later owner change | §0 D11f.3 |
| G15 | `WorkspaceTests` `:381` → `:382` (NIT) | **Accepted** | §2, §6 test 19 |
| G16 | Verify the snapshot is unchanged when adding B-2 (NIT) | **Already covered**: B11 ("no snapshot change … stop") and §7 B2 (Designer `BuildTargetModel` diff empty) | — |

## 13. Part A review adjudication (2026-09-24)

A second review round (orchestrator against the shipped code, Opus implementing), after Part A first landed. Opus re-confirmed
the shipped `AppDbContext.cs` role-filter subquery form (`Set<Role>().Any(r => r.Id == e.RoleId && r.IsSuperAdmin)`) against real
Postgres 15 — **kept as-is** (§3.3, §7 A3, this doc's own text amended below to match; the doc had still shown its
originally-written literal form).

| # | Finding (severity) | Verdict + fix | Where changed |
|---|---|---|---|
| R1 | F1 gap: `UserService.CreateAsync` only checked `GrantsAdmin`/`IsSuperAdmin`, missing the foreign-workspace-role half of the F1 predicate that `ApproveAsync`/`UpdateAsync` already had (HIGH) | **Accepted.** A single `role.IsSuperAdmin \|\| (role.OwnerId != null && role.OwnerId != ownerId)` check now runs once both `role` and `ownerId` are resolved, covering both the super-admin and non-super branches | `UserService.cs` `CreateAsync` |
| R2 | Global-role lookups **by Name** (not id) bypass the Role query filter for a super-admin caller entirely, and are unscoped even for a non-super one — `UserService.cs` (`CreateAsync`'s `deputyRole`, `TransferOwnershipAsync`'s `adminRole`/`deputyRole`), `InviteService.cs` (`CreateAsync`'s super-admin-to-existing-workspace `deputyRole`) resolved whichever row matched the NAME, regardless of `OwnerId` — a workspace-owned custom role sharing the exact name could be picked instead of the real global one (HIGH) | **Accepted.** `&& r.OwnerId == null` added to all four lookups. Also found and fixed the same gap in `DemoService.ProvisionAsync`'s `IgnoreQueryFilters()` "Workspace Admin" lookup during the audit (not explicitly named by the reviewer, same shape) | `UserService.cs` (×2 sites), `InviteService.cs` (×1 site), `DemoService.cs` (×1 site, found by audit) |
| R3 | No defense-in-depth at the one place every membership row is actually created (`MembershipService.JoinAsync`) — every caller is trusted to have already refused a bad role (MEDIUM) | **Accepted.** `JoinAsync` now throws `InvalidOperationException` if `role.IsSuperAdmin \|\| (role.OwnerId != null && role.OwnerId != workspaceId)`. Audited all 10 call sites (`DemoService`, `InviteService` ×3, `TenantService`, `UserService`, `AuthService` ×3) — none legitimately needed a role this guard refuses; full suite green | `MembershipService.cs`, `IMembershipService.cs` |
| R4 | D11f.7 ex-member: a tenant-X caller viewing a FORMER member of X (no live membership left there) got `RoleName = ""` instead of a fallback role (MEDIUM; orchestrator decision — **not asked of the owner**, since D11f.7 itself was already a refined default, not an owner-answered decision, §0/§12 G4) | **Accepted.** Falls back to that SAME workspace's latest ENDED membership role (`OrderByDescending(LeftAt).ThenByDescending(Id)`), never crossing into another workspace. Test added (`Profile_ExMember_FallsBackToLatestEndedMembershipRoleInSameWorkspace`) | `ProfileService.cs` `BuildAsync`, `Tests/Db11fMembershipRulesTests.cs` |
| R5 | Quick-access invite race: on the duplicate-email `DbUpdateException`, the invite row saved moments earlier (already marked "used") was left behind with no member ever created (MEDIUM) | **Accepted.** The catch clears the change tracker (the failed save's pending identity/membership/`QuickAccessLink` entries), re-fetches the invite by id, and deletes it. Regression test (`QuickAccessInvite_DuplicateEmailRace_RollsBackTheOrphanedInvite`) uses a `SaveChangesAsync`-counting `IUnitOfWork` decorator (same precedent as `ChangeEmailTests.ThrowDuplicateKeyUnitOfWork`) to throw only on the SECOND save; confirmed the test fails without the fix and passes with it | `InviteService.cs` `CreateQuickAccessInviteAsync` |
| R6 | Test-suite cleanup: two test comments described the REJECTED `e.Role != null && e.Role.IsSuperAdmin` navigation form as if it were shipped; an unused `uow0` local; the `AppDbContext.cs` comment contained the literal text `e.Memberships.Any()`, which §7 Part A criterion 2's own acceptance grep (`grep -n "e.Memberships.Any()" Infrastructure/AppDbContext.cs` → nothing) would have failed against (LOW, but a verification-breaking bug) | **Accepted.** `AuditQueryFilterTests.cs:206-209`, `TenantQueryFilterTests.cs:155-158` reworded to "a real Role row is needed for the platform-role branch"; `uow0` deleted (`Db11fMembershipRulesTests.cs`); `AppDbContext.cs`'s comment rephrased so the literal text isn't present, while still explaining the same thing (an emptiness check on `Memberships`, in prose) | `Tests/AuditQueryFilterTests.cs`, `Tests/TenantQueryFilterTests.cs`, `Tests/Db11fMembershipRulesTests.cs`, `Infrastructure/AppDbContext.cs` |
| R7 | Tests 3 F1 cases in `RoleServiceDeleteTests.cs` (`ScopedAdmin_CannotReassignTo_AdminGrantingRole`, `SuperAdmin_CannotReassignTo_OtherTenantRole`, `SuperAdmin_CannotReassignTo_SuperAdminRole`) asserted the refused reassignment didn't happen by reading `users.role_id` — but `RoleService.DeleteAsync`'s reassignment only ever writes `workspace_memberships.role_id` (DB-11a), so the assertion is **vacuous**: it passes whether or not the guard fired, because nothing in this code path ever touches `users.role_id` in the first place (HIGH — a false-negative-proof test) | **Accepted.** All three rewritten to assert the live membership's `RoleId` instead. Re-ran with the guard temporarily reverted to confirm each test actually fails without the fix (it does) | `Tests/RoleServiceDeleteTests.cs` |
| R8 | Doc drift: §3.3's `AppDbContext.cs:108` row, task A10 and §7 A2/A3 still showed the doc's originally-written literal navigation form (`e.Role != null && e.Role.IsSuperAdmin`) instead of the shipped subquery form Opus re-confirmed on Postgres 15 | **Accepted.** §3.3 row, §2 inventory row, task A10, §7 A2 (ROLEID list gains `Infrastructure/AppDbContext.cs` 1) and §7 A3's grep amended to the shipped form | §2, §3.3, A10, §7 A2, §7 A3 |

**Rejected (this round; each independently re-verified against the shipped code, not just re-read):**

| Finding | Reason rejected |
|---|---|
| Gemini: "impersonation sees all users" | By design, unchanged by Part A. `IdentityEraseService.cs`/similar super-admin-unconditional branches on `User`/membership metadata are DB-13 F2's documented exception (content stays workspace-scoped even under impersonation; **metadata** — who exists, their role — does not, because an operator's whole job is managing every workspace's members). Verified the specific branches are the same ones DB-13 already reviewed and accepted; nothing in this doc's read-path migration touches that boundary. |
| Gemini: "a selection/MFA token can't read its own row" | Identical on `main` — not a regression this doc introduces. The selection-token code path this doc's Part A touched (`SessionRole`, `HomeWorkspaceIdAsync`, the delete-set rule) does not intersect the MFA challenge's own row lookup, which reads the identity directly by id under `IgnoreQueryFilters()` exactly as it did before Part A. |
| Gemini: "add an index on users.role_id" | Already exists. `IX_users_role_id`, created by `InitialCreate.cs:183` (snapshot `:2258`) and confirmed still present and unchanged in the shipped model — this doc's own §2 inventory already recorded it (`users.role_id` row, "Both stay (D11f.2)"). |


## 14. Part A release record (2026-09-24)

- Implemented `f0d56d9`, `1352b55`, `4c14aa4`; review fixes `fef6afc`, `b9d2fd5`, `c2d1fe2` (reviews: `docs/db/reviews/REVIEW-DB11F-PARTA-CODE-2026-09-24.md` — Gemini 3.8 Flash, Gemini 3.1 Pro, Opus on Postgres 15). `dotnet test` 1520; no model change; no migration.
- **Pre-checks on the 03:00 UTC production dump** `pointer-20260924T030001Z.dump` (81 migrations): P1 0 rows, P2 0 rows, P3 0 rows, P4 2 super-admin rows (1 soft-deleted since 2026-07-01) with owner NULL / approval 1 / 0 memberships and 0 non-super NULL-owner identities, P5 0, P6 0, P6b 0, P9 Developer/PM/Tester, P11 0, P7 0|0.
- **Rehearsal on the same dump:** main and Part A booted in turn; as the production super admin and as a two-workspace Workspace Admin (password hash set on the local copy only): `/api/auth/me` ×2, `/api/admin/tenants` (3), `/api/admin/users` (4 as admin), `/api/me/profile`, `/api/admin/workspace`, login workspace picker (same `isHome`) — **identical responses**; no `DB-11f invariant`, 23503 or 42703 in either log.
- **Local e2e gate PASS** twice (before and after the review fixes).
- Pre-existing, not DB-11f: a password login by a multi-workspace identity writes no `auth.login.succeeded` audit row (AUDIT GAP error line; strict coverage is off in production) — follow-up.
- **Deployed 2026-09-24 05:05 UTC** (ordinary, via agy): deploy OK, `/health` 200, no pending migration, 0 invariant/23503/42703/Error lines in the first 3 min. 24 h watch → Part B not before **2026-09-25 05:05 UTC**.

## 15. Part B review adjudication (2026-09-24)

Reviews: `docs/db/reviews/REVIEW-DB11F-PARTB-CODE-2026-09-24.md` (Gemini 3.8 Flash = PB-G*, Opus code-reviewer = PB-O*). Every finding was re-verified against the shipped Part B code on
`feat/db-11f-part-b`, not just re-read. Fixed in a review-fix pass in the same git worktree (`pointer-db11f-b`); migrations and their bodies were still editable because Part B is **not yet deployed**
(the release-timing gate below is exactly why). `dotnet build` and `dotnet test Tests` are green throughout (1520 → 1524, the 4 new invariant tests).

| # | Finding (severity) | Verdict + why | Where changed |
|---|---|---|---|
| PB-G1 | Part B deployment timing violation: Part A was deployed `2026-09-24 05:05 UTC`; the mandatory 24 h soak has not elapsed, and DB-11f (§9 step 5, header) forbids merging/applying Part B before `2026-09-25 05:05 UTC` and the post-window P1/P11 checks (BLOCKER) | **Acknowledged, not a code defect** (orchestrator's own note in the review file). Already enforced by §9 Part B step 0 (owner approval line) and step 1 (re-run P1/P2/P4/P5/P9/P6b/P11 before merging); this review-fix pass itself does not merge, deploy, or touch any database but throwaway containers, and the header's Status line above now states the earliest allowed date explicitly | header Status line |
| PB-G2 / PB-O3 | Test 7 (`HardDelete_RehomedIdentityWithTenantRole_Succeeds_UnderRealForeignKeys`) was deleted outright instead of adapted for Part B; it was the only test exercising a real-FK Sqlite hard delete of a workspace-owned Role alongside a surviving multi-workspace identity — §6 item 16 authorized deleting only tests 6, 8 and 8b (MEDIUM) | **Accepted.** Re-added as `HardDelete_RehomedIdentityWithWOwnedRoleMembership_Succeeds_UnderRealForeignKeys`: identity seeded with `RoleId = null` (Part B: no tenant role left to re-point — D11f.2), memberships in W (a W-owned custom role) and X (an ordinary global role); `HardDeleteAsync(W)` succeeds, the survivor keeps only the X membership (`RoleId` still null on the identity), the W role is gone. The deviation from §6 item 16 is recorded there and in the test file's own header comment | `Tests/Db11fMembershipRulesTests.cs` (test re-added; §6 item 16 comment amended) |
| PB-G3 / PB-O2 (part) | Test 18 omitted part (3) of §6 item 18 — `AdminSeeder` against an empty database leaving the seeded super admin's `RoleId` = the super role id (LOW / part of Opus MEDIUM) | **Accepted.** Added, using the existing `BuildSeederProvider` harness (test 14d) against a fresh empty in-memory DB: asserts the seeded admin's `RoleId` equals the seeded super role's id | `Tests/Db11fMembershipRulesTests.cs` (test 18) |
| PB-O1 | InviteServiceTests.cs `QuickAccess_Create_RequiresEmail`/`RequiresProjectAppUrl` (`:1831`, `:1885`) assert `db.Users…Where(u => u.RoleId == clientRoleId)` is empty — vacuous, because nothing writes `users.role_id` to a tenant role any more regardless of whether the guard fired (MEDIUM) | **Accepted** (exactly §6 item 20's own prescribed rewrite, which had not been applied at these two sites). Both now assert `db.WorkspaceMemberships…Where(m => m.RoleId == clientRoleId)` is empty. Proved non-vacuous by temporarily bypassing each guard (email-required, app-url-required) and confirming the test then fails, then restoring | `Tests/InviteServiceTests.cs` (`:1831`, `:1885` region) |
| PB-O2 | Test 18's `DemoService` was built with the default `IAuditWriter` (a `NoopAuditWriter`, since no audit writer was passed), so a re-added `demoUser.Role = role` would never be persisted and the assertion would never catch it (MEDIUM) | **Accepted.** `DemoService` is now built with a REAL `AuditWriter` sharing the same `DbContext`/`UnitOfWork` (same pattern as `DemoServiceAnalyticsFailureTests.cs`), so its `WriteAsync`'s `SaveChangesAsync()` would persist any such regression. Proved by temporarily re-adding `demoUser.Role = role` in `DemoService.cs` and confirming the test fails (`Assert.Null(demoUser.RoleId)` fails with `1`), then reverting | `Tests/Db11fMembershipRulesTests.cs` (test 18), `Application/Services/Implementation/DemoService.cs` (temporarily, reverted) |
| PB-O4 | Nothing enforces `users.role_id ∈ {null} ∪ {global super-admin roles}` — the invariant is a design intent (D11f.2), not a checked one (LOW) | **Accepted**, task item 4. `AppDbContext.EnforcePlatformRoleInvariant()` added, refusing any save of a `User` whose non-null `RoleId` isn't a global (`OwnerId == null`) `IsSuperAdmin` role. Two deliberate, documented scope limits (both because production only ever writes through the plain async `SaveChangesAsync()` and never writes a bad `RoleId` there): checked on the async overloads only (hundreds of pre-existing test fixtures across ~15 files seed a pre-DB-11f-shaped tenant `RoleId` via the SYNCHRONOUS path, which nothing under test reads); and, for an already-tracked Modified entry, refused only when the save genuinely changes the VALUE (`OriginalValue` vs `CurrentValue`, not the coarser `IsModified` flag — ~15 production call sites use `IRepository<User>.Update(entity)` on an already-tracked entity, which EF marks every scalar property "modified" for regardless of whether its value changed). New tests: refuses a tenant `RoleId` on insert, refuses a genuine reassignment on an existing row, allows the global super role, allows `null`. Full suite green (1524) | `Infrastructure/AppDbContext.cs`, `Tests/Db11fMembershipRulesTests.cs` (4 new tests) |
| PB-O5 | B-2's `Down()` refill `UPDATE` silently no-ops (0 rows, no error) if no least-privilege global role exists, after which B-1's `Down()` fails setting `role_id NOT NULL` on a column that still has NULLs — a half-rolled-back state (LOW) | **Accepted**, task item 5. Added a `DO $$ … RAISE EXCEPTION …` guard immediately before the refill `UPDATE`, in the SAME `migrationBuilder.Sql()` call (so it runs first, in the same migration transaction). Verified on a throwaway `postgres:15` (2026-09-24): positive case (role exists) → refill works, history 82 → 83 round-trip clean; negative case (role deleted first) → `P0001: DB-11f: no least-privilege global role — cannot roll back B-2` raised, history **stays at 83** (nothing partially applied, since the guard runs before the `UPDATE` in one transaction) | `Infrastructure/Migrations/20260924051230_ClearUsersRoleIdForMembers.cs` |
| PB-O6 | §7 B6 / §8 path A's rollback command uses `psql … -1 < script.sql`, implying the whole multi-migration script runs as one atomic transaction — but the generated script already wraps EACH migration's `Down()` in its own `START TRANSACTION; … COMMIT;`, so `-1` adds nothing and misrepresents the atomicity actually available (LOW) | **Accepted**, task item 6. Dropped `-1` from both commands; both places now state plainly that each migration's `Down()` commits independently, describe the resulting half-rollback scenario (B-2 reverted, B-1 not) precisely, and give the recovery command (`dotnet ef database update <target-migration>`, never a hand-fix of the schema or `__EFMigrationsHistory`) | §7 B6, §8 path A |
| PB-G4 / PB-O7 | `SoftDeleteUniqueIndexTests.cs`'s `TestDb` bootstrap recreates `ux_users_email_live` via raw `ExecuteSqlRaw` (Sqlite `lower(email)` form), since the index is unmodelled in EF and `EnsureCreated()` skips it — Gemini: justified, keep as-is (NIT). Opus: justified, but share the DDL with the production migration via a constant if simple (LOW) | **Gemini's verdict accepted as-is; Opus's "share via a constant" rejected.** Sharing would mean retrofitting the frozen, historical migration `20260922205015_AddUsersEmailLiveUniqueIndex.cs` to reference a shared constant — historical migrations are never edited (R10) — and the two DDL strings are dialect-specific anyway (Postgres `IF NOT EXISTS` vs. plain Sqlite). Documented as an explicit, intentional deviation instead | §6 item 22 (new) |
| PB-G5 / PB-O8 | Test 5's local `Make(string email, Guid? ownerId, int roleId, …)` helper carries an unused `ownerId` parameter — `User.OwnerId` no longer exists (NIT) | **Accepted.** Removed the parameter and updated every call site (`a`–`j`, `h`, `i`) | `Tests/Db11fMembershipRulesTests.cs` (test 5) |
| PB-O9 | Stale comments describing dropped columns as if still present: `TenantService.cs:212-213`, `AuthService.cs:1451-1452` (both: "`users.owner_id ⇒ fk_users_workspaces_owner_id`"), `AuthService.cs:1218-1220` ("legacy users.role_id"), `AppDbContext.cs:107` region ("today's `users.owner_id` IS NULL set") (NIT) | **Accepted.** All four reworded to describe the CURRENT schema (no `owner_id` column at all) rather than a "legacy" column that still exists in Part A phrasing | `Application/Services/Implementation/TenantService.cs`, `Application/Services/Implementation/AuthService.cs` (×2), `Infrastructure/AppDbContext.cs` |
| PB-O10 | `SCHEMA.md`/`DB-REVIEW-2026-09-22.md` stamping is a deploy-time action, not a code-review fix (NIT) | **Deferred, not rejected.** Already covered by §9 Part B release step 8 ("Stamp docs… at actual deploy time"); nothing to change now because Part B has not deployed (PB-G1) | §9 step 8 (unchanged) |

**Test counts:** before this pass (`feat/db-11f-part-b` @ `395b158`, stashed working tree), `dotnet test` = **1519** green. After: **1524** green (+1 test 7 re-added, +4 new `PlatformRoleInvariant_*` tests for task item 4;
test 18's AdminSeeder-empty-DB leg was folded into the existing test method rather than a new `[Fact]`, so it adds no count on its own). All green before and after every individual fix (re-run per item, then the full suite
at the end); no regression anywhere else in the suite.
