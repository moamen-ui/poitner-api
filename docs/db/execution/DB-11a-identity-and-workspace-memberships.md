# DB-11a — One identity per e-mail, `workspace_memberships`, `user_aliases`

Review findings: S-7, S-13 (partly), S-14, R14. Owner requirements 2026-09-22 (verbatim intent):
"super admin can delete the user and the user can delete his account while the workspace admin can
move away the user out the workspace or disable/enable it but can't delete the user while it might
be in other workspaces. also what about if the current workspace admin joined in other workspaces?"
Rules: R1, R2, R3, R5, R7, R8, R9, R10, R11, R13, R14 (amended by this doc), R16 (new).
**Class: Expand** (two new tables, one nullable column, one index swap, an abort-guarded merge +
backfill, a new unique index, and the code switch of every per-workspace read from `users.owner_id`
/`users.role_id` to memberships). Three migrations, one release, explicit R7 deploy path. The old
columns `users.owner_id` and `users.role_id` **stay** and are still written at identity creation;
their removal is a later contract doc (DB-11e, not yet written; DB-11d is the change-e-mail code doc).
**Status 2026-09-22: written; not implemented.** Owner decisions D1–D7 and D13 have defaults (§3.9);
none blocks implementation, but the **production census (§9 step 1) must be pasted to the owner
before the deploy** because D1/D2 only matter if it shows duplicates.

**Status 2026-09-23: ✔ deployed to production 00:08 UTC** (`POINTER_APPLY_CONTRACT=1
POINTER_CONTRACT_LABEL=pre-db11a`, migrations 65→68), after a same-day fresh-dump rehearsal. Census
before the deploy: 0 duplicate e-mails → 0 merges, 0 aliases; 5 live memberships (6e4b3406:1,
98699076:4); comments 122 with 0 orphan authors; super admin untouched; both new unique indexes
present; active API keys 2→2. Reviewed by Gemini Pro and Opus 5 (both MERGE WITH FIXES); fixes
folded before merge — tenant admin routes now `/api/admin/tenants/{workspaceId:guid}` (+
`TenantResponse.WorkspaceId`), `api_keys` merge revokes losing duplicates first, super admins
excluded from the merge, identity `IsActive` guards restored on key/invite logins, membership
`mstamp` validated (`API/Auth/StampValidator.cs`). All pre-deploy sessions invalidated by design.
817 tests at merge. Client `@moamen-ui/pointer-react` 1.0.41 published from production; dashboard
TenantsPage update in progress.
**Amended 2026-09-22 (evening)** after the cross-review (`docs/roadmap/meetings/2026-09-22-foundations/04-chair-synthesis.md`
§1 rows D1/D7/D8 = GLM A1/A7/A8). Inside this doc the amendments are cited by the reviewer's finding id, because
D1–D13 here are **owner decisions** (§3.9): **GLM A1** the unique identity is enforced by the database on
`lower(email)` (expression index, raw SQL) and every write path goes through one `EmailNormalizer` (§3.3a);
**GLM A7** block 2.4 uses `DISTINCT ON` so a merged pair of soft-deleted rows yields one ended membership;
**GLM A8** a wrong password on a merged identity suggests "Forgot password" (§3.5 Login). Migration 3
therefore carries the `index change` marker too (§3.2, §4).

Staging: **DB-11a ships alone** (this doc) → [DB-11b](DB-11b-login-workspace-picker-and-switch.md)
(login picker, `switch-workspace`, `/me` memberships; code only) →
[DB-11c](DB-11c-deletion-semantics-remove-disable-erase.md) (remove/disable/leave/erase, sole-admin
guard, revoke-invite response; one additive column). Each later doc assumes this one is in
production.

## 1. Goal

Today a person is one `users` row **per workspace** (`ux_users_email_owner_live (email, owner_id)`,
`UserMapping.cs:26-30`). Accepting a second workspace's invite creates a second row with a new
`public_id` (`InviteService.cs:612-625`), password login picks an arbitrary one of them
(`AuthService.cs:241-247`, no `OrderBy`), and "delete the user" cannot mean anything coherent because
the same human is several rows. After this doc: **one `users` row per e-mail** (the identity),
**one `workspace_memberships` row per (identity, workspace)** carrying the role, `is_active`,
approval status and a per-membership session stamp, and a `user_aliases` table that maps every
`public_id` that ever existed to the identity it now belongs to — so every `author_id`, `actor_id`
and audit column written before the merge still resolves to a display name.

User-visible changes in this release: (1) a person with the same e-mail in two workspaces logs in
**once** and lands in their home workspace (the picker is DB-11b); (2) accepting an invite or signing
up for a new workspace with an e-mail that already exists asks for **that** account's password and
adds a membership instead of a second account; (3) an admin-set password via `PATCH /api/admin/users`
is refused when the person also belongs to another workspace; (4) the eleven `?? _currentUser.Id`
fallbacks are gone — a non-super-admin token without a `tenant` claim gets 403 instead of minting an
admin-less tenant (S-14). Every JWT keeps its shape (`sub`, `tenant`, `stamp`, …) plus one new claim
`mstamp`.

Answer to the owner's question ("what if the current workspace admin joined other workspaces?"):
one identity, several memberships, `Workspace Admin` in one of them (or several). Leaving, removal,
disable and erase are blocked wherever that identity is the **sole** Workspace Admin until
ownership is transferred (`POST /api/admin/users/{deputyPublicId}/promote`) — the guard is DB-11c;
this doc gives it the data to check. `workspaces.id` **no longer needs to equal anyone's
`public_id`**: from this doc on every mint point sets `Workspace.Id = Guid.NewGuid()` and the
founding admin is just the first membership.

## 2. Prerequisites (verified facts, 2026-09-22 @ `2756bd6`)

- 65 migrations; newest `20260922115038_DropUsersLegacyApiKey`. `just migrate name="…"` (`justfile:6`)
  = `dotnet ef migrations add … -p Infrastructure -s API`. Rehearsal: DB-RULES R11. Prod psql:
  `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer`.
- `users` today (`Infrastructure/Mappings/UserMapping.cs`): `public_id` unique (`:24`);
  `ux_users_email_owner_live` unique `(email, owner_id) WHERE deleted_at IS NULL`, `AreNullsDistinct(false)`
  (`:26-30`); `role_id → roles` Restrict (`:58-61`); `fk_users_workspaces_owner_id` Restrict (`:47-51`);
  `approval_status` default `ApprovalStatus.Approved` (`:37-39`; enum `Approved=1, Pending=2, Rejected=3`,
  `Domain/Enums/ApprovalStatus.cs`). Entity `Domain/Entity/User.cs` (fields incl. `SecurityStamp`,
  `IsDemo`, `ExpiresAt`, `RecipientEmail`, `PasswordlessOnly`).
- `api_keys`: `user_id → users.id` Cascade (`ApiKeyMapping.cs:38-41`) — the only `users.id` FK (R14);
  `ux_api_keys_active_per_user` unique `user_id WHERE revoked_at IS NULL AND deleted_at IS NULL`
  (`:50-53`); `owner_id` mirrors the user's (`ApiKeyService.cs:128 BuildRow`). `IApiKeyService`
  (`Application/Services/Interfaces/IApiKeyService.cs:17-26`): `GetOrCreateAsync(Guid publicId)`,
  `RegenerateAsync(Guid publicId)`, `ResolveAsync(string)`, `TouchLastUsedAsync(int)`.
- Every uuid column that holds a **user** `public_id` (from `AppDbContextModelSnapshot.cs`, verified
  today): `comments.author_id`, `comments.applied_by`, `comments.edited_by`, `replies.author_id`,
  `notifications.user_id`, `notifications.actor_id`, `ai_rules.user_id`, `predefined_actions.user_id`,
  `predefined_action_suggestions.reviewed_by`, `quick_access_links.user_id`, `device_logins.user_id`,
  `usage_events.user_id`, plus `created_by/updated_by/deleted_by` on all 24 `BaseEntity` tables and on
  `workspaces`. Uuid columns that are **not** users: every `owner_id`, `workspaces.id`,
  `users.public_id`, `users.security_stamp`. None of the user columns has an FK (R14).
- JWT: `Infrastructure/Auth/JwtTokenService.cs:14-40` — `Issue(User u, int? keyScopes)`; claims `sub`,
  `email`, `name`, `role_id`, `role`, `is_admin`, `is_super_admin`, `is_quick_access`, `stamp`,
  `tenant` (only when `u.OwnerId != null`), `key_scopes`. `ITokenService`
  (`Application/Abstractions/ITokenService.cs:13`). `HttpCurrentUser` reads `sub`, `is_admin`,
  `is_super_admin`, `is_quick_access`, `tenant`, `role_id` (`Infrastructure/CurrentUser/HttpCurrentUser.cs`).
  Callers of `.Issue(`: `AuthService.cs:280,327,624`, `DemoService.cs:208,338`, `InviteService.cs:641,776`.
- Stamp validation: `API/Extensions/AuthenticationExtensions.cs:55-105` — `OnTokenValidated` only when
  `Auth:ValidateSecurityStamp` (prod `true`, `docker-compose.prod.yml:35`); cache key
  `secstamp:{publicId}` 60 s (`:76-86`); fail-open on exception (`:88-97`); rejects when stamp is null
  or differs (`:101-102`).
- Query filters (`Infrastructure/AppDbContext.cs`): `User` strict-own `:91-96`; `ApiKey` `:99-104`;
  `Workspace` `:289-293`; `DeviceLogin` exempt (`:280-284` comment). Audit stamping
  `SaveChangesAsync` `:304-333` (`uid = currentUser.Id ?? Guid.Empty`).
- `TenantStamp.OwnerFor` (`Application/Common/TenantStamp.cs:11`). **S-14 sites** (`?? _currentUser.Id`
  or `?? Guid.Empty` after `OwnerFor`): `PredefinedActionService.cs:53,93`, `CommentFieldService.cs:255,273`,
  `WorkspaceService.cs:38,61`, `ProjectService.cs:220,298,763`, `AiRuleService.cs:167`,
  `UserService.cs:114`, `InviteService.cs:124,438`, `EntitlementService.cs:77`. Check:
  `grep -rn '?? _currentUser.Id' Application --include='*.cs' | wc -l` → 13 lines (two are comments at
  `ProjectService.cs:217` and `UserService.cs:111`).
- Role-name sites (`"Workspace Admin"` / `WorkspaceAdminRoleName`): `UserService.cs:51,82,274,306,325,354,379`,
  `TenantService.cs:56,136,191,203,272,383,450`, `InviteService.cs:75,97,653,682`, `AuthService.cs:473,480`,
  `DemoService.cs:93,98`, `API/Seed/AdminSeeder.cs:19`, `CreateInviteRequest.cs:28` (doc-comment).
- Every `Repository<User>()` / `db.Users` read site (this doc touches all of them; the list is the
  §5 checklist): `AuthService.cs:55,122,141,155,170,241,395,418,434,461,509,557,599`;
  `UserService.cs:46,64,122,142,150,169,188,216,226,251,297,314,330,344,359,389,393,403,404`;
  `TenantService.cs:51,180,235,262,293,303,326,350,361,374,434`;
  `InviteService.cs:90,177,571,584,629,663,722,821,832,887`; `DemoService.cs:83,138,272,295,326`;
  `ApiKeyService.cs:97`; `DeviceLoginService.cs:134`; `ProfileService.cs:25,35`; `PreferencesService.cs:36,53`;
  `CommentService.cs:127,1111`; `ProjectService.cs:1181`; `ExportImportService.cs:527,567`;
  `SuggestionService.cs:342,409`; `AiRuleService.cs:394,559`; `RoleService.cs:232,274`;
  `StatsService.cs:29,34`; `PlatformInsightsService.cs:43`; `API/Seed/AdminSeeder.cs:137,143,160,254`;
  `API/Hosted/DemoCleanupService.cs:42`; `API/Extensions/AuthenticationExtensions.cs:80`.
- Mint points (each `new User { … OwnerId = publicId }` + `Workspaces.AddAsync(new Workspace { Id = publicId … })`):
  `AuthService.RegisterAdminAsync` `:486-507`, `TenantService.CreateAsync` `:212-235`,
  `InviteService.AcceptCreateNewWorkspaceAsync` `:693-724`, `DemoService.ProvisionAsync` `:110-140`.
  Stakeholder/other user creation: `AuthService.RegisterAsync` `:407-421`, `UserService.CreateAsync`
  `:133-145`, `InviteService.AcceptJoinExistingWorkspaceAsync` `:612-640`,
  `InviteService.CreateQuickAccessInviteAsync` `:855-866`.
- Hard delete: `TenantService.HardDeleteAsync(Guid tenantId)` `:430-505` (22 `DeleteOwnedAsync<T>` calls
  `:463-484`, then the `workspaces` row `:487-492`); `HardDeleteOrder` array feeds
  `Tests/WorkspaceTests.cs` `HardDeleteOrder_CoversEveryOwnerCarryingEntity` (22 entries). Caller
  `API/Hosted/DemoCleanupService.cs:42-52,78` passes the demo admin's `PublicId`.
- Notification creation: `NotificationService.EnqueueAsync` (`NotificationService.cs:25-31`); callers
  `CommentService.cs:672,784,967`, `SuggestionService.cs:219,384`. Device approval:
  `DeviceLoginService.ApproveAsync` `:183-207` (no `IsActive` check today; stamps `OwnerId = _currentUser.TenantId`).
- Name resolution from `public_id` (five copies of the same 6-line query): `CommentService.cs:1105-1116`,
  `ProjectService.cs:1175-1186`, `ExportImportService.cs:565-570`, `SuggestionService.cs:405-414`,
  `AiRuleService.cs:393-400,558-565`. Precedent for a static resolver: `WorkspaceNameResolver.cs`.
- DB-02 guard (`Tests/MigrationSafetyTests.cs:75-82,158-172`): risky ops regex includes `DropIndex`
  and `.Sql(`; marker regex `// DB-RULES: (R2 contract|R3 backfill|index change|R4 constraint) approved yyyy-mm-dd by <non-space>`;
  marker ⇔ `[ContractMigration("…")]` (`Infrastructure/Migrations/ContractMigrationAttribute.cs`).
  Because the regex matches `.Sql(` inside `Up()`, **any migration whose `Up()` calls `migrationBuilder.Sql`
  needs a marker + attribute** — Migration 3 included (GLM A1).
- **E-mail normalisation today (GLM A1):** ten hand-written `.Trim().ToLower()` calls and no shared routine —
  `AuthService.cs:50,236,344,454`, `UserService.cs:62`, `DemoService.cs:267`, `InviteService.cs:154,518,799`,
  `API/Seed/AdminSeeder.cs:128` (and `:139` compares `u.Email.ToLower() == adminEmail`). Static-normaliser
  precedent: `Application/Common/OriginNormalizer.cs`. `UserMapping.cs:26-30` is the only e-mail index;
  EF Core 8 `HasIndex` has no expression-index API, so an index on `lower(email)` is raw SQL and is **not**
  in the model (`AppDbContextModelSnapshot.cs` does not change for it). DB-10 CI
  (`.github/workflows/db-migrations.yml:46-58`) applies every migration from empty, round-trips the newest
  `Down()`/`Up()`, and has `psql` on the runner (`:51`).
- Test fixtures: InMemory `AppDbContext` + `FakeCurrentUser` (`Tests/WorkspaceAdminOwnershipTests.cs:24-46`,
  `UserGovernanceTests.cs:20-60` with `IdentityHasher`, `NoopEmail`, `NoopBrandingService`); Sqlite `TestDb`
  (`Tests/UsageEventFirstCommentTests.cs:43-62`; `SqliteBtrimFunctionInterceptor` in `Tests/TestDoubles.cs:40`);
  tenancy tests `Tests/TenantQueryFilterTests.cs`. 26 test files build `new User { … }` and 17 seed
  `OwnerId =` on users — every one that then calls a service listed in §5 needs a membership (§6).
- Dashboard reads `/api/auth/me` (`tenantName`, `isAdmin`, `isSuperAdmin`; `../pointer-dashboard/react/src/features/shell/Shell.tsx:82-89`),
  users page uses `PATCH isActive` and `DELETE` (`features/users/UsersPage.tsx:251-260,478-480`).
  Widget: `web-component/src/element.ts:921` (`/api/auth/login`), `auth-ui.ts:79-108` (status
  branches `ok|pending|disabled|rejected`), `element.ts:2587-2600` (`/api/auth/me`). CLI:
  `cli/src/commands/login.ts:45-50`, `whoami.ts:35-37` (`login-with-key` then `/api/auth/me`).

## 3. Design

### 3.1 Entities and tables

**`workspace_memberships`** — `Domain/Entity/WorkspaceMembership.cs : BaseEntity`

| property | column | type | null | default / notes |
|---|---|---|---|---|
| `UserId` / `User` | `user_id` | `int` FK `users(id)` **Restrict** | no | the identity. Structural child of `users` → int FK, like `api_keys` (R14 amended) |
| `OwnerId` | `owner_id` | `uuid` FK `workspaces(id)` Restrict, `fk_workspace_memberships_workspaces_owner_id` | no | **the workspace**. Named `OwnerId` so R8 (filter shape, `HardDeleteOrder` reflection test, `TenantStamp`) applies unchanged |
| `RoleId` / `Role` | `role_id` | `int` FK `roles(id)` Restrict | no | the role **in this workspace** |
| `IsActive` | `is_active` | `bool` | no | `true`. Disable/enable lives here |
| `ApprovalStatus` | `approval_status` | `int` | no | `HasDefaultValue(ApprovalStatus.Approved)` — approval is per workspace (stakeholder self-signup is pending **in that workspace**) |
| `SecurityStamp` | `security_stamp` | `uuid` | no | `Guid.NewGuid()` in C#; JWT `mstamp`. Rotated on role change / disable / removal → revokes **this workspace's** sessions only |
| `JoinedAt` | `joined_at` | `timestamptz` | no | set by code |
| `LeftAt` | `left_at` | `timestamptz` | yes | non-null = ended membership (removed / left / erased). Row is kept for audit |
| `LeftReason` | `left_reason` | `int` | yes | enum `MembershipEndReason { Removed = 1, Left = 2, AccountErased = 3 }` (`Domain/Enums/MembershipEndReason.cs`; append-only, R10) |
| `InviteId` | `invite_id` | `int` FK `invites(id)` **SetNull** | yes | which invite created it (DB-11c's "also disable the invitee" needs it) |
| audit | `created_at/…/deleted_by` | | | as every mapping. `DeletedAt` is reserved for hard-delete paths only; **ending a membership sets `LeftAt`, never `DeletedAt`** |

Indexes: `ux_workspace_memberships_user_workspace_live` unique `(user_id, owner_id)`
`.HasFilter("left_at IS NULL AND deleted_at IS NULL")` (R9 — re-joining after removal inserts a new
row); `ix_workspace_memberships_owner_role` `(owner_id, role_id)` (admin lookups; owner-leading, so
EF adds no `IX_…_owner_id`). EF adds `IX_workspace_memberships_user_id`, `IX_workspace_memberships_role_id`,
`IX_workspace_memberships_invite_id` — expected. Query filter: strict-own on `OwnerId`, copy of
`Project` (`AppDbContext.cs:85-90`). Add to `HardDeleteOrder` and to `HardDeleteAsync` (§3.6).

**`user_aliases`** — `Domain/Entity/UserAlias.cs` (**not** a `BaseEntity`; Guid PK — same reasoning as `Workspace`)

| property | column | type | null | notes |
|---|---|---|---|---|
| `AliasPublicId` | `alias_public_id` | `uuid` PK, `ValueGeneratedNever()` | no | a `public_id` that existed before the merge |
| `UserId` / `User` | `user_id` | `int` FK `users(id)` **Cascade** | no | the identity it now belongs to |
| `SourceWorkspaceId` | `source_workspace_id` | `uuid` | yes | informational; **no FK** (the workspace may be hard-deleted later) |
| `MergedAt` | `merged_at` | `timestamptz` | no | |

No query filter (mapping comment: "lookup table keyed by a uuid the caller already holds; joined to
`users`, which is filtered"). `DbSet<UserAlias> UserAliases` on `AppDbContext`, `IUnitOfWork`,
`UnitOfWork` — same shape as `Workspaces` (`IUnitOfWork.cs:10`, `UnitOfWork.cs:21`).

**`users`** — one new column: `MergedIntoUserId` / `merged_into_user_id int NULL`, FK `users(id)`
Restrict, `fk_users_merged_into_user`. Non-null ⇔ this row was merged by §3.3 and is soft-deleted.
New navigation `ICollection<WorkspaceMembership> Memberships`. New unique index (Migration 3, **GLM A1**):
`ux_users_email_live` — `CREATE UNIQUE INDEX … ON users (lower(email)) WHERE deleted_at IS NULL`, an
**expression** index written with `migrationBuilder.Sql` because EF cannot express `lower()` in
`HasIndex`. The EF model keeps **no** index for it; `UserMapping.cs` gets a two-line comment above the
`Email` property ("`ux_users_email_live` on `lower(email)` is raw SQL in migration
`*_AddUsersEmailLiveUniqueIndex`; not modelled — DB-11a GLM A1") so nobody "fixes" the snapshot. The DB-10 CI
job applies it from empty and round-trips its `Down()`/`Up()`, which is the mechanical check that the raw
SQL and the model agree. Application code still normalises (§3.3a) so equality lookups hit the row; the
database is the authority. **Keep** `ux_users_email_owner_live`
and the `(Email, OwnerId)` mapping lines — dropping them is the contract doc's job. New doc-comments
on `OwnerId` and `RoleId`: "**Legacy (DB-11a).** Written once at identity creation (first workspace /
super-admin role); never read by application code after DB-11a. Dropped by DB-11e." On `IsActive`:
"Identity-level switch (false only after erase/merge). Per-workspace enable/disable is
`WorkspaceMembership.IsActive`." On `ApprovalStatus`: "Legacy for non-super-admins; per-workspace
approval is `WorkspaceMembership.ApprovalStatus`."

**`api_keys`** — keys are **per membership** (owner decision D3, default yes): a key belongs to
(identity, workspace). `owner_id` already exists and already equals the workspace. Replace
`ux_api_keys_active_per_user` with `ux_api_keys_active_per_membership` unique `(user_id, owner_id)`
`WHERE revoked_at IS NULL AND deleted_at IS NULL` (`.AreNullsDistinct(false)` so a super admin's
null-owner key is also unique). Why per membership: `login-with-key` must land the CLI in one
workspace without a picker (agents are non-interactive); removing a person from workspace A must
revoke A's key without touching their key for B; device-login approval happens inside a dashboard
session that already has a `tenant`, so the minted key is naturally that workspace's.

**`device_logins`, `quick_access_links`, `notifications`** — unchanged schema. `user_id` = identity
`public_id`; `owner_id` = workspace; both are therefore per membership already.

**Query filter on `User`** (replaces `AppDbContext.cs:91-96`):
```csharp
b.Entity<User>()
    .HasQueryFilter(e =>
        currentUser.IsSuperAdmin
        || (currentUser.TenantId != null && e.Memberships.Any(m => m.OwnerId == currentUser.TenantId))
        || (currentUser.TenantId == null && !strict && e.OwnerId == null)
    );
```
Any membership counts, **including ended ones** — a person who left still authored comments the
workspace can see, and their name must resolve. Listing "current members" always filters on
`LeftAt == null` explicitly (§3.5).

### 3.2 Migrations (three files, this order)

**Migration 1 — `AddWorkspaceMembershipsAndUserAliases`** (scaffolded). Expected `Up()`, nothing else:
`CreateTable("workspace_memberships", …)` with the four FKs; `CreateTable("user_aliases", …)`;
`AddColumn("merged_into_user_id", "users", nullable)` + `AddForeignKey("fk_users_merged_into_user")`;
`CreateIndex` × 5 for memberships (`ux_…_live` with the filter, `ix_…_owner_role`, `IX_…_user_id`,
`IX_…_role_id`, `IX_…_invite_id`), `IX_user_aliases_user_id`, `IX_users_merged_into_user_id`;
**`DropIndex("ux_api_keys_active_per_user")` + `CreateIndex("ux_api_keys_active_per_membership", …)`**.
Because of the `DropIndex`: `[ContractMigration("DB-11a")]` two lines above the class and, on the line
above `Up(`, verbatim:
`// DB-RULES: index change approved 2026-09-22 by Moamen (owner; requirement "one identity, memberships per workspace, keys per membership", relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)`
`Down()` is the generated inverse (drops both tables, the column, restores the old key index).
**GLM A1:** the model carries **no** `HasIndex` for `ux_users_email_live`, so this scaffold must contain no
index on `users.email` other than the existing `ux_users_email_owner_live`; if it does, a mapping edit
leaked — stop and report.

**Migration 2 — `MergeSameEmailIdentitiesAndBackfillMemberships`** (scaffolded with **no** model change →
empty `Up()`/`Down()`; fill `Up()` by hand with the SQL blocks below, in order, one `migrationBuilder.Sql(@"…")`
each; `Down()` stays **empty** — see §8). `[ContractMigration("DB-11a")]` + marker verbatim:
`// DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; decisions D1 newest password wins, D2 admin-preferring canonical, relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)`

```sql
-- 2.1 Who merges into whom. Only LIVE rows (deleted_at IS NULL) are considered. Canonical per
--     lower(email): a 'Workspace Admin' row first, then a Deputy, then the oldest row (D2).
--     Empty when the census (§9 step 1) shows no duplicates — every later block is then a no-op.
CREATE TEMP TABLE db11_merge AS
SELECT u.id AS merged_id, u.public_id AS merged_public_id, u.owner_id AS merged_owner_id,
       c.id AS canonical_id
FROM users u
JOIN LATERAL (
  SELECT c.id
  FROM users c JOIN roles r ON r.id = c.role_id
  WHERE c.deleted_at IS NULL AND lower(c.email) = lower(u.email)
  ORDER BY (r.name = 'Workspace Admin') DESC, (r.name = 'Workspace Admin Deputy') DESC, c.created_at, c.id
  LIMIT 1
) c ON true
WHERE u.deleted_at IS NULL AND u.id <> c.id;
```
```sql
-- 2.2 D1: the password of the most recently written duplicate wins; the canonical row's sessions
--     are revoked (new stamp). A passwordless (magic-link-only) row never donates its hash.
UPDATE users c
SET password_hash = n.password_hash, passwordless_only = false, security_stamp = gen_random_uuid()
FROM (
  SELECT DISTINCT ON (m.canonical_id) m.canonical_id, x.password_hash
  FROM (SELECT DISTINCT canonical_id FROM db11_merge) m
  JOIN users k ON k.id = m.canonical_id
  JOIN users x ON x.deleted_at IS NULL AND lower(x.email) = lower(k.email) AND NOT x.passwordless_only
  ORDER BY m.canonical_id, coalesce(x.updated_at, x.created_at) DESC, x.id DESC
) n
WHERE c.id = n.canonical_id AND c.password_hash <> n.password_hash;
```
```sql
-- 2.3 Aliases: every merged public_id → its identity. Idempotent.
INSERT INTO user_aliases (alias_public_id, user_id, source_workspace_id, merged_at)
SELECT merged_public_id, canonical_id, merged_owner_id, now() FROM db11_merge
ON CONFLICT (alias_public_id) DO NOTHING;
```
```sql
-- 2.4 Memberships for EVERY workspace-scoped users row (owner_id IS NOT NULL), live or soft-deleted,
--     pointing at the canonical identity. A soft-deleted row becomes an ENDED membership
--     (left_at = deleted_at, reason Removed=1). Runs BEFORE 2.6 so merged rows are still live here.
--     NOT EXISTS makes a second run a no-op. created_by = the all-zero uuid (system).
--     GLM A7: DISTINCT ON (identity, workspace, live?) — several pre-merge rows of the same person
--     in the same workspace (two soft-deleted rows; or a live pair differing only in e-mail case, which
--     ux_users_email_owner_live did not catch) collapse to ONE membership per (identity, workspace, live?).
--     Live partition: the canonical row's own membership wins, else the oldest. Ended partition: the most
--     recently ended row wins. Without this the live pair would violate ux_workspace_memberships_user_workspace_live
--     and the migration would fail with a unique-violation instead of a clean ABORT.
INSERT INTO workspace_memberships
  (user_id, owner_id, role_id, is_active, approval_status, security_stamp, joined_at, left_at, left_reason, invite_id, created_at, created_by)
SELECT DISTINCT ON (coalesce(m.canonical_id, u.id), u.owner_id, (u.deleted_at IS NULL))
       coalesce(m.canonical_id, u.id), u.owner_id, u.role_id,
       (u.is_active AND u.deleted_at IS NULL), u.approval_status, gen_random_uuid(), u.created_at,
       CASE WHEN u.deleted_at IS NOT NULL THEN u.deleted_at END,
       CASE WHEN u.deleted_at IS NOT NULL THEN 1 END,
       NULL, now(), '00000000-0000-0000-0000-000000000000'
FROM users u LEFT JOIN db11_merge m ON m.merged_id = u.id
WHERE u.owner_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM workspace_memberships w
                  WHERE w.user_id = coalesce(m.canonical_id, u.id) AND w.owner_id = u.owner_id
                    AND w.deleted_at IS NULL
                    AND (w.left_at IS NULL) = (u.deleted_at IS NULL))
ORDER BY coalesce(m.canonical_id, u.id), u.owner_id, (u.deleted_at IS NULL),
         (u.id = coalesce(m.canonical_id, u.id)) DESC, u.deleted_at DESC NULLS LAST, u.created_at, u.id;
```
```sql
-- 2.4b invite_id for quick-access memberships (the only ones we can attribute today).
UPDATE workspace_memberships m SET invite_id = q.invite_id
FROM quick_access_links q JOIN users u ON u.public_id = q.user_id
WHERE m.invite_id IS NULL AND m.owner_id = q.owner_id
  AND m.user_id = coalesce(u.merged_into_user_id, (SELECT canonical_id FROM db11_merge d WHERE d.merged_id = u.id), u.id);
```
```sql
-- 2.5 Rewrite every uuid reference to a merged public_id → the canonical public_id. Generic over
--     information_schema so no user column is missed; excludes owner_id (workspace ids),
--     workspaces.id, users.public_id/security_stamp, workspace_memberships.security_stamp and the
--     alias table itself. Expected to touch exactly the columns listed in §2 ("Every uuid column").
--     Also api_keys.user_id (int). Idempotent: after one run nothing matches an alias.
DO $$
DECLARE t record;
BEGIN
  FOR t IN
    SELECT c.table_name, c.column_name
    FROM information_schema.columns c
    WHERE c.table_schema = 'public' AND c.udt_name = 'uuid'
      AND c.table_name NOT IN ('user_aliases', '__EFMigrationsHistory')
      AND c.column_name <> 'owner_id'
      AND NOT (c.table_name = 'users' AND c.column_name IN ('public_id', 'security_stamp'))
      AND NOT (c.table_name = 'workspaces' AND c.column_name = 'id')
      AND NOT (c.table_name = 'workspace_memberships' AND c.column_name = 'security_stamp')
  LOOP
    EXECUTE format(
      'UPDATE %I t SET %I = c.public_id FROM user_aliases a JOIN users c ON c.id = a.user_id WHERE t.%I = a.alias_public_id',
      t.table_name, t.column_name, t.column_name);
  END LOOP;
END $$;
UPDATE api_keys k SET user_id = m.canonical_id FROM db11_merge m WHERE k.user_id = m.merged_id;
```
```sql
-- 2.6 Retire the merged rows: soft-delete, mark merged_into, kill sessions. Then normalise e-mails
--     to lower-case on live rows (safe now: duplicates are soft-deleted).
UPDATE users u
SET merged_into_user_id = m.canonical_id, deleted_at = now(),
    deleted_by = '00000000-0000-0000-0000-000000000000', is_active = false, security_stamp = gen_random_uuid()
FROM db11_merge m WHERE u.id = m.merged_id AND u.merged_into_user_id IS NULL;
UPDATE users SET email = lower(email) WHERE deleted_at IS NULL AND email <> lower(email);
```
```sql
-- 2.7 ABORT if the invariants do not hold (rolls back this migration; Migration 3 never runs).
DO $$
DECLARE dupes text; missing bigint;
BEGIN
  SELECT string_agg(min_id::text, ', ') INTO dupes
  FROM (SELECT min(id) AS min_id FROM users WHERE deleted_at IS NULL GROUP BY lower(email) HAVING count(*) > 1) d;
  IF dupes IS NOT NULL THEN
    RAISE EXCEPTION 'DB-11a ABORT: live duplicate e-mails remain after merge (users.id): %', dupes;
  END IF;
  SELECT count(*) INTO missing FROM users u
  WHERE u.owner_id IS NOT NULL AND u.deleted_at IS NULL
    AND NOT EXISTS (SELECT 1 FROM workspace_memberships w WHERE w.user_id = u.id AND w.owner_id = u.owner_id AND w.left_at IS NULL AND w.deleted_at IS NULL);
  IF missing > 0 THEN
    RAISE EXCEPTION 'DB-11a ABORT: % live workspace users have no live membership', missing;
  END IF;
END $$;
DROP TABLE IF EXISTS db11_merge;
```

**Migration 3 — `AddUsersEmailLiveUniqueIndex`** (**GLM A1**; scaffolded with **no** model change → empty
`Up()`/`Down()`, filled by hand). `Up()` is exactly one call:
```csharp
migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email_live ON users (lower(email)) WHERE deleted_at IS NULL;");
```
`Down()` is exactly one call: `migrationBuilder.Sql("DROP INDEX IF EXISTS ux_users_email_live;");`.
Plain (not `CONCURRENTLY`): `users` is far below the R4 threshold. Because `Up()` calls `.Sql(`, the
DB-02 guard requires `[ContractMigration("DB-11a")]` on the class and, on the line above `Up(`, verbatim:
`// DB-RULES: index change approved 2026-09-22 by Moamen (owner; requirement "one identity per e-mail", relayed by the orchestrator; docs/db/execution/DB-11a-identity-and-workspace-memberships.md)`
It runs only if 2.7 did not abort, so `GROUP BY lower(email)` duplicates are already known to be zero.

**Every existing row, after the three migrations:** `users` — unchanged except: merged duplicates
(if any) are soft-deleted with `merged_into_user_id` set; the canonical row may have a new
`password_hash`/`security_stamp` (D1) and a lower-cased e-mail. Every other table — unchanged except
uuid columns that pointed at a merged `public_id` now point at the canonical one (and `user_aliases`
records the old value forever). `api_keys` rows keep `owner_id`; the merged row's keys now belong
to the canonical identity for that workspace. One membership row exists per (identity, workspace)
that ever had a `users` row. NULL-owner (super admin) rows: untouched, no membership.

Rehearsal verification (R11 step 4), run after `dotnet ef database update`:
```sql
SELECT lower(email), count(*) FROM users WHERE deleted_at IS NULL GROUP BY 1 HAVING count(*) > 1;    -- 0 rows
SELECT count(*) FROM users WHERE merged_into_user_id IS NOT NULL;                                     -- M (= census duplicates)
SELECT count(*) FROM user_aliases;                                                                    -- M
SELECT count(*) FROM workspace_memberships;                                                           -- = SELECT count(*) FROM users WHERE owner_id IS NOT NULL
SELECT count(*) FROM workspace_memberships WHERE left_at IS NULL;                                     -- = live users with owner_id before the merge
SELECT count(*) FROM comments c LEFT JOIN users u ON u.public_id = c.author_id WHERE u.id IS NULL;   -- 0 (every author_id resolves)
SELECT count(*) FROM replies r  LEFT JOIN users u ON u.public_id = r.author_id WHERE u.id IS NULL;   -- 0
SELECT count(*) FROM comments c JOIN user_aliases a ON a.alias_public_id = c.author_id;              -- 0 (rewritten)
SELECT w.id, w.name, count(m.*) FILTER (WHERE m.left_at IS NULL AND r.name = 'Workspace Admin') AS live_admins
  FROM workspaces w LEFT JOIN workspace_memberships m ON m.owner_id = w.id LEFT JOIN roles r ON r.id = m.role_id
  GROUP BY w.id, w.name;                                                                              -- informational: 0 live_admins = admin-less (owner question, not an abort)
SELECT indexname FROM pg_indexes WHERE tablename = 'api_keys' AND indexname LIKE 'ux_api_keys_active%';  -- ux_api_keys_active_per_membership only
SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_users_email_live';                                -- … UNIQUE INDEX … ON public.users USING btree (lower((email)::text)) WHERE (deleted_at IS NULL)
```
GLM A1 negative check on the rehearsal copy (must **fail** with `duplicate key value violates unique constraint "ux_users_email_live"`;
wrap in a transaction and roll back):
```sql
BEGIN;
INSERT INTO users (public_id, email, password_hash, display_name, role_id, is_active, approval_status, security_stamp, is_demo, "DemoExtended", created_at, created_by)
SELECT gen_random_uuid(), upper(email), password_hash, 'dup', role_id, true, 1, gen_random_uuid(), false, false, now(), '00000000-0000-0000-0000-000000000000'
FROM users WHERE deleted_at IS NULL LIMIT 1;   -- expected: ERROR … ux_users_email_live
ROLLBACK;
```

### 3.3 Identity creation and "join" — the shared routine

New `Application/Services/Implementation/MembershipService.cs : IMembershipService` (Scrutor
auto-registers by the `Service` suffix, `Application/DependencyInjection.cs:11-12`). Methods (all
`IgnoreQueryFilters()` inside — they run on anonymous paths or are scoped by an explicit workspace id):

```csharp
Task<User?> FindIdentityByEmailAsync(string email);                        // live row, Include(Role); applies EmailNormalizer.Normalize itself (GLM A1) — callers may pass raw input
Task<User?> FindIdentityByPublicIdAsync(Guid publicId);                    // live row, Include(Role)
Task<WorkspaceMembership?> GetMembershipAsync(int userId, Guid workspaceId); // live (LeftAt == null, DeletedAt == null), Include(Role)
Task<List<WorkspaceMembership>> ListForIdentityAsync(int userId);          // live memberships, Include(Role), workspaces not deleted
IQueryable<WorkspaceMembership> InWorkspace(Guid workspaceId);             // IgnoreQueryFilters().Where(m => m.OwnerId == workspaceId && m.DeletedAt == null).Include(m => m.User).Include(m => m.Role)
Task<WorkspaceMembership?> CurrentAdminAsync(Guid workspaceId);            // InWorkspace + LeftAt == null + Role.Name == "Workspace Admin"
Task<WorkspaceMembership> JoinAsync(User identity, Guid workspaceId, Role role, ApprovalStatus status, bool isActive, int? inviteId); // builds the row (JoinedAt = UtcNow, SecurityStamp = NewGuid), AddAsync; does NOT SaveChanges
User NewIdentity(string email, string passwordHash, string displayName, Role firstRole, Guid firstWorkspaceId, bool passwordlessOnly = false); // PublicId = NewGuid; Email = EmailNormalizer.Normalize(email) (GLM A1); legacy OwnerId = firstWorkspaceId, RoleId = firstRole.Id (dual-write, never read)
```
Why `InWorkspace` uses `IgnoreQueryFilters` **and** an explicit `OwnerId` predicate: an `Include(m => m.User)`
through the filtered `User` set is exactly the kind of navigation filter EF applies inconsistently;
the explicit predicate is the isolation, and §6 test 2 proves tenant B gets nothing.

**Join-or-create rule** (used by every path that today creates a `User` for a workspace):
1. `identity = FindIdentityByEmailAsync(email)`.
2. If `identity == null` → `NewIdentity(...)` + `JoinAsync(...)` (+ `Workspaces.AddAsync` at mint points).
3. If `identity != null` and the caller is **anonymous** (register, accept invite, quick-access is
   admin-driven so skip) → the request's password must verify against `identity.PasswordHash`
   (D4/D5, default yes); a `PasswordlessOnly` identity or a wrong password → `Conflict(MessageKeys.Auth.AccountExists)`
   (same message as today — reveals nothing new).
4. If a **live** membership already exists in that workspace → `Conflict(MessageKeys.Auth.AccountExists)`
   (or the path's existing conflict message).
5. Else `JoinAsync(identity, workspaceId, role, status, isActive, inviteId)`. The identity's
   `DisplayName`, `PasswordHash`, preferences are **not** changed by a join.

### 3.3a E-mail normalisation — one routine (GLM A1)

New `Application/Common/EmailNormalizer.cs` (precedent for a static normaliser: `OriginNormalizer.cs`):
```csharp
namespace Pointer.Application.Common;

/// <summary>
/// The ONLY e-mail normalisation in the codebase (DB-11a, GLM A1). The database enforces one live identity per
/// <c>lower(email)</c> (<c>ux_users_email_live</c>, expression index); this routine exists so every equality
/// lookup finds that row and every write stores the same shape. Trim + lower-invariant. Returns null for
/// null/whitespace so optional fields (invite e-mail lock) stay null. Never add a second normaliser —
/// the DB-11a acceptance criteria grep for stray <c>.Trim().ToLower()</c> on e-mails.
/// </summary>
public static class EmailNormalizer
{
    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>For required fields: normalises or returns "" (so validators, not this class, decide emptiness).</summary>
    public static string NormalizeRequired(string? email) => Normalize(email) ?? string.Empty;
}
```
Every site listed in §2 "E-mail normalisation today" replaces its hand-written expression with
`EmailNormalizer.NormalizeRequired(request.Email)` (required fields) or `EmailNormalizer.Normalize(request.Email)`
(nullable: `InviteService.cs:154,799`). `MembershipService.FindIdentityByEmailAsync` and `NewIdentity`
normalise internally as well (double normalisation is idempotent). `AdminSeeder.cs:128` uses
`NormalizeRequired`; `:139` may keep `u.Email.ToLower() ==` (after 2.6 every live e-mail is already
lower-case, so the comparison is redundant but harmless). Postgres `lower()` and .NET `ToLowerInvariant()`
agree on every ASCII address; for the rare non-ASCII local part the database wins (a second identity
cannot be inserted), and the app-level lookup miss surfaces as `AccountExists`/`EmailTaken`, never as a
duplicate.

### 3.4 JWT, current user, stamp validation

- `ITokenService.Issue(User user, WorkspaceMembership? membership, int? keyScopes = null)`. Role source:
  `membership?.Role ?? user.Role`. `tenant` = `membership?.OwnerId` (absent for super admins). New claim
  `mstamp` = `membership.SecurityStamp` when a membership is present. `role_id` = the same role's id.
  Everything else unchanged. Update the seven callers (§2) to pass the membership they just resolved
  (super-admin and demo-upgrade paths pass what they have; `DemoService.cs:208` passes the demo admin membership).
- `HttpCurrentUser`: unchanged (no code reads `mstamp` except the validator).
- `AuthenticationExtensions.OnTokenValidated` (inside the existing `validateStamp` block): cache key
  becomes `$"secstamp:{publicId}:{tenantClaim ?? "-"}"`; the lookup returns an anonymous
  `{ UserStamp, MembershipStamp, MembershipLive }` where, when the `tenant` claim is present,
  `MembershipStamp`/`MembershipLive` come from
  `db.Set<WorkspaceMembership>().IgnoreQueryFilters().Where(m => m.User.PublicId == publicId && m.OwnerId == tenant && m.DeletedAt == null && m.LeftAt == null).Select(m => new { m.SecurityStamp, Live = m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved })`.
  Reject (`ctx.Fail("Token has been revoked.")`) when: user stamp null/mismatch (as today), **or** a
  `tenant` claim is present and (no live membership row, or `!Live`, or `mstamp` claim missing/mismatch).
  Fail-open behaviour on exceptions unchanged. Consequence: disable/removal takes effect within 60 s
  for that workspace's tokens only; other workspaces' tokens keep working.

### 3.5 Every per-workspace behaviour, mapped

| Behaviour | Today | After DB-11a |
|---|---|---|
| Tenant query filters | `owner_id == tenant` on 23 tables | unchanged; `workspace_memberships` joins them with the same shape; `User` filter becomes membership-based (§3.1) |
| JWT `tenant` | `users.owner_id` | the chosen membership's `OwnerId`; chosen at login (§3.5 Login) or, in DB-11b, via `switch-workspace` |
| Password login (`AuthService.LoginAsync`) | arbitrary row by email | identity by email → password → `PasswordlessOnly` → if `Role.IsSuperAdmin`: as today (identity `ApprovalStatus`/`IsActive`). Else `ListForIdentityAsync` → candidates = `IsActive && Approved && identity.IsActive`; **none**: any `Pending` → `"pending"`, else any `Rejected` → `"rejected"`, else `"disabled"` (DB-11b adds `"no-workspace"`); **one or more**: pick the one whose `OwnerId == identity.OwnerId` (home) if present, else the earliest `JoinedAt` → `Issue(identity, membership)` (DB-11b replaces the pick with the picker). **GLM A8:** when the identity exists but the password does not verify, and `Users.IgnoreQueryFilters().Any(u => u.MergedIntoUserId == identity.Id)`, return `Failure(MessageKeys.Auth.InvalidCredentialsAfterMerge)` instead of `InvalidCredentials` — the person whose password lost in 2.2 is told to use "Forgot password". Same status code and shape; the only difference is the message. (It reveals that the address exists, but only for identities that were merged — the census is expected to make this set empty.) |
| `login-with-key` | key → user | key → identity + `key.OwnerId` → `GetMembershipAsync(user.Id, key.OwnerId)` must be live+active+Approved, else `"disabled"`; null-owner key = super admin path |
| Magic link (`LoginWithInviteAsync`) | `link.UserId` → user, role QuickAccess | identity by `link.UserId` → membership `(identity, link.OwnerId)` live+active+Approved with `Role.QuickAccess` → `Issue(identity, membership)` |
| `/api/auth/me` (`MeAsync`) | user + `TenantName` | identity + membership `(sub, tenant)` → `UserMapper.ToMeResponse(user, role, tenantName)` (signature gains `Role? role`; role = membership's, or `user.Role` for super admins). Shape unchanged (DB-11b adds `Workspaces`) |
| Invite accept, existing workspace | new row | join-or-create (§3.3) with the invite's role; seat check counts **live memberships** in the workspace (`InWorkspace(owner).Count(m => m.LeftAt == null)`); `inviteId = invite.Id`; token for the new membership. `DisplayName` from the request only for a **new** identity |
| Invite accept, new workspace | new self-owned row | `workspaceId = Guid.NewGuid()`; `Workspaces.AddAsync` (name rule unchanged, DB-03 §3.4); join-or-create with the Workspace Admin role, Approved, active; the "address already owns a workspace" refusal at `InviteService.cs:172-185` is **deleted** (D13) |
| Self-serve register (stakeholder, `RegisterAsync`) | row per project tenant, pending | join-or-create into the project's workspace with `ApprovalStatus.Pending`, `isActive = false`; re-apply after rejection = the existing membership `Rejected → Pending` (password verify as today); a live Pending/Approved membership → `Conflict(AccountExists)` |
| Self-serve register admin (`RegisterAdminAsync`) | new self-owned row, pending | `workspaceId = Guid.NewGuid()`; workspace row; join-or-create with Workspace Admin, **membership** `Pending`/inactive (identity `IsActive` stays as-is for an existing identity; `false` for a new one as today). Subscription code unchanged with `OwnerId = workspaceId` |
| Super admin create tenant (`TenantService.CreateAsync`) | new row | same as register admin but Approved/active; `TenantResponse.OwnerId = workspaceId` |
| Super admin direct add / invite a Deputy (`UserService.CreateAsync`, `InviteService.CreateAsync`) | row in target tenant | `CurrentAdminAsync(targetOwnerId) != null` guard; join-or-create (admin-driven: no password check; an existing identity is just joined) |
| Workspace admin direct add (`UserService.CreateAsync` else-branch) | row + `?? _currentUser.Id` | `TryRequireOwner` (S-14) → join-or-create; "email taken" = live membership already in **this** workspace (not global) |
| Quick-access invite (`CreateQuickAccessInviteAsync`) | passwordless row | identity by email: exists → join with the Client role (its password, if any, is untouched); else `NewIdentity(passwordlessOnly: true)`; `inviteId` set |
| Demo mint (`DemoService.ProvisionAsync`) | self-owned demo row | `workspaceId = Guid.NewGuid()`; identity (`IsDemo`, `ExpiresAt`, `RecipientEmail` stay on `users` — S-8 later) + admin membership. `UpgradeAsync`: e-mail uniqueness becomes **global** (`FindIdentityByEmailAsync(email) != null && id != caller` → `Conflict(MessageKeys.Demo.EmailTaken)`, D7) |
| Demo cap lookups (`CommentService.cs:127`, `ExportImportService.cs:527`) | `users.public_id == owner && IsDemo` | `CurrentAdminAsync(owner)` → `.User.IsDemo / DemoCommentCapOverride` (the founding admin's `public_id` no longer equals the workspace id) |
| Users list (`UserService.ListAsync`) | `users` under filter | `InWorkspace(tenant)` where `LeftAt == null` (+ optional approval filter on the membership) → `UserResponse` (`Id = User.Id`, `PublicId`, `Email`, `DisplayName` from the identity; `RoleId/RoleName/IsAdmin/IsActive/ApprovalStatus` from the membership; `CreatedAt = JoinedAt`). **F7 (cross-review):** this is a workspace-scoped member list, not a global one — `TryRequireOwner` returns `Forbidden` for a super admin (no `tenant` claim), a **behaviour change** from `main` where a super admin's `GET /api/admin/users` returned every user across every tenant. No global "every user, every workspace" replacement is added by this doc — the super-admin operator view of tenants is `TenantService.ListAsync` (one row per workspace with its current admin). If a global all-users view is wanted later, it is a new, explicitly-designed endpoint, not a silent branch here. |
| Approve / Reject (`UserService`) | user row | the membership `(users.id == id, tenant)`; reject rotates the **membership** stamp |
| Update role / `isActive` (`UserService.UpdateAsync`) | user row + user stamp | membership `RoleId`/`IsActive` + **membership** stamp; the self-demotion guard reads the membership's role; `Password` → identity `PasswordHash` + identity stamp **only if** `ListForIdentityAsync(user.Id).Count == 1`, else `Failure(MessageKeys.User.PasswordManagedElsewhere)` (D6) |
| Delete (`UserService.DeleteAsync`) | soft-delete row | **end membership**: `LeftAt = UtcNow`, `LeftReason = Removed`, `IsActive = false`, membership stamp rotated. Guards as today (self, Workspace Admin, deputy matrix). Key/link revocation and the sole-admin guard are DB-11c |
| Transfer ownership | swaps `users.role_id` | swaps `RoleId` on the two memberships in the deputy's workspace, rotates both **membership** stamps |
| `GetCurrentAdminAsync` / `TenantService.ListAsync` / `SetStatusAsync` / `ChangePlanAsync` / `HardDeleteAsync` | `Role.Name == "Workspace Admin"` on `users` | `CurrentAdminAsync(workspaceId)`. `ListAsync` enumerates **`Workspaces`** (IgnoreQueryFilters) left-joined to the current admin membership; a workspace with no live admin is listed with `Id = 0`, `PublicId = Guid.Empty`, `Email = ""`, `DisplayName = ""`, `IsActive = false` (dashboard task: disable row actions when `id == 0`). `SetStatusAsync(id)` acts on the admin's **membership** in that workspace (approve/enable/disable + membership stamp) |
| Seats (`MaxSeats` checks) | count `users` by owner | count live memberships in the workspace (`UserService.cs:122`, `InviteService.cs:584,832`) |
| Roles in use (`RoleService.cs:232,274`) | users with `role_id` | memberships with `RoleId == id` in the caller's tenant (`InWorkspace(tenant)` or all workspaces for super admin); reassign `RoleId` on the membership |
| Stats (`StatsService.cs:29,34`) | count users | live memberships / pending memberships under the tenant filter |
| Admin notifications (`SuggestionService.cs:342`) | active admin users of the tenant | `InWorkspace(project.OwnerId)` where `LeftAt == null && IsActive && Role.GrantsAdmin && !Role.IsSuperAdmin && User.PublicId != caller` |
| Notification creation (`NotificationService.EnqueueAsync`) | always adds | resolves `notification.OwnerId` (stamp as today) then **skips** (returns without `AddAsync`) unless the recipient (`users.public_id == notification.UserId`) has a live, `IsActive`, Approved membership in that workspace. Owns the `is_active`-on-notification requirement |
| Device-login approval (`DeviceLoginService.ApproveAsync`) | no status check | after the super-admin check: `_currentUser.TenantId is not Guid tenant` → `Forbidden(MessageKeys.Common.Forbidden)`; membership `(caller, tenant)` must be live+`IsActive`+Approved, else `Forbidden(MessageKeys.Auth.Disabled)`. Owns the `is_active`-on-device-approval requirement. `PollAsync` passes `row.OwnerId` to `GetOrCreateAsync` |
| API keys (`ApiKeyService`, `ProfileService`, `MeController`) | per user | `GetOrCreateAsync(Guid publicId, Guid? workspaceId)`, `RegenerateAsync(Guid publicId, Guid? workspaceId)`; `ActiveKeyQuery(userId, ownerId)`; `BuildRow(user, ownerId, raw)` stamps `OwnerId = workspaceId`; callers pass `_currentUser.TenantId` (`MeController`/`ProfileService`) or `row.OwnerId` (device poll). `LoginWithApiKeyAsync` uses `apiKey.OwnerId` for the membership |
| Name resolution (5 sites) | `users` by `public_id` under filter | `UserNameResolver.ResolveAsync(IUnitOfWork, IEnumerable<Guid>)` (static, `Application/Services/Implementation/UserNameResolver.cs`, precedent `WorkspaceNameResolver`): query `users` by `PublicId` under the filter; for ids still missing, query `UserAliases.Where(a => ids.Contains(a.AliasPublicId)).Select(a => new { a.AliasPublicId, a.User.DisplayName })`. Replace the bodies at `CommentService.cs:1105-1116`, `ProjectService.cs:1175-1186`, `ExportImportService.cs:565-570`, `SuggestionService.cs:405-414` and the two `AiRuleService` lookups (keep their `Email` projection: add an overload returning `(DisplayName, Email)`) |
| `author_id` stability | — | identity `public_id` = the canonical row's (§3.2 2.1); merged rows' ids are rewritten in place (2.5) **and** recorded in `user_aliases` forever; resolver falls back to aliases |
| Workspace hard delete (`HardDeleteAsync`) | by admin `PublicId`; deletes `users` by `owner_id` | signature `HardDeleteAsync(Guid workspaceId)` (callers: `TenantsController.Delete(int id)` resolves the workspace via `CurrentAdmin`'s user id → membership `OwnerId`; `DemoCleanupService` selects `u.OwnerId` instead of `u.PublicId`). Order: the existing 22 calls with `DeleteOwnedAsync<WorkspaceMembership>` inserted **before** `DeleteOwnedAsync<User>`; then `User` handling replaces the plain delete: for each `users` row with `OwnerId == workspaceId` (IgnoreQueryFilters) — if it has any remaining membership (`Memberships.Any(m => m.OwnerId != workspaceId)`) set `OwnerId = <that membership's OwnerId>` (re-home; legacy column) else `RemoveRange` (api_keys cascade; aliases cascade). `HardDeleteOrder` gains `typeof(WorkspaceMembership)` before `typeof(User)` (23 entries) |
| `AdminSeeder` legacy subscription backfill (`:254-262`) | tenants = self-owned admins | `tenantPublicIds` → `db.Workspaces.Select(w => w.Id)` |
| Super admin identity | `users.role_id` = super role, `owner_id` null | unchanged; no membership; every "if super admin" branch stays on `user.Role` |

### 3.6 S-14 — the eleven fallbacks

Add to `TenantStamp`:
```csharp
/// <summary>S-14 (DB-11a). True with the caller's workspace for a tenant user; false for a
/// non-super-admin without a tenant claim — callers return Forbidden, never mint a tenant from a
/// user id. Super admins: false as well (they own nothing) unless the site documents a global-row case.</summary>
public static bool TryRequireOwner(ICurrentUser u, out Guid owner)
{
    owner = u.TenantId ?? Guid.Empty;
    return !u.IsSuperAdmin && u.TenantId is not null;
}
```
At each site in §2 "S-14 sites": delete `?? _currentUser.Id` (and `?? Guid.Empty`). Nine sites already
have `if (ownerId is not Guid owner) return …Forbidden/… null;` on the next line — they need nothing
else. `ProjectService.cs:220` → add `if (ownerId is not Guid owner) return Result<ProjectResponse>.Forbidden(MessageKeys.Common.Forbidden);`
after it and use `owner` below; `:298` and `:763` → `project.OwnerId ?? throw new InvalidOperationException("project without owner")` for `:298` (projects have NOT NULL `owner_id`) and `owner ?? …` same for `:763`; `UserService.cs:114` → `if (!TenantStamp.TryRequireOwner(_currentUser, out var o)) return Result<UserResponse>.Forbidden(MessageKeys.Common.Forbidden); ownerId = o;`;
`EntitlementService.cs:76-77` → `private Guid? CurrentTenantId() => TenantStamp.OwnerFor(_currentUser);`
and the two convenience overloads (`:36-37`, `:57`) return `Result.Forbidden(MessageKeys.Common.Forbidden)`
when it is null and the caller is not a super admin (super admin: `Result.Success()` — no plan applies).
Delete the two comments that justify the fallback (`ProjectService.cs:217-219`, `UserService.cs:111-113`).

### 3.7 Message keys (add to `Application/Resources/MessageKeys.cs`)

`User.PasswordManagedElsewhere = "This user also belongs to other workspaces — they must change their password themselves."`,
`User.AlreadyMember = "This person is already a member of this workspace."`,
`Auth.NoWorkspace = "Your account is not a member of any workspace."` (used by DB-11b; harmless now),
`Auth.InvalidCredentialsAfterMerge = "Invalid email or password. Accounts that shared this e-mail were combined into one — if your previous password no longer works, use \"Forgot password\"."` (GLM A8).

### 3.8 What is deliberately not read any more

After this doc, `grep -rn "u.OwnerId ==\|\.OwnerId == u.PublicId\|OwnerId == publicId" Application/Services/Implementation/{AuthService,UserService,TenantService,InviteService,DemoService}.cs`
must print only the mint-point assignments (`OwnerId = …`) and `Workspace.Id` comparisons, never a
`users.owner_id` predicate; `grep -rn "u.Role.Name\|user.Role.Name\|target.Role.Name" Application` must
print only super-admin checks (`IsSuperAdmin`) — every "Workspace Admin" check goes through a
membership's `Role`. Acceptance criteria §7 mechanise this.

### 3.9 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9 step 2)

| # | Question | Default encoded |
|---|---|---|
| D1 | Two live rows, same e-mail, different passwords | the most recently written row's hash wins (2.2); the canonical's sessions are revoked; the other password stops working — and the login failure for a merged identity **says so** and points at "Forgot password" (GLM A8, §3.5 Login). Uniqueness is enforced by the database on `lower(email)` (§3.1), not by convention |
| D2 | Which row is the identity | Workspace Admin row, else Deputy, else oldest (2.1); its `public_id` survives |
| D3 | API keys per identity or per membership | per membership |
| D4 | Anonymous accept/register with an existing e-mail | must present the existing account's password; else `AccountExists` |
| D5 | Self-serve "register admin" with an existing e-mail | same as D4, then a new workspace + admin membership under the same identity (pending approval per membership) |
| D6 | Admin sets another member's password | only when the identity has exactly one live membership |
| D7 | Demo upgrade to an e-mail that already exists | `Conflict` (no automatic merge) |
| D13 | "An address that already owns a workspace cannot accept a new-workspace invite" | refusal removed — one identity may administer several workspaces |

## 4. Safety classification

**Expand** (R1 tables/column/index, R3 abort-guarded backfill and in-place uuid rewrite, R9 filtered
unique indexes) with **one index swap** on `api_keys` and **one data-moving rewrite** (2.5) whose
only inverse is the dump (§8). Migration 1 (index change), Migration 2 (R3) **and Migration 3 (index
change, raw SQL — GLM A1)** carry markers and `[ContractMigration("DB-11a")]`. Ships through the explicit R7 path,
**alone** (R7.1 batching is not used: this is the first release whose rollback is "restore the
dump", and the census must be read by a human first). No column is dropped, renamed or narrowed.

## 5. File-level tasks

Order matters: 1–4 compile without touching behaviour; 5–7 are the migrations; 8–20 switch the code.

1. `Domain/Enums/MembershipEndReason.cs` — `public enum MembershipEndReason { Removed = 1, Left = 2, AccountErased = 3 }` with the R10 append-only comment.
2. `Domain/Entity/WorkspaceMembership.cs`, `Domain/Entity/UserAlias.cs` — per §3.1, with the doc-comments quoted there. `Domain/Entity/User.cs` — add `public int? MergedIntoUserId { get; set; }`, `public ICollection<WorkspaceMembership> Memberships { get; set; } = new List<WorkspaceMembership>();`, and the four doc-comment changes (§3.1 "users").
3. `Infrastructure/Mappings/WorkspaceMembershipMapping.cs`, `UserAliasMapping.cs` — new; copy the column-naming style of `ApiKeyMapping.cs`. Membership FKs: `HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict)`; `HasOne<Workspace>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(Restrict).HasConstraintName("fk_workspace_memberships_workspaces_owner_id")`; `HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(Restrict)`; `HasOne<Invite>().WithMany().HasForeignKey(x => x.InviteId).OnDelete(DeleteBehavior.SetNull)`; the two indexes of §3.1. `UserMapping.cs` — add `MergedIntoUserId` column + self FK `HasOne<User>().WithMany().HasForeignKey(x => x.MergedIntoUserId).OnDelete(Restrict).HasConstraintName("fk_users_merged_into_user")`; add, directly above the `b.Property(x => x.Email)` line (`:26`), the two-line comment `// ux_users_email_live: UNIQUE on lower(email) WHERE deleted_at IS NULL — raw SQL in *_AddUsersEmailLiveUniqueIndex (DB-11a GLM A1),` / `// deliberately NOT modelled (EF cannot express lower()); do not add a HasIndex for it.` — **no** `HasIndex` line. `ApiKeyMapping.cs:50-53` — replace the `HasIndex(x => x.UserId)…ux_api_keys_active_per_user` block with `b.HasIndex(x => new { x.UserId, x.OwnerId }).IsUnique().HasFilter("revoked_at IS NULL AND deleted_at IS NULL").AreNullsDistinct(false).HasDatabaseName("ux_api_keys_active_per_membership");` and rewrite the comment above it ("one live key per membership").
4. `Infrastructure/AppDbContext.cs` — `DbSet<WorkspaceMembership> WorkspaceMemberships`, `DbSet<UserAlias> UserAliases` after line 71; replace the `User` filter (`:91-96`) with §3.1's; add the strict-own filter for `WorkspaceMembership` (copy `:85-90`) with a two-line comment; add a comment block for `UserAlias` (no filter, reason in §3.1). `Application/Abstractions/IUnitOfWork.cs:10` + `Infrastructure/Repository/UnitOfWork.cs:21` — add `DbSet<UserAlias> UserAliases`.
5. `just migrate name="AddWorkspaceMembershipsAndUserAliases"` → read it against §3.2 Migration 1. Any `CreateIndex` on `users` other than the existing ones → a mapping edit leaked — stop. Any operation on a table other than `workspace_memberships`, `user_aliases`, `users`, `api_keys` → stop and report. Add the attribute and the marker verbatim.
6. `just migrate name="MergeSameEmailIdentitiesAndBackfillMemberships"` → the file must have **empty** `Up()`/`Down()` (if not, a mapping change leaked — stop). Paste the seven SQL blocks of §3.2 as separate `migrationBuilder.Sql(@"…")` calls in order (2.1, 2.2, 2.3, 2.4, 2.4b, 2.5 (both statements in one call), 2.6, 2.7). The `DO $$ … $$` blocks contain no `"`; the `format()` call's single quotes are fine inside a C# verbatim string. Add the attribute and the R3 marker verbatim. Leave `Down()` empty and add the comment `// No inverse: 2.5 rewrites uuid references in place. Rollback = restore the pre-db11a dump (docs/db/execution/DB-11a-… §8).`
7. `just migrate name="AddUsersEmailLiveUniqueIndex"` → the file must have **empty** `Up()`/`Down()` (else a mapping change leaked — stop). Paste the two `migrationBuilder.Sql(…)` calls of §3.2 Migration 3 (one in `Up()`, one in `Down()`), add `[ContractMigration("DB-11a")]` and the `index change` marker verbatim. `dotnet ef migrations has-pending-model-changes -p Infrastructure -s API` must still print "No changes have been made to the model since the last migration."
7a. `Application/Common/EmailNormalizer.cs` — §3.3a verbatim. Replace the ten hand-normalisations listed in §2 (`AuthService.cs:50,236,344,454`, `UserService.cs:62`, `DemoService.cs:267`, `InviteService.cs:154,518,799`, `API/Seed/AdminSeeder.cs:128`) with `EmailNormalizer.NormalizeRequired(...)` / `.Normalize(...)` as §3.3a says; do this before task 8 so `MembershipService` is born normalised.
8. `Application/Services/Interfaces/IMembershipService.cs` + `Application/Services/Implementation/MembershipService.cs` — §3.3 verbatim signatures. `Application/Services/Implementation/UserNameResolver.cs` — §3.5 "Name resolution".
9. `Application/Abstractions/ITokenService.cs`, `Infrastructure/Auth/JwtTokenService.cs` — §3.4. `Application/Common/UserMapper.cs` — `ToMeResponse(User user, Role? role, string? tenantName = null)`; every `user.Role?.X` becomes `role?.X`. `API/Extensions/AuthenticationExtensions.cs:55-105` — §3.4 validator change.
10. `Application/Common/TenantStamp.cs` + the 13 S-14 lines (§3.6).
11. `Application/Services/Implementation/AuthService.cs` — `RequestPasswordResetAsync` (`:55-62`): identity by email, `IsActive` on identity, `!IsDemo` as today, plus "has at least one live membership or is super admin"; `LoginAsync`, `LoginWithApiKeyAsync`, `RegisterAsync`, `RegisterAdminAsync`, `MeAsync`, `LoginWithInviteAsync` per §3.5; `ResolveTenantNameAsync` unchanged.
12. `UserService.cs` — every method per §3.5; `GetCurrentAdminAsync` → delegate to `_memberships.CurrentAdminAsync`; `MapToResponse(WorkspaceMembership m)` replaces `MapToResponse(User, Role?)`. Constructor gains `IMembershipService memberships` (tests construct `UserService` by hand: `WorkspaceAdminOwnershipTests.cs:52-56`, `UserGovernanceTests.cs` — add the argument there).
13. `TenantService.cs` — `ListAsync`, `CreateAsync`, `SetStatusAsync`, `ChangePlanAsync`, `HardDeleteAsync(Guid workspaceId)` + `HardDeleteOrder` (23) per §3.5; `ExtendDemoAsync`/`SetDemoConfigAsync` keep reading `users` by `Id` (demo fields stay there). `API/Controllers/Admin/TenantsController.cs:117-124` `Delete(int id)` → resolve `workspaceId` via `ITenantService.ResolveWorkspaceIdAsync(int adminUserId)` (new small method: membership of that user with role Workspace Admin) then `HardDeleteAsync(workspaceId)`. `API/Hosted/DemoCleanupService.cs:42-52` → `.Select(u => u.OwnerId)` and pass the workspace id.
14. `InviteService.cs` — `CreateAsync` (`:90-100` guard → `CurrentAdminAsync`; `:124` S-14; delete `:172-185`), `AcceptJoinExistingWorkspaceAsync`, `AcceptCreateNewWorkspaceAsync`, `CreateQuickAccessInviteAsync`, `LoadOwnAsync` (`:438` S-14) per §3.5. Constructor gains `IMembershipService` (tests: `InviteServiceTests.BuildService`, `TenantInviteServiceTests`).
15. `DemoService.cs` — `ProvisionAsync` (`:110-140`), `UpgradeAsync` (`:288-300` global uniqueness), token calls. `CommentService.cs:127-133`, `ExportImportService.cs:524-533` — demo cap via `CurrentAdminAsync(owner)`.
16. `ApiKeyService.cs` + `IApiKeyService.cs` + `ProfileService.cs:43-47` + `API/Controllers/MeController.cs:49-66` + `DeviceLoginService.cs:142` — §3.5 "API keys". `DeviceLoginService.ApproveAsync` — §3.5 "Device-login approval". `NotificationService.EnqueueAsync` — §3.5 "Notification creation" (constructor gains `IMembershipService`; tests `NotificationServiceTests`).
17. `SuggestionService.cs:338-352,405-414`, `RoleService.cs:228-245,270-276`, `StatsService.cs:29-36`, `ProjectService.cs:1175-1186`, `AiRuleService.cs:390-410,555-575`, `CommentService.cs:1105-1116` — per §3.5.
18. `API/Seed/AdminSeeder.cs:254-262` — `db.Workspaces`. `Application/Resources/MessageKeys.cs` — §3.7.
19. Tests (§6). 20. `just fmt`, `just test`, rehearsal (R11) with §3.2 queries and the §9 step-1 census on the rehearsal copy; paste outputs into the PR. Update `docs/db/SCHEMA.md` rows for `users`, `workspace_memberships`, `user_aliases`, `api_keys` from "planned" to present (only doc edit allowed here).

## 6. Tests

New `Tests/TestSeed.cs` helper: `public static WorkspaceMembership Join(AppDbContext db, User user, Guid workspaceId, Role role, bool isActive = true, ApprovalStatus status = ApprovalStatus.Approved)` — adds the membership (and a `Workspace` row if missing) and saves. Use it in every existing test that seeds a tenant user and then calls `AuthService`, `UserService`, `TenantService`, `InviteService`, `RoleService`, `StatsService`, `SuggestionService`, `NotificationService`, `DeviceLoginService` or `ApiKeyService` (start with the 17 files that set `OwnerId =` on a `User`; the failing tests name the rest). Do **not** weaken assertions.

New `Tests/WorkspaceMembershipTests.cs` (InMemory fixture from `WorkspaceAdminOwnershipTests.cs:24-46`; Sqlite `TestDb` for 1 and 9):

1. `Membership_UnknownWorkspaceOrUser_RejectedByForeignKey` (Sqlite) — `DbUpdateException` for a membership with a random `OwnerId`, and for a random `UserId`.
2. `InWorkspace_TenantB_SeesNothingOfTenantA` — two workspaces, memberships in each; `MembershipService.InWorkspace(B)` returns only B's; `db.Users` under a tenant-B context returns only identities with a membership in B (the R8 test for the new `User` filter).
3. `Login_SameEmailInTwoWorkspaces_IsOneIdentity_HomeWins` — one identity, memberships in A (home = `user.OwnerId`) and B; `LoginAsync` → token `tenant` claim == A (decode with `JwtSecurityTokenHandler`, as `TokenServiceTests.cs`).
4. `Login_AllMembershipsPending_ReturnsPending`; `Login_AllMembershipsDisabled_ReturnsDisabled`; `Login_SuperAdmin_NoTenantClaim` (unchanged behaviour).
5. `AcceptInvite_ExistingEmail_WrongPassword_Conflict_NoRowCreated` and `AcceptInvite_ExistingEmail_RightPassword_AddsMembership_KeepsPublicId` (build `InviteService` as `InviteServiceTests.BuildService`).
6. `RegisterAdmin_ExistingEmail_RightPassword_MintsWorkspaceUnderSameIdentity` — `Workspaces.Count == 2`, `users.Count == 1`, admin membership Pending.
7. `UpdateUser_Password_RefusedWhenIdentityHasOtherMemberships` (D6) and `_AllowedWhenSingleMembership`.
8. `Delete_EndsMembership_KeepsIdentity` — `LeftAt != null`, `LeftReason == Removed`, `users` row intact, other workspace's membership untouched.
9. `HardDelete_Workspace_KeepsMultiWorkspaceIdentity_RemovesSingleWorkspaceIdentity` (Sqlite) — identity X in A and B, identity Y only in A; `HardDeleteAsync(A)` → X survives with `OwnerId == B`, Y gone, memberships of A gone, `HardDeleteOrder.Length == 23`.
10. `UserNameResolver_ResolvesAliasedAuthor` — seed identity + a `UserAlias` row for an old uuid; resolver returns the identity's name for the old uuid.
11. `Notification_SkippedForInactiveOrEndedMembership`; `DeviceApprove_ForbiddenForInactiveMembership`.
12. `StampValidator_RejectsMembershipStampMismatch` — build the `OnTokenValidated` handler as `Tests/ApiKeyAuthTests.cs` builds auth pieces (or extract the lookup into a static `SecurityStampCheck.EvaluateAsync(AppDbContext, Guid sub, Guid? tenant, Guid stamp, Guid? mstamp)` and test that directly — preferred; then the handler is a one-line call).
13. `S14_NullTenantNonSuperAdmin_IsForbidden` — for `ProjectService.CreateAsync`, `UserService.CreateAsync`, `InviteService.CreateAsync`, `PredefinedActionService` create, `WorkspaceService.RenameAsync`: `FakeCurrentUser { Id = x, IsAdmin = true, TenantId = null }` → `IsForbidden`, and `db.Workspaces.Count()` / `db.Projects.Count()` unchanged.
14. `TokenService_Issue_WithMembership_EmitsTenantAndMstamp` (extend `TokenServiceTests`).
15. **GLM A1** `Tests/EmailNormalizerTests.cs`: `Normalize_TrimsAndLowers` (`"  User@X.COM "` → `"user@x.com"`), `Normalize_NullOrWhitespace_ReturnsNull`, `NormalizeRequired_Whitespace_ReturnsEmpty`.
16. **GLM A1** `FindIdentityByEmail_MixedCaseInput_FindsLowerCaseRow` (InMemory) and `AcceptInvite_MixedCaseEmail_JoinsExistingIdentity_NoSecondRow` (InMemory; `users.Count == 1` after accepting with `"USER@x.com"` for an identity stored as `user@x.com`).
17. **GLM A1, database-level** — the expression index cannot be tested on InMemory/Sqlite, so it is a CI step: in `.github/workflows/db-migrations.yml`, after the "Newest migration Down/Up round-trip" step and before the guard tests, add
    ```yaml
      - name: Mixed-case duplicate e-mail is rejected by the database (DB-11a, GLM A1)
        run: |
          PGPASSWORD=pointer psql -h localhost -U pointer -d pointer -v ON_ERROR_STOP=1 -c "INSERT INTO roles (name, is_super_admin, grants_admin, quick_access, is_active, is_system, created_at, created_by) VALUES ('db11a-ci', false, false, false, true, false, now(), '00000000-0000-0000-0000-000000000000') ON CONFLICT DO NOTHING;" || true   # roles may already be seeded by a migration
          rid=$(PGPASSWORD=pointer psql -h localhost -U pointer -d pointer -tAc "SELECT id FROM roles ORDER BY id LIMIT 1")
          PGPASSWORD=pointer psql -h localhost -U pointer -d pointer -v ON_ERROR_STOP=1 -c "INSERT INTO users (public_id, email, password_hash, display_name, role_id, is_active, approval_status, security_stamp, is_demo, \"DemoExtended\", created_at, created_by) VALUES (gen_random_uuid(), 'ci-dup@example.com', 'x', 'a', $rid, true, 1, gen_random_uuid(), false, false, now(), '00000000-0000-0000-0000-000000000000');"
          if PGPASSWORD=pointer psql -h localhost -U pointer -d pointer -v ON_ERROR_STOP=1 -c "INSERT INTO users (public_id, email, password_hash, display_name, role_id, is_active, approval_status, security_stamp, is_demo, \"DemoExtended\", created_at, created_by) VALUES (gen_random_uuid(), 'CI-Dup@Example.com', 'x', 'b', $rid, true, 1, gen_random_uuid(), false, false, now(), '00000000-0000-0000-0000-000000000000');" 2>/tmp/dup.err; then echo "second insert succeeded — ux_users_email_live is not an expression index"; exit 1; fi
          grep -q ux_users_email_live /tmp/dup.err
    ```
    (Column lists verified against `AppDbContextModelSnapshot.cs` on 2026-09-22: every NOT NULL column without a default is supplied — `users`: `is_demo`, `"DemoExtended"` (unmapped PascalCase column, hence quoted), `is_active`, `security_stamp`, `created_by`; `roles`: `is_system`. The first statement only guarantees one role exists and may fail if roles are already seeded; the assertion is the last two lines.)
18. **GLM A7** negative rehearsal addition (§6 last paragraph): on the second scratch database also seed a person with **two soft-deleted** rows in the same workspace plus a live row differing only in case (`Bob@x.com` / `bob@x.com`) in that workspace; after the migrations: exactly **one** ended membership and **one** live membership for (identity, workspace), and Migration 2 did not fail on `ux_workspace_memberships_user_workspace_live`.
19. **GLM A8** `Login_WrongPassword_MergedIdentity_SuggestsForgotPassword` — identity with a soft-deleted row whose `MergedIntoUserId` points at it; wrong password → `Message == MessageKeys.Auth.InvalidCredentialsAfterMerge`; the same wrong password for an unmerged identity → `InvalidCredentials`.

"Existing data survives" is the rehearsal (§3.2 queries) — the SQL is Npgsql-only (`gen_random_uuid`, `DO $$`, `information_schema`), Sqlite cannot run it; say so in the PR. Negative rehearsal: on a second scratch database insert two live users with the same e-mail in two workspaces plus a comment by each, run the migrations, and check: one live user, one alias, both comments' `author_id` equal the survivor's `public_id`, two live memberships.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` shows exactly three new ids ending `_AddWorkspaceMembershipsAndUserAliases`, `_MergeSameEmailIdentitiesAndBackfillMemberships`, `_AddUsersEmailLiveUniqueIndex`, in that order.
2. `grep -c "ContractMigration(\"DB-11a\")" Infrastructure/Migrations/*_AddWorkspaceMembershipsAndUserAliases.cs Infrastructure/Migrations/*_MergeSameEmailIdentitiesAndBackfillMemberships.cs Infrastructure/Migrations/*_AddUsersEmailLiveUniqueIndex.cs` → 1 each (GLM A1: Migration 3 is raw SQL); `grep -c "lower(email)" Infrastructure/Migrations/*_AddUsersEmailLiveUniqueIndex.cs` → 1; `grep -c "HasIndex" Infrastructure/Mappings/UserMapping.cs` → 2 (`PublicId`, `ux_users_email_owner_live` — no third); `grep -c "RAISE EXCEPTION 'DB-11a ABORT" Infrastructure/Migrations/*_MergeSameEmail*.cs` → 2; `grep -c "migrationBuilder.Sql(" …_MergeSameEmail*.cs` → 8; `grep -c "DISTINCT ON (coalesce(m.canonical_id, u.id), u.owner_id" …_MergeSameEmail*.cs` → 1 (GLM A7).
3. `grep -rn '?? _currentUser.Id\|?? currentUser.Id' Application API Infrastructure --include='*.cs' | wc -l` → 0.
4. `grep -rn '"Workspace Admin"' Application --include='*.cs' | grep -v "const string\|///" | wc -l` → 0 outside `AdminSeeder.cs` and the constants (every check goes through `MembershipService.CurrentAdminAsync` or a membership's `Role.Name` constant).
5. `grep -rn "Role.Name == WorkspaceAdminRoleName" Application --include='*.cs'` → only lines whose receiver is a membership (`m.Role`, `membership.Role`, `admin.Role` where `admin` is a `WorkspaceMembership`) — reviewer reads each.
6. `grep -c "await DeleteOwnedAsync<" Application/Services/Implementation/TenantService.cs` → 23; `HardDeleteOrder` has 23 entries.
7. `grep -rn "GetOrCreateAsync(Guid publicId)" Application` → 0 (new signature everywhere); `grep -rn "\.Issue(" Application API --include='*.cs' | wc -l` → 7 and none passes a `User` alone without a membership argument (super-admin paths pass `null`).
8. `just test` green, incl. the 19+ new facts; `MigrationSafetyTests` passes with markers/attributes as in §3.2; the DB-10 workflow is green **including** the new GLM A1 step (§6 test 17).
9. Rehearsal: §3.2 queries print the expected values; the negative rehearsal shows one survivor, one alias, rewritten `author_id`s; `\d api_keys` shows `ux_api_keys_active_per_membership` and not `_per_user`; `\d users` shows `"ux_users_email_live" UNIQUE, btree (lower(email)) WHERE deleted_at IS NULL`; the GLM A1 negative INSERT of §3.2 fails with that constraint name.
11. **GLM A1** `grep -rn "Trim().ToLower" Application API --include='*.cs' | grep -i email | wc -l` → 0; `grep -rln "EmailNormalizer\." Application API --include='*.cs' | wc -l` → ≥ 6 (`AuthService`, `UserService`, `DemoService`, `InviteService`, `MembershipService`, `AdminSeeder`).
10. On the rehearsal API: password login of the production admin → JWT has `tenant` = their workspace id and an `mstamp` claim; `GET /api/auth/me` unchanged shape; `GET /api/admin/users` lists the same people as before; `GET /api/admin/tenants` lists the same workspaces; CLI `pointer whoami` with the existing key still works.

## 8. Rollback

Migrations 1 and 3 have full `Down()`s (Migration 3's is `DROP INDEX IF EXISTS ux_users_email_live`). **Migration 2 has none: 2.5 rewrites `author_id`/audit uuids
in place and 2.6 soft-deletes merged rows; the inverse is the labelled dump.** Rollback = API stopped,
`DEPLOY.md` § Restore from `pre-db11a`, `git checkout <commit before DB-11a>`, `up -d --build api`.
Anything written after the deploy (new comments, new members) is lost in that case — which is why
this doc ships alone and is verified (§9 step 5) within minutes of the deploy. If the census shows
**zero** duplicates, 2.1–2.6 touch nothing but insert memberships, and `Down()` of Migrations 3 → 1
plus `DELETE FROM workspace_memberships` is a complete rollback without the dump; the doc still
requires the dump.

## 9. Release steps

1. **Census (mandatory, paste to the owner and into the PR):**
   ```bash
   docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "SELECT lower(email) AS email, count(*) AS rows, string_agg(u.id::text || ':' || coalesce(r.name,'?') || ':' || coalesce(u.owner_id::text,'-'), ' | ' ORDER BY u.id) AS rows_detail FROM users u LEFT JOIN roles r ON r.id = u.role_id WHERE u.deleted_at IS NULL GROUP BY 1 HAVING count(*) > 1;"
   docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -tAc "SELECT count(*) FROM users WHERE deleted_at IS NULL AND owner_id IS NOT NULL;"   -- expected live memberships
   ```
   Zero rows → D1/D2 are moot, proceed. Otherwise the owner confirms (or overrides) D1/D2 per
   e-mail listed; if they override, the SQL in 2.1/2.2 changes and this doc is amended first.
2. Merge; R11 rehearsal on a same-day dump (outputs in the PR).
3. On the VM: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db11a bash scripts/deploy-api.sh`.
   Expect three `Applying migration` lines and `DB-09: applying 3 contract migration(s)` (GLM A1 made Migration 3 a marked one). A `DB-11a ABORT`
   line means the database is unchanged (Migration 1 **did** apply — it is in its own transaction —
   so run `git checkout <previous commit> && … up -d --build api`, then `dotnet ef database update <id before Migration 1>` with the API stopped, or simply restore `pre-db11a`).
4. Verify: the §3.2 rehearsal queries against prod; `GET /api/auth/me` for the real admin returns
   the same `tenantName`; dashboard login, users page, tenants page; widget login on the production
   project; `pointer whoami`.
5. Watch `docker compose logs api` for `Token has been revoked` bursts (would indicate an `mstamp`
   mismatch — every pre-deploy token is expected to be rejected once and re-issued at next login,
   which is the normal 12 h expiry behaviour brought forward) and for `DemoCleanupService` failures.
6. Hand DB-11b to the implementer.

## 10. Out of scope

Dropping `users.owner_id`, `users.role_id`, `users.approval_status`, `ux_users_email_owner_live`
(contract doc **DB-11e**, after one release of zero readers); changing an identity's e-mail
(`POST /api/me/change-email`, [DB-11d](DB-11d-change-email.md)); applying the `login` rate limit to
password login (foundations #6, ops pack — not this doc); the workspace picker, `switch-workspace`,
`/me.workspaces`, `LoginRequest.ProjectKey`, status `no-workspace` (DB-11b); remove/disable/leave/erase
semantics, key/link revocation on removal, sole-admin guard, `users.erased_at`, revoke-invite response
(DB-11c); moving `is_demo/expires_at/caps/recipient_email` to `workspaces` (S-8); FKs from
`comments.author_id` etc. to `users(public_id)` (Q5 — now answerable: possible after DB-11c since
erase keeps the row; separate doc); the `TenantResponse.Id == 0` admin-less workspace cleanup
(owner question); `ON-DISK-CONTRACT.md` (no identifier in it changes); regenerating `clients/`
(no DTO shape changes in this doc — `MeResponse`, `UserResponse`, `TenantResponse`, `LoginResponse`
keep their fields).

## 11. Dashboard / widget / CLI tasks

- **Dashboard** — **required, F9 (cross-review fix).** The super-admin tenant endpoints
  (`PATCH /api/admin/tenants/{id}` → `.../status`, `PATCH /api/admin/tenants/{id}/plan`,
  `DELETE /api/admin/tenants/{id}`) now key on the **workspace id**
  (`{workspaceId:guid}`), never the admin's `users.id` — the previous "exactly one admin
  membership" resolution 404'd for any identity administering more than one workspace, this
  release's own headline capability (D13). `TenantResponse` gains `WorkspaceId: Guid` (`Id: int` is
  now doc-commented legacy — 0 for an admin-less workspace, and no longer unique once one identity
  administers several workspaces). `TenantsPage` must build these three requests from
  `tenant.WorkspaceId`, not `tenant.Id`. This is a route/DTO contract change: the client needs
  regenerating (`orval.config.ts` already lists the `Tenants` tag) before the dashboard is synced —
  the `dashboard-agent` handles this once per phase per `docs/roadmap/execution/00-API-INVENTORY.md`
  § Cross-repo sync agents. Optional, still open: `TenantsPage` disables row actions when
  `id === 0` (admin-less workspace).
- **Widget** — none. Login statuses unchanged.
- **CLI** — none. Keys keep working (they are already stamped with the workspace); `login-with-key` now lands in the key's workspace deterministically.
