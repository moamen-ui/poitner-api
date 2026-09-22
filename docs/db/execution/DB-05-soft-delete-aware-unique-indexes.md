# DB-05 — Soft-delete-aware unique indexes; retire the four raw `_global` indexes

Review findings: S-4, S-5, M-2; cross-review AGY 1.3 / GLM (`plans`, `app_settings` folded in
2026-09-22 by caller decision). Rules: R4 (small tables → plain ops), R9, R10, R11, R13.
**Class: index Expand/Contract in one migration** (each old unique index is replaced by a new one
that is at least as strict on live rows). One migration; **10** unique indexes replaced; no table
data changes.

## 1. Goal

A soft-deleted user, role, environment, status label, extension origin, subscription, role
override, plan (name or slug) or app setting key no longer blocks re-creating the same value
(today: unique violation → raw 500). The `owner_id IS NULL` "global bucket" uniqueness that four hand-written
SQL indexes provide moves into the EF model, so the snapshot finally describes production.
User-visible reason: deleting a stakeholder and inviting them again with the same e-mail works.

## 2. Prerequisites (verified facts)

| Table | Mapping line (current) | Raw index to drop (migration) |
|---|---|---|
| `users` | `UserMapping.cs:26` `b.HasIndex(x => new { x.Email, x.OwnerId }).IsUnique();` | `ix_users_email_global` (`20260629130828_AddTenancy.cs:149`) |
| `roles` | `RoleMapping.cs:24` `b.HasIndex(x => new { x.Name, x.OwnerId }).IsUnique();` | `ix_roles_name_global` (`20260629131255_AddTenantRoleNameIndex.cs:24`) |
| `app_environments` | `AppEnvironmentMapping.cs:22` `b.HasIndex(x => new { x.Name, x.OwnerId }).IsUnique();` | — (never had one; NULL-owner duplicates are possible today) |
| `status_presentations` | `StatusPresentationMapping.cs:20` `b.HasIndex(x => new { x.StatusValue, x.OwnerId }).IsUnique();` | `ix_status_presentations_status_value_global` (`AddTenancy.cs:148`) |
| `extension_sites` | `ExtensionSiteMapping.cs:26` `b.HasIndex(x => new { x.OwnerId, x.Origin }).IsUnique();` | — |
| `subscriptions` | `SubscriptionMapping.cs:24` `b.HasIndex(x => x.OwnerId).IsUnique();` | — |
| `role_tenant_overrides` | `RoleTenantOverrideMapping.cs:24` `b.HasIndex(x => new { x.RoleId, x.OwnerId }).IsUnique();` | — |
| `plans` | `PlanMapping.cs:25` `b.HasIndex(x => x.Name).IsUnique();` and `:27` `b.HasIndex(x => x.Slug).IsUnique();` | — (global table, no `owner_id`) |
| `app_settings` | `AppSettingMapping.cs:25` `b.HasIndex(x => x.Key).IsUnique();` | — (global table, no `owner_id`) |
| `projects` | (already partial, `ProjectMapping.cs:28`) | `ix_projects_key_global` (`AddTenancy.cs:147`) — permanently empty since `owner_id` is NOT NULL (`20260827130246`) |

- Precedent for the target shape: `WorkspaceSettingMapping.cs:30` `b.HasIndex(x => x.OwnerId).IsUnique().HasFilter("deleted_at IS NULL").AreNullsDistinct(false);` → generated as `CreateIndex(..., unique: true, filter: "deleted_at IS NULL").Annotation("Npgsql:NullsDistinct", false)` (`20260921215630:42-48`). Postgres 15 (`docker-compose.prod.yml:6`) supports `NULLS NOT DISTINCT`.
- Precedent for replacing an index: `20260831153128_MakeProjectKeyIndexFilterSoftDeleted` (DropIndex → CreateIndex with filter).
- All these tables are tiny (single workspace): plain `DropIndex`/`CreateIndex` inside the migration transaction is fine (R4).
- `Plan` and `AppSetting` are `BaseEntity` (soft-deletable). `PlanService.DeleteAsync` **soft-deletes** (`Application/Services/Implementation/PlanService.cs:132` `plan.DeletedAt = DateTime.UtcNow;`), and its duplicate checks already filter `DeletedAt == null` (`:56,60,93,96`) — so today a deleted plan's name/slug is burned by the unfiltered index. `app_settings` has **no** delete path (`SettingsService.cs` only reads/upserts by `Key`); it is folded in for R9 uniformity, not because of a live bug.
- Email duplicate checks in code stay as they are (`AuthService.cs:57`, `UserService.cs:64-68`, `TenantService.cs:141-150`) — they already filter `DeletedAt == null`.
- DB-02 guard: `Up()` contains `DropIndex` and `.Sql(` → marker required.

## 3. Design

Each listed mapping line becomes (name pattern `ux_<table>_<cols>_live`):

| Table | New mapping |
|---|---|
| `users` | `b.HasIndex(x => new { x.Email, x.OwnerId }).IsUnique().HasFilter("deleted_at IS NULL").AreNullsDistinct(false).HasDatabaseName("ux_users_email_owner_live");` |
| `roles` | `…new { x.Name, x.OwnerId }… .AreNullsDistinct(false).HasDatabaseName("ux_roles_name_owner_live")` |
| `app_environments` | `…new { x.Name, x.OwnerId }… .AreNullsDistinct(false).HasDatabaseName("ux_app_environments_name_owner_live")` |
| `status_presentations` | `…new { x.StatusValue, x.OwnerId }… .AreNullsDistinct(false).HasDatabaseName("ux_status_presentations_status_owner_live")` |
| `extension_sites` | `…new { x.OwnerId, x.Origin }… .HasFilter("deleted_at IS NULL").HasDatabaseName("ux_extension_sites_owner_origin_live")` (owner NOT NULL — no nulls clause) |
| `subscriptions` | `b.HasIndex(x => x.OwnerId).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName("ux_subscriptions_owner_live");` |
| `role_tenant_overrides` | `…new { x.RoleId, x.OwnerId }… .HasFilter("deleted_at IS NULL").HasDatabaseName("ux_role_tenant_overrides_role_owner_live")` |
| `plans` (name) | `b.HasIndex(x => x.Name).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName("ux_plans_name_live");` (global table, no `owner_id` → no nulls clause) |
| `plans` (slug) | `b.HasIndex(x => x.Slug).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName("ux_plans_slug_live");` |
| `app_settings` | `b.HasIndex(x => x.Key).IsUnique().HasFilter("deleted_at IS NULL").HasDatabaseName("ux_app_settings_key_live");` |

Expected migration `SoftDeleteAwareUniqueIndexes`, `Up()`:
1. **10** × `DropIndex` (EF names: `IX_users_email_owner_id`, `IX_roles_name_owner_id`, `IX_app_environments_name_owner_id`, `IX_status_presentations_status_value_owner_id`, `IX_extension_sites_owner_id_origin`, `IX_subscriptions_owner_id`, `IX_role_tenant_overrides_role_id_owner_id`, `IX_plans_name`, `IX_plans_slug`, `IX_app_settings_key` — confirm against the generated file; EF derives them from the snapshot).
2. **10** × `CreateIndex(… unique: true, filter: "deleted_at IS NULL")`, **four** of them (users, roles, app_environments, status_presentations) with `.Annotation("Npgsql:NullsDistinct", false)`; the other six have no nulls annotation.
3. **Hand-added** at the end of `Up()`:
   ```csharp
   migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_email_global;");
   migrationBuilder.Sql("DROP INDEX IF EXISTS ix_roles_name_global;");
   migrationBuilder.Sql("DROP INDEX IF EXISTS ix_status_presentations_status_value_global;");
   migrationBuilder.Sql("DROP INDEX IF EXISTS ix_projects_key_global;");
   ```
   and in `Down()`, after the generated re-creation of the old indexes, the four `CREATE UNIQUE INDEX … WHERE owner_id IS NULL;` statements copied verbatim from `AddTenancy.cs:147-149` and `AddTenantRoleNameIndex.cs:24`.

Existing rows: untouched. Live-row uniqueness is unchanged or stricter (NULL owners now compared
equal — already true via the raw indexes for users/roles/status_presentations; **new** for
`app_environments`, hence the pre-check). Soft-deleted rows stop participating.

Pre-check (rehearsal; each must return zero rows):
```sql
SELECT email, owner_id, count(*) FROM users WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT name, owner_id, count(*) FROM roles WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT name, owner_id, count(*) FROM app_environments WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT status_value, owner_id, count(*) FROM status_presentations WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT owner_id, origin, count(*) FROM extension_sites WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT owner_id, count(*) FROM subscriptions WHERE deleted_at IS NULL GROUP BY 1 HAVING count(*)>1;
SELECT role_id, owner_id, count(*) FROM role_tenant_overrides WHERE deleted_at IS NULL GROUP BY 1,2 HAVING count(*)>1;
SELECT name, count(*) FROM plans WHERE deleted_at IS NULL GROUP BY 1 HAVING count(*)>1;
SELECT slug, count(*) FROM plans WHERE deleted_at IS NULL GROUP BY 1 HAVING count(*)>1;
SELECT key, count(*) FROM app_settings WHERE deleted_at IS NULL GROUP BY 1 HAVING count(*)>1;
```
(The last three cannot fail today — the unfiltered index already forbids duplicates among *all*
rows — they are here so the ten checks mirror the ten indexes.)
(`GROUP BY` treats NULLs as equal, matching `NULLS NOT DISTINCT`.)

## 4. Safety classification

Index Expand/Contract (R9 target shape; R2 does not apply — no data shape changes). Marker:
`// DB-RULES: index change approved <date> by <owner>`. Dump `pre-db05`; R7 explicit step because a
pre-check miss would fail the boot.

## 5. File-level tasks

1. Edit the **ten** mapping lines per §3 (seven owner-scoped ones, `PlanMapping.cs:25`, `PlanMapping.cs:27`, `AppSettingMapping.cs:25`; replace in place; keep surrounding lines).
2. `just migrate name="SoftDeleteAwareUniqueIndexes"`; open it; verify `Up()` = **10** `DropIndex` + **10** `CreateIndex` (4 with the NullsDistinct annotation) and nothing else; append the four `Sql` drops; mirror in `Down()`; add the marker. Any extra operation → stop and report (a stray `AlterColumn` here would mean a mapping typo).
3. `Tests/SoftDeleteUniqueIndexTests.cs` (§6). 4. `just fmt`, `just test`. 5. Rehearsal with the **ten** pre-checks, then `\di+ ux_*` shows the ten new indexes and `\di ix_*_global` shows none.

## 6. Tests

New `Tests/SoftDeleteUniqueIndexTests.cs` using the Sqlite `TestDb` fixture (`Tests/UsageEventFirstCommentTests.cs:43-60`; Sqlite honours `HasFilter` strings and `IsUnique`; it ignores the Npgsql nulls annotation, so the NULL-owner case is covered by the rehearsal only):

1. `User_SameEmail_AfterSoftDelete_IsAllowed` — user A (`Email="x@e", OwnerId=T`), set `DeletedAt`, save; user B same email/owner → saves.
2. `User_SameEmail_LiveDuplicate_IsRejected` — two live users same email/owner → `DbUpdateException`.
3. `Role_SameName_AfterSoftDelete_IsAllowed` — same pattern on `Role`.
4. `Subscription_SecondLive_IsRejected_AfterSoftDelete_IsAllowed` — one fact with both halves.
5. `Plan_SameSlug_AfterSoftDelete_IsAllowed` — plan A (`Slug="pro"`), set `DeletedAt`, save; plan B same slug → saves; a third **live** plan with slug `pro` → `DbUpdateException`.

## 7. Acceptance criteria

1. `grep -c 'HasFilter("deleted_at IS NULL")' Infrastructure/Mappings/*.cs` (summed over files) increased by exactly **10** versus `main`.
2. `grep -c "_global" Infrastructure/Migrations/*_SoftDeleteAwareUniqueIndexes.cs` → 8 (4 in `Up`, 4 in `Down`).
3. Snapshot contains the **ten** `ux_*_live` names (`ux_users_email_owner_live`, `ux_roles_name_owner_live`, `ux_app_environments_name_owner_live`, `ux_status_presentations_status_owner_live`, `ux_extension_sites_owner_origin_live`, `ux_subscriptions_owner_live`, `ux_role_tenant_overrides_role_owner_live`, `ux_plans_name_live`, `ux_plans_slug_live`, `ux_app_settings_key_live`) and none of `IX_users_email_owner_id`, `IX_plans_name`, `IX_plans_slug`, `IX_app_settings_key` etc.
4. All **five** new tests pass; `just test` green (including `MigrationSafetyTests` with the marker present).
5. Rehearsal: the ten pre-checks return no rows; after update `SELECT indexname FROM pg_indexes WHERE indexname LIKE 'ix_%_global'` → 0 rows and `SELECT count(*) FROM pg_indexes WHERE indexname LIKE 'ux_%_live'` → at least 10; inserting a second live user with an existing email in psql fails with `ux_users_email_owner_live`; inserting a second live plan with an existing slug fails with `ux_plans_slug_live`.

## 8. Rollback

`Down()` recreates the ten unfiltered indexes and the four raw ones. Rollback **fails** if, in
between, a soft-deleted duplicate was re-created live (exactly the case this doc enables) — then
the offending live rows must be deleted first. Dump `pre-db05` required (R5).

## 9. Release steps

`git pull` → `stop api` → `backup-db.sh pre-db05` → run the ten pre-check queries on prod (all
empty) → `up -d --build api` → log grep → smoke: invite/create a user with an e-mail that belonged
to a deleted user in the same workspace (should succeed); `GET /api/plans` still lists the seeded
plans. (Once DB-09 ships, this is `POINTER_APPLY_CONTRACT=1 POINTER_CONTRACT_LABEL=pre-db05 bash
scripts/deploy-api.sh` — if DB-05 lands first, the manual R7 sequence above applies as written.)

## 10. Out of scope

`projects.key` index (already correct), `api_keys`, `workspace_settings`, `invites.code`,
`quick_access_links.token_hash`, `device_logins` (all already filtered or on tables whose rows are
never soft-deleted by a code path *and* whose values are random hashes), any service-layer
uniqueness check, `clients/`, dashboard.

**Amendment log:** 2026-09-22 — `plans(name)`, `plans(slug)`, `app_settings(key)` moved from
out-of-scope into §2/§3/§5/§7 (cross-review AGY 1.3; `PlanService.cs:132` proves plans are
soft-deleted). Design otherwise unchanged.
