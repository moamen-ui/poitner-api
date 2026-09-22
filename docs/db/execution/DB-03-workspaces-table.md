# DB-03 — `workspaces` table, `owner_id` foreign keys, complete tenant hard-delete

Review findings: S-1, S-2, I-1 (deferred), M-1. Rules: R1, R2, R3, R7, R8, R10, R11, R13.
**Class: Expand** (new table + backfill into it + 23 FK constraints + code). Two migrations, one
release. Owner decisions **Q2 and Q3** (review §8) must be answered before task 1.
**Amended 2026-09-22** after cross-review: §3.2 index census (GLM B1), §3.3 two-scaffold recipe
(GLM B4) and soft-deleted-admin caveat (GLM B5), §3.4 explicit delete calls (AGY 3.1). Requires
[DB-09](DB-09-migration-apply-gate.md) to have shipped (both migrations carry markers and therefore
`[ContractMigration("DB-03")]`, and release goes through the explicit `deploy-api.sh` path).

## 1. Goal

Give the workspace a row of its own. Today a workspace is a uuid copied into `owner_id` on 23
tables with no table behind it; identity is recovered by matching a role *name*, a typo can create
an unreachable tenant silently, and tenant deletion misses 17 of 23 tables and fails outright when
a suggestion notification exists (`DemoCleanupService` retries hourly). After this doc: every
`owner_id` references `workspaces(id)`; creating a workspace inserts a row; deleting one is a single
ordered routine whose completeness a test enforces. **Nothing customer-visible changes**: the JWT
`tenant` claim, every `OwnerId` property, every DTO and every query filter stay exactly as they are,
because `workspaces.id` *is* today's `owner_id` value.

## 2. Prerequisites (verified facts)

- `BaseEntity` has `int Id` (`Domain/Entity/BaseEntity.cs:5`); `Repository<T>`/`UnitOfWork.Repository<T>()` are constrained to `BaseEntity` (`Infrastructure/Repository/Repository.cs:7`, `UnitOfWork.cs:11`). `UsageEvent` is the precedent for a non-`BaseEntity` entity exposed as a `DbSet` on the unit of work (`Application/Abstractions/IUnitOfWork.cs:9`, `Infrastructure/Repository/UnitOfWork.cs:21`, `AppDbContext.cs:61`).
- `AppDbContext.SaveChangesAsync` stamps audit columns only for `BaseEntity` entries (`AppDbContext.cs:168-194`) — a `Workspace` must set its own `CreatedAt/CreatedBy`.
- Query filters: strict-own shape `AppDbContext.cs:77`; the Project filter is the one to copy.
- `TenantStamp.OwnerFor` (`Application/Common/TenantStamp.cs:11`) returns `null` for super admins.
- Workspace mint points (each builds a `User` with `OwnerId = publicId` and then `AddAsync` + `SaveChangesAsync`):
  1. `Application/Services/Implementation/AuthService.cs:406-421` (`RegisterAsync`, pending approval),
  2. `Application/Services/Implementation/TenantService.cs:170-185` (`CreateAsync`, super admin),
  3. `Application/Services/Implementation/InviteService.cs:602-613` (`AcceptAsync` new-workspace branch),
  4. `Application/Services/Implementation/DemoService.cs:89-100` (demo provisioning; `ExpiresAt` set, `IsDemo = true`).
  Check: `grep -n "OwnerId = publicId" Application/Services/Implementation/*.cs` prints exactly `AuthService.cs:417`, `AuthService.cs:442` (subscription — not a mint), `DemoService.cs:96,111,123,138,153` (first is the user; the rest are the demo project/actions/statuses), `InviteService.cs:612,655`, `TenantService.cs:181,191`. If the line numbers moved, re-locate by the `OwnerId = publicId` text; if a **new** file appears, stop and report.
- Workspace name today: the admin's `display_name`, resolved live by `AuthService.ResolveTenantNameAsync` (`AuthService.cs:157-170`). `Invite.DisplayName` (`Invite.cs:63`, ≤120) is the name a super admin typed for a new-workspace invite.
- Enumeration of tenants: `TenantService.ListAsync` (`TenantService.cs:36-45`) by `Role.Name == "Workspace Admin"`; constants `TenantService.cs:19`, `UserService.cs:19-20`, `InviteService.cs:24-25`.
- Hard delete: `TenantService.HardDeleteAsync(Guid tenantId)` (`TenantService.cs:356-441`) — resolves `realOwnerId` (`:374-375`), deletes owner files (`:378`), then inside `ExecuteInTransactionAsync` removes Replies, Comments, Projects, StatusPresentations, Users, Roles (`:388-437`). Called by `API/Hosted/DemoCleanupService.cs:78`.
- FK behaviours that constrain delete order: `comments.project_id` Restrict (`CommentMapping.cs:72`); `notifications.project_id` Restrict, `comment_id` Cascade, `suggestion_id` Cascade (`NotificationMapping.cs:33-35`); `users.role_id` Restrict (`UserMapping.cs:46-49`); `api_keys.user_id` Cascade (`ApiKeyMapping.cs:33-36`); `project_app_urls`, `project_builds`, `page_context_snapshots` cascade from `projects`; `subscriptions.plan_id` Restrict.
- The 23 tables with `owner_id` and their mapping file + line of `b.Property(x => x.OwnerId)`: `ai_rules` (`AiRuleMapping.cs:21`, NOT NULL), `api_keys` (`ApiKeyMapping.cs:24`), `app_environments` (`AppEnvironmentMapping.cs:23`), `comments` (`CommentMapping.cs:43`, NOT NULL), `device_logins` (`DeviceLoginMapping.cs:28`), `extension_sites` (`ExtensionSiteMapping.cs:23`, NOT NULL, CLR `Guid`), `invites` (`InviteMapping.cs:24`), `notifications` (`NotificationMapping.cs:23`), `page_context_snapshots` (`PageContextSnapshotMapping.cs:27`), `predefined_action_suggestions` (`PredefinedActionSuggestionMapping.cs:23`), `predefined_actions` (`PredefinedActionMapping.cs:26`, NOT NULL), `project_app_urls` (`ProjectAppUrlMapping.cs:29`, NOT NULL), `project_builds` (`ProjectBuildMapping.cs:25`), `projects` (`ProjectMapping.cs:44`, NOT NULL), `quick_access_links` (`QuickAccessLinkMapping.cs:15`), `replies` (`ReplyMapping.cs:27`), `role_tenant_overrides` (`RoleTenantOverrideMapping.cs:22`, NOT NULL, CLR `Guid`), `roles` (`RoleMapping.cs:30`), `status_presentations` (`StatusPresentationMapping.cs:24`), `subscriptions` (`SubscriptionMapping.cs:23`, NOT NULL, CLR `Guid`), `usage_events` (`UsageEventMapping.cs:16`), `users` (`UserMapping.cs:39`), `workspace_settings` (`WorkspaceSettingMapping.cs:23`).
- Test fixtures: InMemory `AppDbContext` + `FakeCurrentUser` (`Tests/CommentFieldsTests.cs:28-36,61-62`); Sqlite shared-cache fixture that enforces FKs and unique indexes (`Tests/UsageEventFirstCommentTests.cs:43-60`, `TestDb`); tenancy tests (`Tests/TenantQueryFilterTests.cs:33-48`). InMemory does **not** enforce FKs — FK tests must use the Sqlite fixture.
- Migration commands: `just migrate name="…"` (`justfile:6`); rehearsal R11 in `DB-RULES.md`.
- `Tests/Pointer.Tests.csproj` references the API project (it uses `Pointer.API.Seed.AdminSeeder`), so the test project can reference `TenantService` and `Domain` types.

## 3. Design

### 3.1 Entity and mapping

`Domain/Entity/Workspace.cs` — **not** a `BaseEntity` (PK is `Guid`):

| property | column | type | null | notes |
|---|---|---|---|---|
| `Id` | `id` | `uuid` PK | no | `ValueGeneratedNever()` — always the pre-chosen `publicId` |
| `Name` | `name` | `varchar(120)` | no | initial value = founding admin's display name |
| `CreatedAt` | `created_at` | `timestamptz` | no | set by code |
| `CreatedBy` | `created_by` | `uuid` | no | the founding user's `PublicId` |
| `UpdatedAt` / `UpdatedBy` | `updated_at` / `updated_by` | | yes | reserved |
| `DeletedAt` / `DeletedBy` | `deleted_at` / `deleted_by` | | yes | reserved (hard delete is the only delete today) |

Table `workspaces`. Query filter (add to `AppDbContext.OnModelCreating`):
`b.Entity<Workspace>().HasQueryFilter(e => currentUser.IsSuperAdmin || (currentUser.TenantId != null && e.Id == currentUser.TenantId));`

`DbSet<Workspace> Workspaces` on `AppDbContext` and on `IUnitOfWork`/`UnitOfWork` (same shape as `UsageEvents`).

### 3.2 Foreign keys (all 23 tables)

In each mapping listed in §2, directly after the `OwnerId` property line, add:

```csharp
b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.OwnerId)
    .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_<table>_workspaces_owner_id");
```

Exception — `usage_events`: `.OnDelete(DeleteBehavior.SetNull)` (analytics survive a tenant's deletion with `owner_id = NULL`; Q4 in the review). Nullable `owner_id` columns stay nullable (NULL = super admin / global bucket / pre-approval).

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
| `extension_sites` | `(owner_id, origin)` unique (`ExtensionSiteMapping.cs:26`) | none expected; **accept either** (after DB-05 the index is filtered — EF 8 still treats it as covering) |
| `subscriptions` | `owner_id` unique (`SubscriptionMapping.cs:24`) | none expected; **accept either** (same reason) |
| `workspace_settings` | `owner_id` unique, filtered (`WorkspaceSettingMapping.cs:30`) | none expected; **accept either** |
| all 15 others (`ai_rules :29`, `app_environments :24`, `comments :44`, `invites :36`, `notifications :39`, `page_context_snapshots :46`, `predefined_action_suggestions :34` `(owner_id, status)`, `predefined_actions :36`, `project_app_urls :43`, `projects :45`, `replies :28`, `roles :31`, `status_presentations :25`, `usage_events :42` `(owner_id, …)`, `users :40`) | yes, owner-leading | **none** |

So Migration 2 contains **exactly 5** `CreateIndex` operations plus **0–3** optional ones from the
"accept either" rows. Any `CreateIndex` on a table not in the first eight rows, or any operation
that is not `AddForeignKey`/`CreateIndex`, → **stop and report**; do not delete operations to make
the file match.

### 3.3 Migrations (two files, this order)

**Migration 1 — `AddWorkspaces`** (generated; edit as described):

1. `CreateTable("workspaces", …)` as in §3.1 with `PK_workspaces`.
2. Marker line above `Up(`: `// DB-RULES: R3 backfill approved <date> by <owner>`.
3. **Inserted by hand after `CreateTable`** (R3: insert-only into the new table; idempotent):
   ```sql
   -- 3a. one row per founding Workspace Admin
   INSERT INTO workspaces (id, name, created_at, created_by)
   SELECT u.owner_id, left(u.display_name, 120), u.created_at, u.public_id
   FROM users u JOIN roles r ON r.id = u.role_id
   WHERE r.name = 'Workspace Admin' AND u.owner_id IS NOT NULL AND u.deleted_at IS NULL
   ON CONFLICT (id) DO NOTHING;
   -- 3b. any owner_id still unmatched anywhere (Q2 default: attach, do not abort)
   INSERT INTO workspaces (id, name, created_at, created_by)
   SELECT DISTINCT o.owner_id, 'Recovered ' || left(o.owner_id::text, 8), now(), '00000000-0000-0000-0000-000000000000'::uuid
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
   WHERE o.owner_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = o.owner_id);
   ```
   **How to keep the FKs out of this file without hand-moving anything (two-scaffold recipe):**
   write the 23 FK lines of §3.2 into the mappings **commented out** (prefix each with
   `// DB-03 step 2: `), scaffold Migration 1 with `just migrate name="AddWorkspaces"` — it then
   contains only `CreateTable("workspaces")` (+ `PK_workspaces`) — add the marker, the attribute
   and the two SQL blocks by hand, **then** remove the `// DB-03 step 2: ` prefixes from all 23
   lines and scaffold Migration 2. Never move operations between generated files.
4. `Down`: `DropTable("workspaces")` (generated).

**Migration 2 — `AddWorkspaceForeignKeys`** (scaffolded with `just migrate` after uncommenting the
23 FK lines): 23 × `AddForeignKey` + the `CreateIndex` ops from the §3.2 table (5 expected, up to
3 optional). `Down` drops them. It contains no risky operation, so no marker and no
`[ContractMigration]` — R13 still applies: the file must contain **only**
`AddForeignKey`/`CreateIndex`/their inverses.

**Expected state of every existing row:** unchanged. Every existing `owner_id` value gains a matching `workspaces` row (3a or 3b) *before* any FK is created, so no FK creation can fail. NULL `owner_id` rows are untouched (FKs ignore NULL).

**Soft-deleted founding admins (GLM B5).** 3a deliberately requires `u.deleted_at IS NULL`. A
workspace whose Workspace Admin rows are *all* soft-deleted therefore gets its row from 3b, named
`Recovered <id>`, even though it is a perfectly reachable tenant. That is not corruption — it is a
workspace with no live admin. Run the fourth query below to know how many of the `Recovered` rows
are of this kind before reporting the count to the owner (Q2).

Rehearsal pre-check (R11 step 4). The first two must print the same number; the third is the Q2
count; the fourth explains how many of the third are "no live admin" rather than true orphans:
```sql
SELECT count(*) FROM users u JOIN roles r ON r.id=u.role_id WHERE r.name='Workspace Admin' AND u.deleted_at IS NULL AND u.owner_id IS NOT NULL;
SELECT count(*) FROM workspaces WHERE name NOT LIKE 'Recovered %';
SELECT count(*) FROM workspaces WHERE name LIKE 'Recovered %';   -- Q2: report this number to the owner; expected 0
SELECT count(DISTINCT u.owner_id) FROM users u JOIN roles r ON r.id=u.role_id
 WHERE r.name='Workspace Admin' AND u.owner_id IS NOT NULL AND u.deleted_at IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM users u2 JOIN roles r2 ON r2.id=u2.role_id
                   WHERE u2.owner_id=u.owner_id AND u2.deleted_at IS NULL AND r2.name='Workspace Admin');
-- ^ workspaces whose only admins are soft-deleted: these appear as 'Recovered …' by design
```

### 3.4 Code changes

- **Mint**: at each of the four mint points, immediately before `AddAsync(<user>)`, add
  `await _unitOfWork.Workspaces.AddAsync(new Workspace { Id = publicId, Name = <name>, CreatedAt = DateTime.UtcNow, CreatedBy = publicId });`
  where `<name>` is the same expression used for the user's `DisplayName` at that site (`request.DisplayName`, `request.DisplayName.Trim()`, `"Demo User"`), truncated to 120 with `[..Math.Min(120, s.Length)]`. The existing `SaveChangesAsync` that follows persists both.
- **HardDelete** (amended, AGY 3.1 — **no loop, no reflection in production code**): replace the six blocks at `TenantService.cs:388-437` with **22 explicit, sequential calls** in exactly this order (children before parents):
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
- `ListAsync`, `ResolveTenantNameAsync`, `TransferOwnershipAsync`, JWT claims, DTOs: **unchanged** (stage B later).

## 4. Safety classification

**Expand** (R1 + R3 backfill into a new table + constraints). No column is dropped, renamed or narrowed. Auto-apply on boot is *technically* allowed by R7, but because Migration 2 fails hard if 3b somehow left an orphan, release with the R7 explicit step (API stopped, human reading the log). No owner approval line required (nothing destructive); owner answers Q2/Q3 before task 1.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — new class per §3.1 (plain properties; doc-comment: "One row per workspace. `Id` equals every `owner_id` that belongs to it and the JWT `tenant` claim. Not a `BaseEntity`: the PK is a uuid chosen before insert.").
2. `Infrastructure/Mappings/WorkspaceMapping.cs` — new `IEntityTypeConfiguration<Workspace>`: `ToTable("workspaces")`, `HasKey(x => x.Id)`, `Property(x => x.Id).HasColumnName("id").ValueGeneratedNever()`, `Name` `.HasColumnName("name").IsRequired().HasMaxLength(120)`, the six audit columns with the same `HasColumnName` calls every other mapping uses.
3. `Infrastructure/AppDbContext.cs` — add `public DbSet<Workspace> Workspaces => Set<Workspace>();` after line 63; add the query filter from §3.1 after line 147 with a two-line comment.
4. `Application/Abstractions/IUnitOfWork.cs` — add `DbSet<Workspace> Workspaces { get; }` after line 9. `Infrastructure/Repository/UnitOfWork.cs` — add `public DbSet<Workspace> Workspaces => db.Workspaces;` after line 21.
5. The 23 mapping files (§2 list) — add the FK line from §3.2 after the `OwnerId` property line, **commented out** with the prefix `// DB-03 step 2: ` (so the first scaffold does not see them); `usage_events` uses `SetNull`.
6. `just migrate name="AddWorkspaces"` → open the generated file. `Up()` must contain **only** `CreateTable("workspaces", …)` (with `PK_workspaces`) and `Down()` only `DropTable`. **If it contains any `AddForeignKey` or `CreateIndex`, a step-5 line was not commented out — stop and report.** If the `CreateTable` has a column not in §3.1 → stop and report. Then add, by hand: the marker line and `[ContractMigration("DB-03")]` (DB-09 §3) above the class/`Up(`, and the two SQL blocks (3a, 3b) after `CreateTable`.
7. Remove the `// DB-03 step 2: ` prefix from all 23 lines (`grep -rn "DB-03 step 2" Infrastructure/Mappings | wc -l` → 0 afterwards). `just migrate name="AddWorkspaceForeignKeys"` → open it: `Up()` = 23 × `AddForeignKey` + the `CreateIndex` ops listed in the §3.2 table (5, plus up to 3 optional), nothing else. Anything else → stop and report. No marker, no attribute (no risky operation).
8. Mint points (§2, four files) — add the `Workspaces.AddAsync` line per §3.4.
9. `Application/Services/Implementation/TenantService.cs:388-437` — replace with the 22 explicit `await DeleteOwnedAsync<T>(realOwnerId)` calls and the helper per §3.4; add `internal static readonly Type[] HardDeleteOrder` (22 entries, same order). Add `[assembly: InternalsVisibleTo("Pointer.Tests")]` only if the Application project does not already expose internals to tests (check `Application/*.csproj` and existing `InternalsVisibleTo`; if absent, make the array `public static readonly` instead — either is acceptable).
10. Tests (§6). 11. `just fmt`, `just test`. 12. Rehearsal (R11) with the §3.3 pre-check queries; paste the three counts into the PR.
13. Regenerate nothing under `clients/` (no endpoint changed). Update `docs/db/SCHEMA.md`'s `workspaces` row from "planned" to present (one-line edit) — the only doc edit allowed here.

## 6. Tests

New file `Tests/WorkspaceTests.cs` (copy the Sqlite `TestDb` fixture verbatim from `Tests/UsageEventFirstCommentTests.cs:43-60`; copy `FakeCurrentUser` from `Tests/CommentFieldsTests.cs:28-36`):

1. `Project_WithUnknownOwner_IsRejectedByForeignKey` — insert a `Project` whose `OwnerId` has no `workspaces` row → `Assert.ThrowsAsync<DbUpdateException>`. (Tenancy-integrity proof.)
2. `TenantService_CreateAsync_MintsWorkspaceRow` — build `TenantService` the way `Tests/TenantInviteServiceTests.cs` or `WorkspaceAdminOwnershipTests.cs` builds it; call `CreateAsync`; assert `db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == result.Data.OwnerId).Name == request.DisplayName`.
3. `HardDeleteOrder_CoversEveryOwnerCarryingEntity` — reflection: every type in `typeof(BaseEntity).Assembly` that is a class, not abstract, has a property named `OwnerId`, and is not `Workspace` or `UsageEvent`, must be contained in `TenantService.HardDeleteOrder`; and `HardDeleteOrder.Length == 22`. Failure message names the missing type. (This is the rule R8 point 5 enforcer. The array is test input only — see §3.4.)
4. `HardDelete_RemovesEverything_EvenWithSuggestionNotification` — seed one workspace with a project, a comment, a suggestion, a `Notification { CommentId = null, SuggestionId = …, ProjectId = … }`, a subscription, a workspace_settings row; call `HardDeleteAsync`; assert `IsSuccess` and, for every type in `HardDeleteOrder`, `Set<T>().IgnoreQueryFilters().Count(x => x.OwnerId == id) == 0`, and `Workspaces.Count() == 0`.
5. `Workspace_TenantB_CannotReadTenantA` — InMemory is fine here: two workspaces, context for tenant B, `db.Workspaces.ToList()` returns only B. (Copy shape from `TenantQueryFilterTests.Project_TenantA_SeesOnlyOwnRows`.)

"Existing data survives" is proven by the R11 rehearsal counts (§3.3) — the migrations are Npgsql-specific and cannot run under Sqlite; say so in the PR.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` shows exactly two new ids ending in `_AddWorkspaces` and `_AddWorkspaceForeignKeys`, in that order.
2. `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → 23; `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaces.cs` → 0; `grep -c "CreateIndex(" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → between 5 and 8, and every `name:` among them is one of `IX_api_keys_owner_id`, `IX_device_logins_owner_id`, `IX_project_builds_owner_id`, `IX_quick_access_links_owner_id`, `IX_role_tenant_overrides_owner_id`, `IX_extension_sites_owner_id`, `IX_subscriptions_owner_id`, `IX_workspace_settings_owner_id`.
3. `grep -rn "HasOne<Workspace>" Infrastructure/Mappings | wc -l` → 23; `grep -rn "DB-03 step 2" Infrastructure/Mappings | wc -l` → 0.
4. `grep -n "Workspaces.AddAsync" Application/Services/Implementation/*.cs | wc -l` → 4; `grep -c "await DeleteOwnedAsync<" Application/Services/Implementation/TenantService.cs` → 22; `grep -c "foreach.*HardDeleteOrder\|HardDeleteOrder\[" Application/Services/Implementation/TenantService.cs` → 0.
5. `just test` green; `Tests/WorkspaceTests.cs` has the 5 facts above; `MigrationSafetyTests` (DB-02) passes with the R3 marker and `[ContractMigration]` present on Migration 1 and neither on Migration 2.
6. Rehearsal: the four §3.3 queries print `N`, `N`, `R`, `S` with `R == S` (every Recovered row is a no-live-admin workspace) — report `R` to the owner either way; `\d comments` shows `fk_comments_workspaces_owner_id`; `SELECT count(*) FROM comments c LEFT JOIN workspaces w ON w.id=c.owner_id WHERE w.id IS NULL` → 0 (repeat for `projects`, `users` where `owner_id IS NOT NULL`).
7. A registered user's JWT still carries the same `tenant` value as before (login → decode → compare to `users.owner_id`): no auth change.
8. `DemoCleanupService` log on the rehearsal API shows `hard-deleted demo tenant` (seed one expired demo first) rather than `HardDeleteAsync returned failure`.

## 8. Rollback

Both migrations have full `Down()`s: drop 23 FKs (+ indexes), then drop `workspaces`. No other table is modified, so `dotnet ef database update <previous id>` (API stopped) restores the prior schema with zero data loss. The dump label for this release is `pre-db03` (R5/R6): `bash scripts/backup-db.sh pre-db03`. If rollback happens after new workspaces were minted, the `workspaces` rows are lost but every `owner_id` still exists in `users` — re-running Migration 1 recreates them (idempotent 3a/3b).

## 9. Release steps

1. Owner confirms Q2 (default: attach "Recovered" rows) and Q3 (name = admin display name).
2. Merge; on the VM: `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db03 bash scripts/deploy-api.sh` (DB-09 path: pulls, stops the API, dumps `pre-db03`, rebuilds with `DBApplyContractMigrations=true`).
3. The script prints the log grep; expect two `Applying migration` lines, one `DB-09: applying 1 contract migration(s)` line, and no error.
4. Verify: `docker compose -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "SELECT id, name FROM workspaces;"` → the expected workspaces, none named `Recovered …` (if any are, do **not** delete them; report to the owner).
5. Smoke: dashboard login, comment list, `GET /api/tenants` (super admin) unchanged.
6. Watch `DemoCleanupService` lines over the next hour for failures.

## 10. Out of scope

Moving demo/workspace fields off `users` (S-8, stage B — separate doc); tightening nullable `owner_id` columns to NOT NULL (I-1 — separate doc after a rehearsal shows zero NULLs); changing `ListAsync`/`ResolveTenantNameAsync` to read `workspaces`; any DTO, controller, `orval.config.ts`, `clients/`, the dashboard repo; `workspace_settings` merge (not endorsed); the four raw `_global` indexes (DB-05); integer logical FKs (DB-06); `users.api_key` (DB-07); `ON-DISK-CONTRACT.md` (no customer-visible name changes).
