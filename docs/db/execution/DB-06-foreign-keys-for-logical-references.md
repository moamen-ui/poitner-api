# DB-06 — Real foreign keys for integer logical references; cascade fixes; two redundant indexes

Review findings: S-6, I-2, I-3, S-2 (root cause). Rules: R1, R4, R7, R11, R13. **Class: Additive
(constraints) + two `DropIndex`.** One migration. Owner decision **Q4** (`usage_events`) before
task 1; the default below applies if unanswered. Recommended after DB-03 (so orphan pre-checks have
a single root), but has no code dependency on it.

## 1. Goal

Every `*_id` integer column that names a row in another table is enforced by the database, with a
stated delete behaviour, so hard-deleting a project (tenant delete, demo cleanup) can no longer
leave predefined actions, suggestions, rules or magic links pointing at nothing — and can no longer
be blocked by a stray notification. User-visible reason: demo tenants with suggestions currently
never get cleaned up (S-2).

## 2. Prerequisites (verified facts)

Columns without an FK (mapping line of the property):

| Table.column | Mapping | Target | Nullable | Decided behaviour |
|---|---|---|---|---|
| `ai_rules.project_id` | `AiRuleMapping.cs:22` | `projects(id)` | yes | `Cascade` (a project rule dies with the project) |
| `predefined_actions.project_id` | `PredefinedActionMapping.cs:27` | `projects(id)` | yes | `Cascade` |
| `predefined_action_suggestions.project_id` | `PredefinedActionSuggestionMapping.cs:24` (NOT NULL) | `projects(id)` | no | `Cascade` |
| `quick_access_links.project_id` | `QuickAccessLinkMapping.cs:17` (NOT NULL) | `projects(id)` | no | `Cascade` (link is useless without its project) |
| `quick_access_links.invite_id` | `QuickAccessLinkMapping.cs:18` (NOT NULL) | `invites(id)` | no | `Restrict` (invite is the audit row; `QuickAccessLink.cs:7-10`) |
| `invites.project_id` | `InviteMapping.cs:28` | `projects(id)` | yes | `SetNull` (invite stays valid for the tenant; `Invite.cs:30-34`) |
| `invites.role_id` | `InviteMapping.cs:27` | `roles(id)` | yes | `SetNull` (invitee picks a role on accept; `Invite.cs:27`) |
| `invites.plan_id` | `InviteMapping.cs:34` | `plans(id)` | yes | `Restrict` (plans are never hard-deleted) |
| `role_tenant_overrides.role_id` | `RoleTenantOverrideMapping.cs:21` (NOT NULL) | `roles(id)` | no | `Cascade` |
| `usage_events.project_id` | `UsageEventMapping.cs:17` | `projects(id)` | yes | **Q4 default: `SetNull`** |
| `notifications.project_id` | `NotificationMapping.cs:34` — exists as `Restrict` | `projects(id)` | no | change to `Cascade` (I-2; a notification never outlives its project; `comment_id` is already `Cascade` at `:33`) |

Not touched (uuid references to `users.public_id`, rule R14): `ai_rules.user_id`, `predefined_actions.user_id`, `quick_access_links.user_id`, `device_logins.user_id`, `notifications.user_id/actor_id`, `comments.author_id`, `replies.author_id`.

Redundant indexes: `AiRuleMapping.cs:29` `b.HasIndex(x => x.OwnerId);` (covered by `:30` `(OwnerId, ProjectId)`), `PredefinedActionMapping.cs:36` `b.HasIndex(x => x.OwnerId);` (covered by `:37`).

Navigation properties do not exist for these relationships; EF supports `HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)` without one. EF adds an index for each new FK column unless an existing index starts with that column; expect new `IX_ai_rules_project_id`, `IX_predefined_actions_project_id`, `IX_predefined_action_suggestions_project_id`, `IX_quick_access_links_project_id`, `IX_quick_access_links_invite_id`, `IX_invites_project_id`, `IX_invites_role_id`, `IX_invites_plan_id`, `IX_role_tenant_overrides_role_id`, `IX_usage_events_project_id` (all useful for cascades). Tables are tiny → plain `AddForeignKey` (R4).

DB-02 guard: `Up()` contains `DropIndex` (the two redundant ones) and `DropForeignKey` (notifications) → marker required.

## 3. Design

Mapping additions (one line each, placed after the property line cited):

```csharp
b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);        // ai_rules, predefined_actions, predefined_action_suggestions, quick_access_links
b.HasOne<Invite>().WithMany().HasForeignKey(x => x.InviteId).OnDelete(DeleteBehavior.Restrict);         // quick_access_links
b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);        // invites, usage_events
b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.SetNull);              // invites
b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);             // invites
b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);              // role_tenant_overrides
```
`NotificationMapping.cs:34`: `OnDelete(DeleteBehavior.Restrict)` → `OnDelete(DeleteBehavior.Cascade)`.
Delete `AiRuleMapping.cs:29` and `PredefinedActionMapping.cs:36`.

Expected migration `AddForeignKeysForLogicalReferences`, `Up()`:
1. `DropForeignKey("FK_notifications_projects_project_id")` + `AddForeignKey(… onDelete: Cascade)`.
2. `DropIndex("IX_ai_rules_owner_id")`, `DropIndex("IX_predefined_actions_owner_id")`.
3. 10 × `CreateIndex` (list above) — some may be absent if EF finds an existing prefix index; accept fewer, never more.
4. 10 × `AddForeignKey` with the behaviours in §2.

Existing rows: unchanged **provided no orphans exist**. Pre-check (rehearsal and prod; every query must return 0):
```sql
SELECT count(*) FROM ai_rules a WHERE a.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=a.project_id);
SELECT count(*) FROM predefined_actions a WHERE a.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=a.project_id);
SELECT count(*) FROM predefined_action_suggestions s WHERE NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=s.project_id);
SELECT count(*) FROM quick_access_links q WHERE NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=q.project_id);
SELECT count(*) FROM quick_access_links q WHERE NOT EXISTS (SELECT 1 FROM invites i WHERE i.id=q.invite_id);
SELECT count(*) FROM invites i WHERE i.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=i.project_id);
SELECT count(*) FROM invites i WHERE i.role_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM roles r WHERE r.id=i.role_id);
SELECT count(*) FROM invites i WHERE i.plan_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM plans p WHERE p.id=i.plan_id);
SELECT count(*) FROM role_tenant_overrides o WHERE NOT EXISTS (SELECT 1 FROM roles r WHERE r.id=o.role_id);
SELECT count(*) FROM usage_events e WHERE e.project_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM projects p WHERE p.id=e.project_id);
```
If any count is non-zero on prod: **stop**; report the rows to the owner (they are the orphans S-2
predicted). Do not add a `DELETE` to the migration without a written owner decision.

## 4. Safety classification

Additive constraints (R1) with two index drops and one FK behaviour change. Marker:
`// DB-RULES: R4 constraint approved <date> by <owner>`. Dump `pre-db06`; R7 explicit step (a
pre-check miss fails the boot).

## 5. File-level tasks

1. Eight mapping edits per §3 (`AiRuleMapping`, `PredefinedActionMapping`, `PredefinedActionSuggestionMapping`, `QuickAccessLinkMapping`, `InviteMapping`, `RoleTenantOverrideMapping`, `UsageEventMapping`, `NotificationMapping`), plus the two index deletions.
2. `just migrate name="AddForeignKeysForLogicalReferences"`; compare `Up()` to §3 (counts: 1 DropForeignKey, 2 DropIndex, ≤10 CreateIndex, 11 AddForeignKey); add marker; anything else → stop and report.
3. `Tests/LogicalForeignKeyTests.cs` (§6). 4. `just fmt`, `just test`. 5. Rehearsal with the ten pre-checks.

## 6. Tests

New `Tests/LogicalForeignKeyTests.cs`, Sqlite `TestDb` fixture (`Tests/UsageEventFirstCommentTests.cs:43-60`; Sqlite enforces FKs and cascades under `EnsureCreated`):

1. `PredefinedAction_WithUnknownProject_IsRejected` — insert with `ProjectId = 999` → `DbUpdateException`.
2. `DeletingProject_CascadesActionsSuggestionsRulesLinks` — seed project + one of each child + a suggestion notification (`CommentId = null`); `db.Projects.Remove(project); SaveChanges()` succeeds; all child sets and `Notifications` are empty afterwards.
3. `DeletingProject_SetsInviteAndUsageEventProjectNull` — seed invite + usage event on the project; remove project; both rows survive with `ProjectId == null`.
4. `Invite_WithUnknownPlan_IsRejected`.
5. Tenancy invariant is unaffected (no filter change) — reference `TenantQueryFilterTests` in the PR, no new test.

## 7. Acceptance criteria

1. `grep -c "HasOne<" Infrastructure/Mappings/{AiRule,PredefinedAction,PredefinedActionSuggestion,QuickAccessLink,Invite,RoleTenantOverride,UsageEvent}Mapping.cs` totals 10.
2. `NotificationMapping.cs:34` reads `DeleteBehavior.Cascade`.
3. Snapshot no longer contains `IX_ai_rules_owner_id` / `IX_predefined_actions_owner_id` (search the `HasIndex("OwnerId")` single-column entries under those two entities).
4. Four new tests pass; `just test` green.
5. Rehearsal: ten pre-checks print 0; `\d predefined_actions` shows `FK_predefined_actions_projects_project_id … ON DELETE CASCADE`.
6. On the rehearsal database, `DELETE FROM projects WHERE id = <a project with a suggestion notification>` succeeds inside a rolled-back transaction (`BEGIN; … ; ROLLBACK;`).

## 8. Rollback

`Down()` drops the 10 new FKs and their indexes, restores `notifications.project_id` to `Restrict`,
re-adds the two single-column indexes. No data change either way. Dump `pre-db06` (R5).

## 9. Release steps

`git pull` → `stop api` → `backup-db.sh pre-db06` → ten pre-checks on prod (all 0) → `up -d --build api` → log grep → smoke: create + delete a predefined action on a project; trigger nothing else. Watch `DemoCleanupService` at the next hour tick: previously failing demo deletions should now log `hard-deleted`.

## 10. Out of scope

uuid user references (R14, Q5), `owner_id` FKs (DB-03), `comments.project_id` behaviour (stays
`Restrict` — a comment must never vanish by cascade), `page_context_snapshots`, `api_keys`,
`subscriptions.plan_id`, application code (no service changes needed), `clients/`, dashboard.
