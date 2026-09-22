# DB-03 — `workspaces` table, `owner_id` foreign keys, complete tenant hard-delete

Review findings: S-1, S-2, I-1 (deferred), M-1. Rules: R1, R2, R3, R7, R8, R10, R11, R13.
**Class: Expand** (new table + abort-guarded backfill into it + 23 FK constraints + code). Two
migrations, one release. **Status 2026-09-22 (evening): owner decisions Q2 and Q3 answered —
ready to implement.** Requires [DB-09](DB-09-migration-apply-gate.md) (merged `ff25a8d`): Migration
1 carries `[ContractMigration("DB-03")]` and ships through the explicit `deploy-api.sh` path.
**Amended 2026-09-22 (evening)** for the owner decisions: §3.3 backfill now **aborts** on a true
orphan (Q2 = b) and names the ids; `workspaces.name` is the workspace's **own attribute** seeded with
a placeholder, never the admin's display name (Q3); §3.5 switches every read of "workspace name"
from the admin user to `workspaces.name`; §9 step 1 is a mandatory prod pre-check. The admin-facing
rename surface is [DB-03b](DB-03b-workspace-name-surface.md). Earlier amendments (GLM B1/B4/B5,
AGY 3.1) stand.

## 1. Goal

Give the workspace a row of its own. Today a workspace is a uuid copied into `owner_id` on 23
tables with no table behind it; identity is recovered by matching a role *name*, its display name
is whatever the current admin calls *themselves*, a typo can create an unreachable tenant silently,
and tenant deletion misses 17 of 23 tables and fails outright when a suggestion notification exists
(`DemoCleanupService` retries hourly). After this doc: every `owner_id` references
`workspaces(id)`; creating a workspace inserts a row; the workspace has a name that is not a
person's name; deleting a workspace is a single ordered routine whose completeness a test enforces.
**Customer-visible change: exactly one** — until the admin renames it in DB-03b, the header label
(`MeResponse.TenantName`) and the invite preview read `Workspace` instead of the admin's display
name. The JWT `tenant` claim, every `OwnerId` property, every other DTO field and every query filter
stay exactly as they are, because `workspaces.id` *is* today's `owner_id` value.

## 2. Prerequisites (verified facts, 2026-09-22 @ `ff25a8d`)

- `BaseEntity` has `int Id` (`Domain/Entity/BaseEntity.cs:5`); `Repository<T>`/`UnitOfWork.Repository<T>()` are constrained to `BaseEntity` (`Infrastructure/Repository/Repository.cs:7`, `UnitOfWork.cs:11`). `UsageEvent` is the precedent for a non-`BaseEntity` entity exposed as a `DbSet` on the unit of work (`Application/Abstractions/IUnitOfWork.cs:9`, `Infrastructure/Repository/UnitOfWork.cs:21`, `AppDbContext.cs:61`).
- `AppDbContext.SaveChangesAsync` stamps audit columns only for `BaseEntity` entries (`AppDbContext.cs:168-194`) — a `Workspace` must set its own `CreatedAt/CreatedBy`.
- Query filters: strict-own shape `AppDbContext.cs:77`; the Project filter is the one to copy.
- `TenantStamp.OwnerFor` (`Application/Common/TenantStamp.cs:11`) returns `null` for super admins.
- Services are registered by Scrutor scanning for classes whose name ends in `Service` (`Application/DependencyInjection.cs:11-12`) — no manual DI line for new services.
- Workspace mint points (each builds a `User` with `OwnerId = publicId` and then `AddAsync` + `SaveChangesAsync`):
  1. `Application/Services/Implementation/AuthService.cs:406-421` (`RegisterAsync`, pending approval; `RegisterRequest` has `DisplayName`, `ProjectKey` — no workspace-name field, `Application/DTOs/Auth/RegisterRequest.cs`),
  2. `Application/Services/Implementation/TenantService.cs:170-185` (`CreateAsync`, super admin; `CreateTenantRequest` has only `Email/Password/DisplayName`),
  3. `Application/Services/Implementation/InviteService.cs:602-613` (`AcceptAsync` new-workspace branch; `invite` is in scope — `invite.Id` at `:599`; `Invite.DisplayName` (`Invite.cs:59-63`, ≤120) is documented as "Workspace name the super admin typed when inviting"),
  4. `Application/Services/Implementation/DemoService.cs:89-100` (demo provisioning; `ExpiresAt` set, `IsDemo = true`).
  Check: `grep -n "OwnerId = publicId" Application/Services/Implementation/*.cs` prints exactly `AuthService.cs:417`, `AuthService.cs:442` (subscription — not a mint), `DemoService.cs:96,111,123,138,153` (first is the user; the rest are the demo project/actions/statuses), `InviteService.cs:612,655`, `TenantService.cs:181,191`. If the line numbers moved, re-locate by the `OwnerId = publicId` text; if a **new** file appears, stop and report.
- **Every place that derives the workspace name from a user row today** (all switch to `workspaces.name` in §3.5):
  1. `AuthService.ResolveTenantNameAsync` (`AuthService.cs:157-170`; callers `:224`, `:268`, `:470-471`, `:519`) → `MeResponse.TenantName` (`Application/DTOs/Auth/MeResponse.cs:19-21`, doc-comment says "the tenant owner user's DisplayName"); shown in the dashboard header (`../pointer-dashboard/react/src/features/shell/Shell.tsx:89,140-143`).
  2. `InviteService.GetPreviewAsync` (`InviteService.cs:411-421`, fallback `?? "Workspace"` at `:439`) → `InvitePreviewResponse.WorkspaceName` (`Application/DTOs/Invite/InvitePreviewResponse.cs:16-17`, doc-comment "The owning admin's display name").
  3. `PlatformInsightsService.BuildTenantNameMapAsync` (`PlatformInsightsService.cs:215-233`; fallback `"Workspace " + id[..8]` at `:313`).
  4. `AiRuleService` — two identical `tenantMap` builds (`AiRuleService.cs:375-397` and `:495-515`; fallback at `:406`).
  `TenantService.ListAsync` (`TenantService.cs:111`) puts the **admin's** display name in `TenantResponse.DisplayName` — that field describes the admin user and stays; a `WorkspaceName` field is added in DB-03b.
- Tests that assert today's behaviour and must be re-seeded: `Tests/InviteServiceTests.cs:90-110` (`SeedTenant`, admin `DisplayName = "Acme Inc"` at `:105`) with the assertion at `:738`; `Tests/PlatformInsightsServiceTests.cs:37-38` (`TenantA/TenantB`), `:62-63` (admins "Admin A"/"Admin B"), assertions `:183`, `:189`. `grep -rn "TenantName" Tests/` prints only those two lines.
- Enumeration of tenants: `TenantService.ListAsync` (`TenantService.cs:36-49`) by `Role.Name == "Workspace Admin"`; constants `TenantService.cs:19`, `UserService.cs:19-20`, `InviteService.cs:24-25`.
- Hard delete: `TenantService.HardDeleteAsync(Guid tenantId)` (`TenantService.cs:356-441`) — resolves `realOwnerId` (`:374-375`), deletes owner files (`:378`), then inside `ExecuteInTransactionAsync` removes Replies, Comments, Projects, StatusPresentations, Users, Roles (`:388-437`). Called by `API/Hosted/DemoCleanupService.cs:78`.
- FK behaviours that constrain delete order: `comments.project_id` Restrict (`CommentMapping.cs:72`); `notifications.project_id` Restrict, `comment_id` Cascade, `suggestion_id` Cascade (`NotificationMapping.cs:33-35`); `users.role_id` Restrict (`UserMapping.cs:46-49`); `api_keys.user_id` Cascade (`ApiKeyMapping.cs:33-36`); `project_app_urls`, `project_builds`, `page_context_snapshots` cascade from `projects`; `subscriptions.plan_id` Restrict.
- The 23 tables with `owner_id` and their mapping file + line of `b.Property(x => x.OwnerId)`: `ai_rules` (`AiRuleMapping.cs:21`, NOT NULL), `api_keys` (`ApiKeyMapping.cs:24`), `app_environments` (`AppEnvironmentMapping.cs:23`), `comments` (`CommentMapping.cs:43`, NOT NULL), `device_logins` (`DeviceLoginMapping.cs:28`), `extension_sites` (`ExtensionSiteMapping.cs:23`, NOT NULL, CLR `Guid`), `invites` (`InviteMapping.cs:24`), `notifications` (`NotificationMapping.cs:23`), `page_context_snapshots` (`PageContextSnapshotMapping.cs:27`), `predefined_action_suggestions` (`PredefinedActionSuggestionMapping.cs:23`), `predefined_actions` (`PredefinedActionMapping.cs:26`, NOT NULL), `project_app_urls` (`ProjectAppUrlMapping.cs:29`, NOT NULL), `project_builds` (`ProjectBuildMapping.cs:25`), `projects` (`ProjectMapping.cs:44`, NOT NULL), `quick_access_links` (`QuickAccessLinkMapping.cs:15`), `replies` (`ReplyMapping.cs:27`), `role_tenant_overrides` (`RoleTenantOverrideMapping.cs:22`, NOT NULL, CLR `Guid`), `roles` (`RoleMapping.cs:30`), `status_presentations` (`StatusPresentationMapping.cs:24`), `subscriptions` (`SubscriptionMapping.cs:23`, NOT NULL, CLR `Guid`), `usage_events` (`UsageEventMapping.cs:16`), `users` (`UserMapping.cs:39`), `workspace_settings` (`WorkspaceSettingMapping.cs:23`).
- DB-02 guard (`Tests/MigrationSafetyTests.cs:75-82`): `.Sql(` is a risky operation; the marker regex is `// DB-RULES: (R2 contract|R3 backfill|index change|R4 constraint) approved yyyy-mm-dd by <non-space>`; marker and `[ContractMigration` must agree (`:158-172`). Precedent marker wording: `20260922080137_DropShadowProjectAppUrlProjectId1.cs:12`.
- Test fixtures: InMemory `AppDbContext` + `FakeCurrentUser` (`Tests/CommentFieldsTests.cs:28-36,61-62`); Sqlite shared-cache fixture that enforces FKs and unique indexes (`Tests/UsageEventFirstCommentTests.cs:43-60`, `TestDb`); tenancy tests (`Tests/TenantQueryFilterTests.cs:34-49`). InMemory does **not** enforce FKs — FK tests must use the Sqlite fixture.
- Migration commands: `just migrate name="…"` (`justfile:6`); rehearsal R11 in `DB-RULES.md`. Prod psql: `docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer` (`scripts/deploy-api.sh:27,33`).
- `Tests/Pointer.Tests.csproj` references the API project (it uses `Pointer.API.Seed.AdminSeeder`), so the test project can reference `TenantService` and `Domain` types.

## 3. Design

### 3.1 Entity and mapping

`Domain/Entity/Workspace.cs` — **not** a `BaseEntity` (PK is `Guid`):

| property | column | type | null | notes |
|---|---|---|---|---|
| `Id` | `id` | `uuid` PK | no | `ValueGeneratedNever()` — always the pre-chosen `publicId` |
| `Name` | `name` | `varchar(120)` | no | **the workspace's own name.** Check constraint `ck_workspaces_name_not_blank`: `length(btrim(name)) > 0`. Not unique (two customers may both be "Acme"). Seeded with `Workspace.PlaceholderName` (below) |
| `CreatedAt` | `created_at` | `timestamptz` | no | set by code |
| `CreatedBy` | `created_by` | `uuid` | no | the founding user's `PublicId` |
| `UpdatedAt` / `UpdatedBy` | `updated_at` / `updated_by` | | yes | set by DB-03b's rename |
| `DeletedAt` / `DeletedBy` | `deleted_at` / `deleted_by` | | yes | reserved (hard delete is the only delete today) |

Constant on the entity: `public const string PlaceholderName = "Workspace";` with the doc-comment
"Seeded by the DB-03 backfill and used by every mint point that has no workspace name to offer.
`Name == PlaceholderName` means 'not yet named by an admin' (DB-03b shows a prompt)."

**Why the placeholder is the constant `Workspace` (Q3, decided here):** (1) it is already the
string the codebase shows when no name is known (`InviteService.cs:439`, and the
`"Workspace " + id[..8]` fallbacks) so nothing new is invented; (2) it is brand-neutral, contains no
PII and is deterministic, so the migration is idempotent and rehearsal output equals production
output; (3) it is trivially detectable, which DB-03b uses to prompt the admin; (4) the alternative
— deriving it from the first project key — would surface an internal identifier in the header and
in the **anonymous** invite preview (`InvitePreviewResponse.WorkspaceName`) and would not read as a
placeholder. The single production workspace therefore boots as `Workspace` until its admin names
it in the Settings page (DB-03b).

Table `workspaces`. Query filter (add to `AppDbContext.OnModelCreating`):
`b.Entity<Workspace>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.Id == currentUser.TenantId));`
Anonymous paths (login, invite preview) read it with `IgnoreQueryFilters()` exactly as they read
`users` today.

`DbSet<Workspace> Workspaces` on `AppDbContext` and on `IUnitOfWork`/`UnitOfWork` (same shape as `UsageEvents`).

### 3.2 Foreign keys (all 23 tables)

In each mapping listed in §2, directly after the `OwnerId` property line, add:

```csharp
b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.OwnerId)
    .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_<table>_workspaces_owner_id");
```

Exception — `usage_events`: `.OnDelete(DeleteBehavior.SetNull)` (analytics survive a tenant's deletion with `owner_id = NULL`; Q4 answered in the same direction for `project_id`, DB-06). Nullable `owner_id` columns stay nullable (NULL = super admin / global bucket / pre-approval).

**Expected new indexes (census verified 2026-09-22 against every mapping).** EF's
`ForeignKeyIndexConvention` adds `IX_<table>_owner_id` for each new FK unless the table already has
an index whose **first** column is `owner_id` (a unique or filtered one counts). Result:

| Table | Existing owner-leading index? | Expect in Migration 2 |
|---|---|---|
| `api_keys` | none (`ApiKeyMapping.cs:39-48` index hash / `user_id` only) | **`IX_api_keys_owner_id`** |
| `device_logins` | none (`DeviceLoginMapping.cs:35,39`) | **`IX_device_logins_owner_id`** |
| `project_builds` | none (`ProjectBuildMapping.cs:32` is `(project_id, sha)`) | **`IX_project_builds_owner_id`** |
| `quick_access_links` | none (`QuickAccessLinkMapping.cs:35` is `token_hash`) | **`IX_quick_access_links_owner_id`** |
| `role_tenant_overrides` | `(role_id, owner_id)` — owner not leading (`RoleTenantOverrideMapping.cs:24`) | **`IX_role_tenant_overrides_owner_id`** |
| `extension_sites` | `(owner_id, origin)` unique, filtered since DB-05 (`ExtensionSiteMapping.cs:26`) | none expected; **accept either** |
| `subscriptions` | `owner_id` unique, filtered since DB-05 (`SubscriptionMapping.cs:24`) | none expected; **accept either** |
| `workspace_settings` | `owner_id` unique, filtered (`WorkspaceSettingMapping.cs:30`) | none expected; **accept either** |
| all 15 others (`ai_rules :29`, `app_environments :24`, `comments :44`, `invites :36`, `notifications :39`, `page_context_snapshots :46`, `predefined_action_suggestions :34` `(owner_id, status)`, `predefined_actions :36`, `project_app_urls :43`, `projects :45`, `replies :28`, `roles :31`, `status_presentations :25`, `usage_events :42` `(owner_id, …)`, `users :40`) | yes, owner-leading | **none** |

So Migration 2 contains **exactly 5** `CreateIndex` operations plus **0–3** optional ones from the
"accept either" rows. Any `CreateIndex` on a table not in the first eight rows, or any operation
that is not `AddForeignKey`/`CreateIndex`, → **stop and report**; do not delete operations to make
the file match.

### 3.3 Migrations (two files, this order)

**Migration 1 — `AddWorkspaces`** (generated; edit as described):

1. `CreateTable("workspaces", …)` as in §3.1 with `PK_workspaces` and the check constraint
   (EF 8 emits `table.CheckConstraint("ck_workspaces_name_not_blank", "length(btrim(name)) > 0")`
   **inside** the `CreateTable` call — it is not a separate operation).
2. Two lines above `public partial class`: `[ContractMigration("DB-03")]` (DB-09 §3); the line
   above `Up(`: the marker, **verbatim**:
   `// DB-RULES: R3 backfill approved 2026-09-22 by Moamen (owner; decisions Q2=(b) abort on orphan, Q3=workspace name is its own attribute, relayed by the orchestrator; docs/db/execution/DB-03-workspaces-table.md)`
3. **Inserted by hand after `CreateTable`, as two `migrationBuilder.Sql(...)` calls in this order**
   (R3: insert-only into the new table; idempotent; the second call is the Q2 abort):
   ```sql
   -- 3a. one row per workspace that has, or ever had, a Workspace Admin. DISTINCT ON keeps one
   --     candidate per owner_id: a live admin before a soft-deleted one, then the earliest created.
   --     A workspace whose admins are ALL soft-deleted still gets its row (GLM B5): it is a
   --     reachable tenant with no live admin, not an orphan. Name = placeholder (Q3).
   INSERT INTO workspaces (id, name, created_at, created_by)
   SELECT DISTINCT ON (u.owner_id) u.owner_id, 'Workspace', u.created_at, u.public_id
   FROM users u JOIN roles r ON r.id = u.role_id
   WHERE r.name = 'Workspace Admin' AND u.owner_id IS NOT NULL
   ORDER BY u.owner_id, (u.deleted_at IS NOT NULL), u.created_at
   ON CONFLICT (id) DO NOTHING;
   ```
   ```sql
   -- 3b. Q2 = (b): ABORT if any owner_id anywhere still has no workspaces row. RAISE inside the
   --     migration's transaction rolls back 3a and the CreateTable; Migration 2 never runs.
   DO $$
   DECLARE orphans text;
   BEGIN
     SELECT string_agg(o.owner_id::text, ', ' ORDER BY o.owner_id::text) INTO orphans
     FROM (
       SELECT owner_id FROM ai_rules UNION SELECT owner_id FROM api_keys UNION SELECT owner_id FROM app_environments
       UNION SELECT owner_id FROM comments UNION SELECT owner_id FROM device_logins UNION SELECT owner_id FROM extension_sites
       UNION SELECT owner_id FROM invites UNION SELECT owner_id FROM notifications UNION SELECT owner_id FROM page_context_snapshots
       UNION SELECT owner_id FROM predefined_action_suggestions UNION SELECT owner_id FROM predefined_actions
       UNION SELECT owner_id FROM project_app_urls UNION SELECT owner_id FROM project_builds UNION SELECT owner_id FROM projects
       UNION SELECT owner_id FROM quick_access_links UNION SELECT owner_id FROM replies UNION SELECT owner_id FROM role_tenant_overrides
       UNION SELECT owner_id FROM roles UNION SELECT owner_id FROM status_presentations UNION SELECT owner_id FROM subscriptions
       UNION SELECT owner_id FROM usage_events UNION SELECT owner_id FROM users UNION SELECT owner_id FROM workspace_settings
     ) o
     WHERE o.owner_id IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = o.owner_id);
     IF orphans IS NOT NULL THEN
       RAISE EXCEPTION 'DB-03 ABORT: owner_id value(s) with no Workspace Admin user (live or soft-deleted): %. Transaction rolled back; the database is unchanged. Re-run the prod pre-check in docs/db/execution/DB-03-workspaces-table.md section 9 step 1 and report the ids to the owner.', orphans;
     END IF;
   END $$;
   ```
   (`UNION` without `ALL` already de-duplicates; the `string_agg … ORDER BY` names every offending
   id in the exception text, which reaches `docker compose logs api` via Npgsql.)
   **How to keep the FKs out of this file without hand-moving anything (two-scaffold recipe):**
   write the 23 FK lines of §3.2 into the mappings **commented out** (prefix each with
   `// DB-03 step 2: `), scaffold Migration 1 with `just migrate name="AddWorkspaces"` — it then
   contains only `CreateTable("workspaces")` (+ `PK_workspaces` + the check constraint) — add the
   marker, the attribute and the two SQL blocks by hand, **then** remove the `// DB-03 step 2: `
   prefixes from all 23 lines and scaffold Migration 2. Never move operations between generated
   files.
4. `Down`: `DropTable("workspaces")` (generated).

**Migration 2 — `AddWorkspaceForeignKeys`** (scaffolded with `just migrate` after uncommenting the
23 FK lines): 23 × `AddForeignKey` + the `CreateIndex` ops from the §3.2 table (5 expected, up to
3 optional). `Down` drops them. It contains no risky operation, so no marker and no
`[ContractMigration]` — R13 still applies: the file must contain **only**
`AddForeignKey`/`CreateIndex`/their inverses. (It auto-applies in the same boot as Migration 1
because the DB-09 flag covers every pending migration of that boot.)

**Expected state of every existing row:** unchanged, in both outcomes. Success: every existing
`owner_id` value has a `workspaces` row named `Workspace` *before* any FK is created, so no FK
creation can fail. Abort: 3b raises, Migration 1's transaction rolls back, `workspaces` does not
exist, Migration 2 does not run, `__EFMigrationsHistory` is unchanged, the API exits non-zero (see
§9 step 6 for recovery). NULL `owner_id` rows are untouched (FKs ignore NULL).

Rehearsal verification (R11 step 4). Run after `dotnet ef database update` on the rehearsal copy:
```sql
SELECT count(*) FROM workspaces;                                            -- W
SELECT count(DISTINCT owner_id) FROM users u JOIN roles r ON r.id=u.role_id
 WHERE r.name='Workspace Admin' AND u.owner_id IS NOT NULL;                 -- must equal W
SELECT count(*) FROM workspaces WHERE name <> 'Workspace';                  -- 0 (all placeholders)
-- informational: workspaces whose only admins are soft-deleted (GLM B5) — they got a row by design
SELECT count(DISTINCT u.owner_id) FROM users u JOIN roles r ON r.id=u.role_id
 WHERE r.name='Workspace Admin' AND u.owner_id IS NOT NULL AND u.deleted_at IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM users u2 JOIN roles r2 ON r2.id=u2.role_id
                   WHERE u2.owner_id=u.owner_id AND u2.deleted_at IS NULL AND r2.name='Workspace Admin');
SELECT count(*) FROM comments c LEFT JOIN workspaces w ON w.id=c.owner_id WHERE w.id IS NULL;   -- 0
SELECT count(*) FROM projects p LEFT JOIN workspaces w ON w.id=p.owner_id WHERE w.id IS NULL;   -- 0
SELECT count(*) FROM users u  LEFT JOIN workspaces w ON w.id=u.owner_id WHERE u.owner_id IS NOT NULL AND w.id IS NULL; -- 0
```

### 3.4 Code changes — mint and hard delete

- **Mint**: at each of the four mint points, immediately before `AddAsync(<user>)`, add
  `await _unitOfWork.Workspaces.AddAsync(new Workspace { Id = publicId, Name = <name>, CreatedAt = DateTime.UtcNow, CreatedBy = publicId });`
  where `<name>` is, per site (the workspace name is **never** the user's display name — Q3):
  | site | `<name>` |
  |---|---|
  | `AuthService.RegisterAsync` | `Workspace.PlaceholderName` |
  | `TenantService.CreateAsync` | `Workspace.PlaceholderName` |
  | `InviteService.AcceptAsync` (new-workspace branch) | `string.IsNullOrWhiteSpace(invite.DisplayName) ? Workspace.PlaceholderName : invite.DisplayName.Trim()[..Math.Min(120, invite.DisplayName.Trim().Length)]` — `Invite.DisplayName` *is* a workspace name typed by the super admin (`Invite.cs:59-63`) |
  | `DemoService` | `"Demo Workspace"` |
  The existing `SaveChangesAsync` that follows persists both. Adding a workspace-name field to
  `RegisterRequest`/`CreateTenantRequest` is **out of scope** (DB-03b §10 lists it as a follow-up).
- **HardDelete** (AGY 3.1 — **no loop, no reflection in production code**): replace the six blocks at `TenantService.cs:388-437` with **22 explicit, sequential calls** in exactly this order (children before parents):
  ```csharp
  await DeleteOwnedAsync<Notification>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Reply>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Comment>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<PageContextSnapshot>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<ProjectBuild>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<ProjectAppUrl>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<PredefinedActionSuggestion>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<PredefinedAction>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<AiRule>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<QuickAccessLink>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Project>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<ExtensionSite>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Invite>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<StatusPresentation>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<RoleTenantOverride>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<WorkspaceSetting>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Subscription>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<ApiKey>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<DeviceLogin>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<User>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<Role>(x => x.OwnerId == realOwnerId);
  await DeleteOwnedAsync<AppEnvironment>(x => x.OwnerId == realOwnerId);
  ```
  with one private generic helper that takes the predicate (so it compiles for the three entities whose `OwnerId` is `Guid` and the nineteen whose is `Guid?` — no `EF.Property`, no reflection):
  ```csharp
  private async Task DeleteOwnedAsync<T>(Expression<Func<T, bool>> ownedBy) where T : BaseEntity
  {
      var repo = _unitOfWork.Repository<T>();
      var rows = await repo.Query().IgnoreQueryFilters().Where(ownedBy).ToListAsync();
      if (rows.Count > 0)
          repo.RemoveRange(rows);
  }
  ```
  (`Repository<T>.Query()` and `RemoveRange` exist: `Infrastructure/Repository/Repository.cs:11,23`; this is the exact shape of the six blocks being replaced, `TenantService.cs:386-392`. Add `using System.Linq.Expressions;`.) `UsageEvent` is intentionally absent (FK `SET NULL`). After the 22 calls, delete the `workspaces` row (`_unitOfWork.Workspaces.Remove(...)` after loading it with `IgnoreQueryFilters()`), then the existing `SaveChangesAsync` inside `ExecuteInTransactionAsync`.
  Also add `internal static readonly Type[] HardDeleteOrder = { typeof(Notification), typeof(Reply), … typeof(AppEnvironment) };` — the **same 22 types in the same order**. It is **only** read by the reflection test in §6 (it is the list the test compares against the entity assembly); production code never iterates it. Put a comment above it: `// Documentation + test input. Keep in the same order as the DeleteOwnedAsync<T> calls above. Never loop over this in production code.`

### 3.5 Code changes — every "workspace name" read moves to `workspaces.name` (Q3)

After this section no production code derives a workspace's name from a `users` row. Four sites:

1. `AuthService.ResolveTenantNameAsync` (`AuthService.cs:157-170`) — replace the body so it keeps
   the signature and the `null → null` behaviour:
   ```csharp
   if (ownerId == null) return null;
   return await _unitOfWork.Workspaces.IgnoreQueryFilters().AsNoTracking()
       .Where(w => w.Id == ownerId.Value).Select(w => w.Name).FirstOrDefaultAsync();
   ```
   Rewrite the comment above it: "Resolves the workspace's own name from `workspaces.name` (DB-03).
   Null ownerId (super admin) → null; a missing row → null." Callers (`:224`, `:268`, `:470-471`,
   `:519`) are unchanged.
2. `InviteService.GetPreviewAsync` (`InviteService.cs:411-421`) — replace the `workspaceName` query
   with the same `Workspaces.IgnoreQueryFilters()…Where(w => w.Id == invite.OwnerId)…Select(w => w.Name)` shape;
   keep `?? Workspace.PlaceholderName` at `:439` (replace the literal `"Workspace"`). Rewrite the
   comment at `:408-410` ("SAFE preview only: the workspace's own name + …").
3. `PlatformInsightsService.BuildTenantNameMapAsync` (`PlatformInsightsService.cs:215-233`) — body
   becomes `return await _unitOfWork.Workspaces.IgnoreQueryFilters().AsNoTracking().Where(w => ids.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);` (keep the `ids` computation and the early return).
4. `AiRuleService` (`AiRuleService.cs:375-397` and `:495-515`) — replace each `tenantAdmins` query +
   `foreach` with the same `ToDictionaryAsync` over `Workspaces`; the `tenantMap` variable name and
   the fallbacks at `:406`/`:419`/`:524` stay.
5. Doc-comments: `MeResponse.cs:19-20` → "The workspace's own name (`workspaces.name`, DB-03).
   Null for super admins, who have no workspace."; `InvitePreviewResponse.cs:16` → "The workspace's
   own name (never a person's name)."
6. `ListAsync`, `TransferOwnershipAsync`, JWT claims, every other DTO: **unchanged** (stage B later;
   `TenantResponse.WorkspaceName` is DB-03b).

## 4. Safety classification

**Expand** (R1 + R3 backfill into a new table, abort-guarded + constraints). No column is dropped,
renamed or narrowed. Migration 1 carries the R3 marker and `[ContractMigration("DB-03")]`, so it
**cannot** auto-apply on an ordinary deploy (DB-09); it ships through the explicit R7 path
(API stopped, labelled dump, human reading the log). No owner approval line is required beyond the
marker (nothing destructive); Q2/Q3 are answered (b / own attribute) and encoded above.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — new class per §3.1 (plain properties + `PlaceholderName` const; doc-comment: "One row per workspace. `Id` equals every `owner_id` that belongs to it and the JWT `tenant` claim. `Name` is the workspace's own name, never a person's. Not a `BaseEntity`: the PK is a uuid chosen before insert.").
2. `Infrastructure/Mappings/WorkspaceMapping.cs` — new `IEntityTypeConfiguration<Workspace>`: `ToTable("workspaces", t => t.HasCheckConstraint("ck_workspaces_name_not_blank", "length(btrim(name)) > 0"))`, `HasKey(x => x.Id)`, `Property(x => x.Id).HasColumnName("id").ValueGeneratedNever()`, `Name` `.HasColumnName("name").IsRequired().HasMaxLength(120)`, the six audit columns with the same `HasColumnName` calls every other mapping uses.
3. `Infrastructure/AppDbContext.cs` — add `public DbSet<Workspace> Workspaces => Set<Workspace>();` after line 63; add the query filter from §3.1 after line 147 with a two-line comment.
4. `Application/Abstractions/IUnitOfWork.cs` — add `DbSet<Workspace> Workspaces { get; }` after line 9. `Infrastructure/Repository/UnitOfWork.cs` — add `public DbSet<Workspace> Workspaces => db.Workspaces;` after line 21.
5. The 23 mapping files (§2 list) — add the FK line from §3.2 after the `OwnerId` property line, **commented out** with the prefix `// DB-03 step 2: ` (so the first scaffold does not see them); `usage_events` uses `SetNull`.
6. `just migrate name="AddWorkspaces"` → open the generated file. `Up()` must contain **only** `CreateTable("workspaces", …)` (with `PK_workspaces` and the check constraint inside it) and `Down()` only `DropTable`. **If it contains any `AddForeignKey` or `CreateIndex`, a step-5 line was not commented out — stop and report.** If the `CreateTable` has a column not in §3.1 → stop and report. Then add, by hand: `[ContractMigration("DB-03")]` above the class, the marker line (verbatim from §3.3 item 2) above `Up(`, and the two `migrationBuilder.Sql(...)` blocks (3a, 3b) after `CreateTable`. The `DO $$ … $$` block goes in a C# verbatim string (`@"…"`) — check no `"` needs doubling (there is none in the SQL above).
7. Remove the `// DB-03 step 2: ` prefix from all 23 lines (`grep -rn "DB-03 step 2" Infrastructure/Mappings | wc -l` → 0 afterwards). `just migrate name="AddWorkspaceForeignKeys"` → open it: `Up()` = 23 × `AddForeignKey` + the `CreateIndex` ops listed in the §3.2 table (5, plus up to 3 optional), nothing else. Anything else → stop and report. No marker, no attribute (no risky operation).
8. Mint points (§2, four files) — add the `Workspaces.AddAsync` line per §3.4 with the per-site `<name>`.
9. `Application/Services/Implementation/TenantService.cs:388-437` — replace with the 22 explicit `await DeleteOwnedAsync<T>(…)` calls and the helper per §3.4; add `internal static readonly Type[] HardDeleteOrder` (22 entries, same order). Add `[assembly: InternalsVisibleTo("Pointer.Tests")]` only if the Application project does not already expose internals to tests (check `Application/*.csproj` and existing `InternalsVisibleTo`; if absent, make the array `public static readonly` instead — either is acceptable).
10. §3.5 — the four read sites + two doc-comments (`AuthService.cs`, `InviteService.cs`, `PlatformInsightsService.cs`, `AiRuleService.cs` ×2, `MeResponse.cs`, `InvitePreviewResponse.cs`).
11. Tests (§6), including the two re-seeded files. 12. `just fmt`, `just test`. 13. Rehearsal (R11) with the §3.3 verification queries and the §9 step-1 pre-check against the rehearsal copy; paste the outputs into the PR.
14. Regenerate nothing under `clients/` (no endpoint changed — `MeResponse.TenantName` keeps its name and type). Update `docs/db/SCHEMA.md`'s `workspaces` row from "planned" to present and mention `name` (one-line edit) — the only doc edit allowed here.

## 6. Tests

New file `Tests/WorkspaceTests.cs` (copy the Sqlite `TestDb` fixture verbatim from `Tests/UsageEventFirstCommentTests.cs:43-60`; copy `FakeCurrentUser` from `Tests/CommentFieldsTests.cs:28-36`):

1. `Project_WithUnknownOwner_IsRejectedByForeignKey` — insert a `Project` whose `OwnerId` has no `workspaces` row → `Assert.ThrowsAsync<DbUpdateException>`. (Tenancy-integrity proof.)
2. `TenantService_CreateAsync_MintsWorkspaceRow_WithPlaceholderName` — build `TenantService` the way `Tests/TenantInviteServiceTests.cs` or `WorkspaceAdminOwnershipTests.cs` builds it; call `CreateAsync` with `DisplayName = "Jane Doe"`; assert the row `db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == result.Data.OwnerId)` has `Name == Workspace.PlaceholderName` and **not** `"Jane Doe"` (Q3 regression guard).
3. `InviteAccept_NewWorkspace_UsesInviteDisplayName` — seed a null-owner invite with `DisplayName = "Acme Inc"`, accept with a user `DisplayName = "Jane Doe"`; assert the minted workspace is named `"Acme Inc"`. (Build the service as `Tests/InviteServiceTests.cs` does.)
4. `HardDeleteOrder_CoversEveryOwnerCarryingEntity` — reflection: every type in `typeof(BaseEntity).Assembly` that is a class, not abstract, has a property named `OwnerId`, and is not `Workspace` or `UsageEvent`, must be contained in `TenantService.HardDeleteOrder`; and `HardDeleteOrder.Length == 22`. Failure message names the missing type. (This is the rule R8 point 5 enforcer. The array is test input only — see §3.4.)
5. `HardDelete_RemovesEverything_EvenWithSuggestionNotification` — seed one workspace with a project, a comment, a suggestion, a `Notification { CommentId = null, SuggestionId = …, ProjectId = … }`, a subscription, a workspace_settings row; call `HardDeleteAsync`; assert `IsSuccess` and, for every type in `HardDeleteOrder`, `Set<T>().IgnoreQueryFilters().Count(x => x.OwnerId == id) == 0`, and `Workspaces.Count() == 0`.
6. `Workspace_TenantB_CannotReadTenantA` — InMemory is fine here: two workspaces, context for tenant B, `db.Workspaces.ToList()` returns only B. (Copy shape from `TenantQueryFilterTests.Project_TenantA_SeesOnlyOwnRows`.)
7. `Me_TenantName_ComesFromWorkspaceRow_NotAdmin` — InMemory: seed a workspace `Name = "Acme Inc"` and its admin `DisplayName = "Jane Doe"`; call `AuthService.MeAsync` (or the login path `Tests/*` already exercise — pick the one with the smallest fixture) as that admin; assert `TenantName == "Acme Inc"`.

Re-seed two existing files (do **not** weaken their assertions):
- `Tests/InviteServiceTests.cs:90-110` `SeedTenant` — add `seed.Workspaces.Add(new Workspace { Id = tenant, Name = "Acme Inc", CreatedAt = DateTime.UtcNow, CreatedBy = tenant });` so `:738` (`"Acme Inc"`) still holds, now from the workspace row.
- `Tests/PlatformInsightsServiceTests.cs:62-63` — add `Workspace` rows `{ Id = TenantA, Name = "Workspace A" }`, `{ Id = TenantB, Name = "Workspace B" }`; change `:183` to `"Workspace A"` and `:189` to `"Workspace B"`.

"Existing data survives" is proven by the R11 rehearsal counts (§3.3) — the migrations are Npgsql-specific and cannot run under Sqlite; say so in the PR. The abort path is proven on the rehearsal copy by §9 step-1's query printing `0 orphans` **and** by one negative rehearsal: `INSERT INTO projects (…) VALUES (… owner_id = gen_random_uuid() …)` on a *second* scratch database, run `dotnet ef database update`, expect the `DB-03 ABORT: … <that uuid>` exception and `\dt workspaces` → does not exist; then drop that database.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` shows exactly two new ids ending in `_AddWorkspaces` and `_AddWorkspaceForeignKeys`, in that order.
2. `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → 23; `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaces.cs` → 0; `grep -c "CreateIndex(" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → between 5 and 8, and every `name:` among them is one of `IX_api_keys_owner_id`, `IX_device_logins_owner_id`, `IX_project_builds_owner_id`, `IX_quick_access_links_owner_id`, `IX_role_tenant_overrides_owner_id`, `IX_extension_sites_owner_id`, `IX_subscriptions_owner_id`, `IX_workspace_settings_owner_id`.
3. `grep -c "RAISE EXCEPTION 'DB-03 ABORT" Infrastructure/Migrations/*_AddWorkspaces.cs` → 1; `grep -c "ContractMigration(\"DB-03\")" Infrastructure/Migrations/*_AddWorkspaces.cs` → 1; `grep -c "ContractMigration" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → 0; `grep -c "Recovered" Infrastructure/Migrations/*_AddWorkspaces.cs` → 0 (the old attach path is gone).
4. `grep -rn "HasOne<Workspace>" Infrastructure/Mappings | wc -l` → 23; `grep -rn "DB-03 step 2" Infrastructure/Mappings | wc -l` → 0.
5. `grep -n "Workspaces.AddAsync" Application/Services/Implementation/*.cs | wc -l` → 4; `grep -c "await DeleteOwnedAsync<" Application/Services/Implementation/TenantService.cs` → 22; `grep -c "foreach.*HardDeleteOrder\|HardDeleteOrder\[" Application/Services/Implementation/TenantService.cs` → 0.
6. **Q3 proof:** `grep -rn "Role.Name == \"Workspace Admin\"\|Role.Name == WorkspaceAdminRoleName" Application/Services/Implementation/AuthService.cs Application/Services/Implementation/PlatformInsightsService.cs Application/Services/Implementation/AiRuleService.cs` → no output (those three files no longer look up admins to name a workspace); `grep -n "Select(u => u.DisplayName)" Application/Services/Implementation/InviteService.cs` → no output; `grep -rn "\.Workspaces" Application/Services/Implementation/{AuthService,InviteService,PlatformInsightsService,AiRuleService}.cs | wc -l` → ≥ 5.
7. `just test` green; `Tests/WorkspaceTests.cs` has the 7 facts above; `MigrationSafetyTests` (DB-02) passes with the R3 marker and `[ContractMigration]` present on Migration 1 and neither on Migration 2.
8. Rehearsal: the §3.3 queries print `W`, `W`, `0`, `S` (informational), `0`, `0`, `0`; `\d comments` shows `fk_comments_workspaces_owner_id`; `\d workspaces` shows `ck_workspaces_name_not_blank`; the negative rehearsal (§6) printed the `DB-03 ABORT` line naming the injected uuid.
9. A registered user's JWT still carries the same `tenant` value as before (login → decode → compare to `users.owner_id`): no auth change. `GET /api/auth/me` on the rehearsal API returns `"tenantName":"Workspace"`.
10. `DemoCleanupService` log on the rehearsal API shows `hard-deleted demo tenant` (seed one expired demo first) rather than `HardDeleteAsync returned failure`.

## 8. Rollback

Both migrations have full `Down()`s: drop 23 FKs (+ indexes), then drop `workspaces`. No other table is modified, so `dotnet ef database update <previous id>` (API stopped) restores the prior schema with zero data loss. Dump label: `pre-db03` when shipped alone, `pre-db03-08` when shipped in the batch (R7.1; `bash scripts/backup-db.sh <label>` is run by `deploy-api.sh`). If rollback happens after new workspaces were minted, the `workspaces` rows (including any name set via DB-03b) are lost but every `owner_id` still exists in `users` — re-running Migration 1 recreates them with the placeholder name (idempotent 3a; 3b passes because the admins exist). An abort (§3.3 3b) needs no rollback: nothing was committed.

## 9. Release steps

1. **Mandatory prod pre-check (Q2).** On the VM, before anything else — the output must be exactly `0 orphans`:
   ```bash
   docker compose --env-file .env.prod -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -tAc "SELECT coalesce(string_agg(o.owner_id::text, ', ' ORDER BY o.owner_id::text), '0 orphans') FROM (SELECT owner_id FROM ai_rules UNION SELECT owner_id FROM api_keys UNION SELECT owner_id FROM app_environments UNION SELECT owner_id FROM comments UNION SELECT owner_id FROM device_logins UNION SELECT owner_id FROM extension_sites UNION SELECT owner_id FROM invites UNION SELECT owner_id FROM notifications UNION SELECT owner_id FROM page_context_snapshots UNION SELECT owner_id FROM predefined_action_suggestions UNION SELECT owner_id FROM predefined_actions UNION SELECT owner_id FROM project_app_urls UNION SELECT owner_id FROM project_builds UNION SELECT owner_id FROM projects UNION SELECT owner_id FROM quick_access_links UNION SELECT owner_id FROM replies UNION SELECT owner_id FROM role_tenant_overrides UNION SELECT owner_id FROM roles UNION SELECT owner_id FROM status_presentations UNION SELECT owner_id FROM subscriptions UNION SELECT owner_id FROM usage_events UNION SELECT owner_id FROM users UNION SELECT owner_id FROM workspace_settings) o WHERE o.owner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM users u JOIN roles r ON r.id = u.role_id WHERE r.name = 'Workspace Admin' AND u.owner_id = o.owner_id);"
   ```
   Any other output is a list of orphan uuids: **do not deploy**; paste the list to the owner (it is the S-2 orphan class the review predicted; a decision to attach or delete those rows is a new execution doc). This is the same predicate as 3b, so a passing pre-check means the migration cannot abort.
2. Merge; on the VM: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db03 bash scripts/deploy-api.sh` (DB-09 path: pulls, pre-flight lists `…_AddWorkspaces`, stops the API, dumps `pre-db03`, rebuilds with `DBApplyContractMigrations=true`). **In the batch release (R7.1) the label is `pre-db03-08` and steps 1–2 of every batched doc run first; see the review §7.**
3. The script prints the log grep; expect two `Applying migration` lines, one `DB-09: applying 1 contract migration(s)` line, and no error.
4. Verify: `docker compose -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "SELECT id, name, created_at FROM workspaces;"` → one row per workspace, all named `Workspace`. `GET /api/auth/me` (dashboard login) → `tenantName: "Workspace"` and the header shows it.
5. Smoke: dashboard login, comment list, `GET /api/admin/tenants` (super admin) unchanged; invite preview of an existing-workspace invite shows `Workspace`.
6. **If the log shows `DB-03 ABORT`** (cannot happen after step 1 passed, but): the API container is restarting in a loop and the database is unchanged. Recover with `git checkout <commit before DB-03> && docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api` (the old image runs fine against the unchanged schema), then report the ids from the log to the owner.
7. Watch `DemoCleanupService` lines over the next hour for failures.
8. Hand DB-03b to the implementer (the admin can then replace `Workspace` with a real name).

## 10. Out of scope

The rename endpoint, `WorkspaceResponse`, `TenantResponse.WorkspaceName`, the Settings card and any
`orval`/client regeneration (DB-03b); a workspace-name field on signup/create-tenant/accept-invite
requests (DB-03b §10 follow-up); moving demo/workspace fields off `users` (S-8, stage B — separate
doc); tightening nullable `owner_id` columns to NOT NULL (I-1 — separate doc after a rehearsal
shows zero NULLs); `ListAsync` enumeration by role name (stage B); `workspace_settings` merge (not
endorsed); integer logical FKs (DB-06); `users.api_key` (DB-07); `ON-DISK-CONTRACT.md` (no
customer-visible *identifier* changes — the header label is a value, not a name).
