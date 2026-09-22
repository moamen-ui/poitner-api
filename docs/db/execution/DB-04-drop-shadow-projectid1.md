# DB-04 — Fix the `ProjectAppUrl` mapping and drop the shadow column `project_app_urls."ProjectId1"`

Review finding: S-3, M-2. Rules: R2 (contract), R5, R7, R11, R13. **Class: Contract** — drops a
column that is always NULL. One small migration; the ideal first exercise of the rehearsal loop.
**Status: shipped** `7a015cb`; **deployed to production 2026-09-22 08:18 UTC** (R7 explicit step,
dump `pre-db04`, pre-check 0, 15 rows unchanged, smoke 200). Retroactively carries
`[ContractMigration("DB-04")]` since DB-09 (`ff25a8d`).

## 1. Goal

Remove a PascalCase shadow column, its index and its FK that EF created by accident because the
`ProjectAppUrl → Project` relationship is configured without naming the inverse collection. Fixing
the mapping makes `Project.ProjectAppUrls` load real rows; dropping the column ends the snapshot
drift. User-visible reason: none today (latent bug) — it is a correctness fix before anyone
`Include`s the navigation.

## 2. Prerequisites (verified facts)

- `Infrastructure/Mappings/ProjectAppUrlMapping.cs:22`: `b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);`
- `Domain/Entity/Project.cs:61`: `public ICollection<ProjectAppUrl> ProjectAppUrls { get; set; } = new List<ProjectAppUrl>();` (the unpaired inverse).
- Snapshot `Infrastructure/Migrations/AppDbContextModelSnapshot.cs:1268` (`b.Property<int?>("ProjectId1")`), `:1291` (`b.HasIndex("ProjectId1")`), `:2400-2402` (`b.HasOne("Pointer.Domain.Entity.Project", null).WithMany("ProjectAppUrls").HasForeignKey("ProjectId1")`).
- Created by `Infrastructure/Migrations/20260911223426_AddTenantInviteFields.cs:14` (`AddColumn<int>(name: "ProjectId1", table: "project_app_urls", nullable: true)`), `:33-35` (`CreateIndex IX_project_app_urls_ProjectId1`), `:38-40` (`AddForeignKey FK_project_app_urls_projects_ProjectId1 … onDelete: ReferentialAction.Restrict`). Its `Down` drops them (`:49-53`).
- Nothing writes the shadow property: the only way it could be non-NULL is `project.ProjectAppUrls.Add(url)` in code, and `grep -rn "\.ProjectAppUrls" Application API` shows only `AppEnvironment.ProjectAppUrls` usages (`AppEnvironmentService.cs:39,162`) and the seeder's `db.ProjectAppUrls` DbSet (`AdminSeeder.cs:103,108`).
- `AppEnvironmentMapping`/`AppEnvironment.ProjectAppUrls` pair is correctly configured (`ProjectAppUrlMapping.cs:25` names the inverse) — leave it alone.
- DB-02's guard requires a marker because `Up()` will contain `DropColumn`.

## 3. Design

Mapping: `ProjectAppUrlMapping.cs:22` becomes
`b.HasOne(x => x.Project).WithMany(p => p.ProjectAppUrls).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);`

Expected generated migration `DropShadowProjectAppUrlProjectId1`, `Up()` in this order:
1. `DropForeignKey(name: "FK_project_app_urls_projects_ProjectId1", table: "project_app_urls")`
2. `DropIndex(name: "IX_project_app_urls_ProjectId1", table: "project_app_urls")`
3. `DropColumn(name: "ProjectId1", table: "project_app_urls")`

`Down()` re-adds column, index, FK (mirror of `20260911223426:14,33-40`). Snapshot diff: only the
three `ProjectId1` entries disappear and the `HasOne("…Project","Project").WithMany()` becomes
`.WithMany("ProjectAppUrls")`; anything else → stop.

Existing rows: `project_app_urls` rows lose a column that is NULL in every row (pre-check below);
no other change.

Pre-check (rehearsal and prod, must print `0`):
`SELECT count(*) FROM project_app_urls WHERE "ProjectId1" IS NOT NULL;`

## 4. Safety classification

**Contract** (R2). Destructive in form, not in substance (column provably empty). Requires: marker
`// DB-RULES: R2 contract approved <date> by <owner>` above `Up(`; dump `pre-db04`; R7 explicit
step (API stopped) — cheap, and it rehearses the procedure.

## 5. File-level tasks

1. `Infrastructure/Mappings/ProjectAppUrlMapping.cs:22` — change `.WithMany()` to `.WithMany(p => p.ProjectAppUrls)`. Add a one-line comment above: `// Inverse named explicitly: leaving WithMany() empty made EF add a second, shadow FK "ProjectId1" (DB-04).`
2. `just migrate name="DropShadowProjectAppUrlProjectId1"` → open the file; verify `Up()` is exactly the three operations of §3 in that order; add the marker line above `Up(`. If `Up()` contains anything else, stop and report.
3. `Tests/ProjectAppUrlNavigationTests.cs` (§6). 4. `just fmt`, `just test`. 5. Rehearsal (R11) with the pre-check query before and `\d project_app_urls` after (column gone).

## 6. Tests

New `Tests/ProjectAppUrlNavigationTests.cs` (InMemory fixture from `Tests/CommentFieldsTests.cs:61-62`, super-admin `FakeCurrentUser`):

1. `Project_ProjectAppUrls_LoadsThroughProjectId` — add an `AppEnvironment`, a `Project`, a `ProjectAppUrl { ProjectId, AppEnvironmentId, Url, OwnerId }`; new context; `db.Projects.Include(p => p.ProjectAppUrls).Single().ProjectAppUrls.Count == 1`. (Fails before the mapping fix: the include followed the NULL shadow key.)
2. `ProjectAppUrl_Model_HasNoShadowProjectId1` — `db.Model.FindEntityType(typeof(ProjectAppUrl))!.GetProperties().Select(p => p.Name)` does not contain `"ProjectId1"`, and `GetForeignKeys().Count() == 2` (project, app environment).

## 7. Acceptance criteria

1. `grep -c "ProjectId1" Infrastructure/Migrations/AppDbContextModelSnapshot.cs` → 0.
2. The new migration's `Up()` has exactly `DropForeignKey`, `DropIndex`, `DropColumn` and the marker; `Down()` restores all three.
3. Both new tests pass; `just test` green; `MigrationSafetyTests` passes.
4. Rehearsal: pre-check prints 0; after update, `\d project_app_urls` lists no `ProjectId1`, and `SELECT count(*) FROM project_app_urls` is unchanged.

## 8. Rollback

`Down()` recreates the nullable column, index and FK; no data to restore (column was NULL). Dump
label `pre-db04` taken anyway (R5).

## 9. Release steps

1. Merge. On the VM: `git pull`; `docker compose -f docker-compose.prod.yml stop api`; `bash scripts/backup-db.sh pre-db04`.
2. Pre-check on prod: `docker compose -f docker-compose.prod.yml exec -T db psql -U pointer -d pointer -c 'SELECT count(*) FROM project_app_urls WHERE "ProjectId1" IS NOT NULL;'` → must be `0`, else abort and report.
3. `up -d --build api`; log grep for `Applying migration` + no error; smoke `GET /api/branding`, dashboard project settings page (environment URLs render).

## 10. Out of scope

`AppEnvironment.ProjectAppUrls`, `ProjectAppUrl.OwnerId`/filters, any other table, the seeder,
`clients/`, the dashboard.
