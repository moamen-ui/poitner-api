# DB-03 — `workspaces` table, `owner_id` foreign keys, complete tenant hard-delete

Review findings: S-1, S-2, I-1 (deferred), M-1. Rules: R1, R2, R3, R7, R8, R10, R11, R13.
**Class: Expand** (new table + backfill into it + 23 FK constraints + code). Two migrations, one
release. Owner decisions **Q2 and Q3** (review §8) must be answered before task 1.

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

Exception — `usage_events`: `.OnDelete(DeleteBehavior.SetNull)` (analytics survive a tenant's deletion with `owner_id = NULL`; Q4 in the review). Nullable `owner_id` columns stay nullable (NULL = super admin / global bucket / pre-approval). EF will add a conventional index `IX_<table>_owner_id` wherever none exists whose **first** column is `owner_id`; expected new indexes: `extension_sites` (existing index starts with `owner_id` — EF may still emit one; accept either), `role_tenant_overrides`, `subscriptions` (already unique on `owner_id`; expect none). Any other new index in the generated migration → stop and report.

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
   The generated migration will also contain the 23 `AddForeignKey` operations (because the mappings declare them). **Move every `AddForeignKey` (and any `CreateIndex` EF emitted for them) out of this file into Migration 2** so Migration 1 is: CreateTable → SQL 3a → SQL 3b, nothing else.
4. `Down`: `DropTable("workspaces")` (generated).

**Migration 2 — `AddWorkspaceForeignKeys`** (created empty with `just migrate`, then filled with the moved operations): 23 × `AddForeignKey` (+ the accepted `CreateIndex` ops). `Down` drops them. No marker needed (no risky operation) — but R13 applies: the file must contain **only** `AddForeignKey`/`CreateIndex`/their inverses.

**Expected state of every existing row:** unchanged. Every existing `owner_id` value gains a matching `workspaces` row (3a or 3b) *before* any FK is created, so no FK creation can fail. NULL `owner_id` rows are untouched (FKs ignore NULL).

Rehearsal pre-check (R11 step 4), must print the same number twice and then `0`:
```sql
SELECT count(*) FROM users u JOIN roles r ON r.id=u.role_id WHERE r.name='Workspace Admin' AND u.deleted_at IS NULL AND u.owner_id IS NOT NULL;
SELECT count(*) FROM workspaces WHERE name NOT LIKE 'Recovered %';
SELECT count(*) FROM workspaces WHERE name LIKE 'Recovered %';   -- Q2: report this number to the owner; expected 0
```

### 3.4 Code changes

- **Mint**: at each of the four mint points, immediately before `AddAsync(<user>)`, add
  `await _unitOfWork.Workspaces.AddAsync(new Workspace { Id = publicId, Name = <name>, CreatedAt = DateTime.UtcNow, CreatedBy = publicId });`
  where `<name>` is the same expression used for the user's `DisplayName` at that site (`request.DisplayName`, `request.DisplayName.Trim()`, `"Demo User"`), truncated to 120 with `[..Math.Min(120, s.Length)]`. The existing `SaveChangesAsync` that follows persists both.
- **HardDelete**: replace the six blocks at `TenantService.cs:388-437` with a loop over a new `internal static readonly Type[] HardDeleteOrder` (children before parents; every entry must have an `OwnerId` property):
  `Notification, Reply, Comment, PageContextSnapshot, ProjectBuild, ProjectAppUrl, PredefinedActionSuggestion, PredefinedAction, AiRule, QuickAccessLink, Project, ExtensionSite, Invite, StatusPresentation, RoleTenantOverride, WorkspaceSetting, Subscription, ApiKey, DeviceLogin, User, Role, AppEnvironment`.
  Because `Repository<T>` is generic, implement the loop with a private generic helper `Task DeleteOwnedAsync<T>(Guid ownerId) where T : BaseEntity` and a `switch`/dictionary from `Type` to that helper (or call `db.Set<T>()` via the `AppDbContext`; either is fine — no reflection-invoked generics needed if the helper is called once per type in the order above). `UsageEvent` is intentionally absent (FK `SET NULL`). After the loop, delete the `workspaces` row (`_unitOfWork.Workspaces` → `Remove`). Keep the existing `IgnoreQueryFilters()` + `Where(x => x.OwnerId == realOwnerId)` shape.
- `ListAsync`, `ResolveTenantNameAsync`, `TransferOwnershipAsync`, JWT claims, DTOs: **unchanged** (stage B later).

## 4. Safety classification

**Expand** (R1 + R3 backfill into a new table + constraints). No column is dropped, renamed or narrowed. Auto-apply on boot is *technically* allowed by R7, but because Migration 2 fails hard if 3b somehow left an orphan, release with the R7 explicit step (API stopped, human reading the log). No owner approval line required (nothing destructive); owner answers Q2/Q3 before task 1.

## 5. File-level tasks

1. `Domain/Entity/Workspace.cs` — new class per §3.1 (plain properties; doc-comment: "One row per workspace. `Id` equals every `owner_id` that belongs to it and the JWT `tenant` claim. Not a `BaseEntity`: the PK is a uuid chosen before insert.").
2. `Infrastructure/Mappings/WorkspaceMapping.cs` — new `IEntityTypeConfiguration<Workspace>`: `ToTable("workspaces")`, `HasKey(x => x.Id)`, `Property(x => x.Id).HasColumnName("id").ValueGeneratedNever()`, `Name` `.HasColumnName("name").IsRequired().HasMaxLength(120)`, the six audit columns with the same `HasColumnName` calls every other mapping uses.
3. `Infrastructure/AppDbContext.cs` — add `public DbSet<Workspace> Workspaces => Set<Workspace>();` after line 63; add the query filter from §3.1 after line 147 with a two-line comment.
4. `Application/Abstractions/IUnitOfWork.cs` — add `DbSet<Workspace> Workspaces { get; }` after line 9. `Infrastructure/Repository/UnitOfWork.cs` — add `public DbSet<Workspace> Workspaces => db.Workspaces;` after line 21.
5. The 23 mapping files (§2 list) — add the FK line from §3.2 after the `OwnerId` property line; `usage_events` uses `SetNull`.
6. `just migrate name="AddWorkspaces"` → open the generated file, apply §3.3 step 2-3 (marker, two SQL blocks, move FK ops out). **Compare with §3.3; if the generated `CreateTable` has a column not in §3.1 or the FK count is not 23, stop and report.**
7. `just migrate name="AddWorkspaceForeignKeys"` → paste the moved operations into `Up`/`Down`.
8. Mint points (§2, four files) — add the `Workspaces.AddAsync` line per §3.4.
9. `Application/Services/Implementation/TenantService.cs:388-437` — replace with the ordered routine per §3.4; add `internal static readonly Type[] HardDeleteOrder`. Add `[assembly: InternalsVisibleTo("Pointer.Tests")]` only if the Application project does not already expose internals to tests (check `Application/*.csproj` and existing `InternalsVisibleTo`; if absent, make the array `public static readonly` instead — either is acceptable).
10. Tests (§6). 11. `just fmt`, `just test`. 12. Rehearsal (R11) with the §3.3 pre-check queries; paste the three counts into the PR.
13. Regenerate nothing under `clients/` (no endpoint changed). Update `docs/db/SCHEMA.md`'s `workspaces` row from "planned" to present (one-line edit) — the only doc edit allowed here.

## 6. Tests

New file `Tests/WorkspaceTests.cs` (copy the Sqlite `TestDb` fixture verbatim from `Tests/UsageEventFirstCommentTests.cs:43-60`; copy `FakeCurrentUser` from `Tests/CommentFieldsTests.cs:28-36`):

1. `Project_WithUnknownOwner_IsRejectedByForeignKey` — insert a `Project` whose `OwnerId` has no `workspaces` row → `Assert.ThrowsAsync<DbUpdateException>`. (Tenancy-integrity proof.)
2. `TenantService_CreateAsync_MintsWorkspaceRow` — build `TenantService` the way `Tests/TenantInviteServiceTests.cs` or `WorkspaceAdminOwnershipTests.cs` builds it; call `CreateAsync`; assert `db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == result.Data.OwnerId).Name == request.DisplayName`.
3. `HardDeleteOrder_CoversEveryOwnerCarryingEntity` — reflection: every type in `typeof(BaseEntity).Assembly` that is a class, not abstract, has a property named `OwnerId`, and is not `Workspace` or `UsageEvent`, must be contained in `TenantService.HardDeleteOrder`. Failure message names the missing type. (This is the rule R8 point 5 enforcer.)
4. `HardDelete_RemovesEverything_EvenWithSuggestionNotification` — seed one workspace with a project, a comment, a suggestion, a `Notification { CommentId = null, SuggestionId = …, ProjectId = … }`, a subscription, a workspace_settings row; call `HardDeleteAsync`; assert `IsSuccess` and, for every type in `HardDeleteOrder`, `Set<T>().IgnoreQueryFilters().Count(x => x.OwnerId == id) == 0`, and `Workspaces.Count() == 0`.
5. `Workspace_TenantB_CannotReadTenantA` — InMemory is fine here: two workspaces, context for tenant B, `db.Workspaces.ToList()` returns only B. (Copy shape from `TenantQueryFilterTests.Project_TenantA_SeesOnlyOwnRows`.)

"Existing data survives" is proven by the R11 rehearsal counts (§3.3) — the migrations are Npgsql-specific and cannot run under Sqlite; say so in the PR.

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` shows exactly two new ids ending in `_AddWorkspaces` and `_AddWorkspaceForeignKeys`, in that order.
2. `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaceForeignKeys.cs` → 23; `grep -c "AddForeignKey(" Infrastructure/Migrations/*_AddWorkspaces.cs` → 0.
3. `grep -rn "HasOne<Workspace>" Infrastructure/Mappings | wc -l` → 23.
4. `grep -n "Workspaces.AddAsync" Application/Services/Implementation/*.cs | wc -l` → 4.
5. `just test` green; `Tests/WorkspaceTests.cs` has the 5 facts above; `MigrationSafetyTests` (DB-02) passes with the R3 marker present.
6. Rehearsal: the three §3.3 queries print `N`, `N`, `0`; `\d comments` shows `fk_comments_workspaces_owner_id`; `SELECT count(*) FROM comments c LEFT JOIN workspaces w ON w.id=c.owner_id WHERE w.id IS NULL` → 0 (repeat for `projects`, `users` where `owner_id IS NOT NULL`).
7. A registered user's JWT still carries the same `tenant` value as before (login → decode → compare to `users.owner_id`): no auth change.
8. `DemoCleanupService` log on the rehearsal API shows `hard-deleted demo tenant` (seed one expired demo first) rather than `HardDeleteAsync returned failure`.

## 8. Rollback

Both migrations have full `Down()`s: drop 23 FKs (+ indexes), then drop `workspaces`. No other table is modified, so `dotnet ef database update <previous id>` (API stopped) restores the prior schema with zero data loss. The dump label for this release is `pre-db03` (R5/R6): `bash scripts/backup-db.sh pre-db03`. If rollback happens after new workspaces were minted, the `workspaces` rows are lost but every `owner_id` still exists in `users` — re-running Migration 1 recreates them (idempotent 3a/3b).

## 9. Release steps

1. Owner confirms Q2 (default: attach "Recovered" rows) and Q3 (name = admin display name).
2. Merge; on the VM: `git pull`; `docker compose -f docker-compose.prod.yml stop api`; `bash scripts/backup-db.sh pre-db03`.
3. `docker compose --env-file .env.prod -f docker-compose.prod.yml up -d --build api`; `docker compose -f docker-compose.prod.yml logs --since 5m api | grep -iE "migrat|error|exception"` — expect two `Applying migration` lines and no error.
4. Verify: `docker compose -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c "SELECT id, name FROM workspaces;"` → the expected workspaces, none named `Recovered …` (if any are, do **not** delete them; report to the owner).
5. Smoke: dashboard login, comment list, `GET /api/tenants` (super admin) unchanged.
6. Watch `DemoCleanupService` lines over the next hour for failures.

## 10. Out of scope

Moving demo/workspace fields off `users` (S-8, stage B — separate doc); tightening nullable `owner_id` columns to NOT NULL (I-1 — separate doc after a rehearsal shows zero NULLs); changing `ListAsync`/`ResolveTenantNameAsync` to read `workspaces`; any DTO, controller, `orval.config.ts`, `clients/`, the dashboard repo; `workspace_settings` merge (not endorsed); the four raw `_global` indexes (DB-05); integer logical FKs (DB-06); `users.api_key` (DB-07); `ON-DISK-CONTRACT.md` (no customer-visible name changes).
