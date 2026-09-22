# DB-02 — Migration safety guard test + PII scrub in migration comments

Review findings: P0-4, M-3, M-5. Rules: R2, R3, R7, R10, R13. **Additive — test code and two
comment edits only. No migration.**

## 1. Goal

A migration that drops, renames, narrows or runs raw SQL can no longer reach `main` — and therefore
production boot (`API/Program.cs:169` auto-applies) — without an explicit, dated approval marker in
the file, and no historical migration can be renamed or deleted without a test failing. Also removes
two personal e-mail addresses from migration comments. User-visible reason: the last four
data-moving migrations shipped unattended; one of them (`20260701163243`) discarded two columns.

## 2. Prerequisites (verified facts)

- Migrations live in `Infrastructure/Migrations/<yyyyMMddHHmmss>_<Name>.cs` with a sibling `.Designer.cs`; the snapshot is `Infrastructure/Migrations/AppDbContextModelSnapshot.cs`. 58 migrations exist today (list in §3).
- Each migration file has `protected override void Up(MigrationBuilder migrationBuilder)` then `protected override void Down(…)`.
- Test project: xunit 2.5.3 (`Tests/Pointer.Tests.csproj:19`), helper `Tests/RepoRoot.cs:3-16` (`RepoRoot.Find()` walks up to `Pointer.sln`). Precedent for a repo-scanning guard test: `Tests/OnDiskContractTests.cs`.
- PII comments: `Infrastructure/Migrations/20260827124245_ReassignPointerLandingOwnership.cs:16-17` and `…/20260827125323_ReassignRemainingSuperAdminOwnedResources.cs:20-21` — the `const string` GUID values are load-bearing (used in the `UPDATE … WHERE`), the trailing `//` comments are not.
- `just test` = `dotnet test` (`justfile:8`).

## 3. Design

`Tests/MigrationSafetyTests.cs`, two facts:

**A. `HistoricalMigrationIdsAreFrozen`** — every id in the baseline set below still exists as
`Infrastructure/Migrations/<id>.cs` (R10). Baseline (58, as of 2026-09-22):

```
20260623133436_InitialCreate, 20260624130359_AddElementScreenshotUrl, 20260624132711_AddUserPreferences,
20260624144403_AddUserApprovalStatus, 20260624151316_AddCommentIsPrivate, 20260624153650_AddCommentEditTrace,
20260629072310_AddStatusPresentations, 20260629130828_AddTenancy, 20260629131255_AddTenantRoleNameIndex,
20260629155415_AddDemoColumns, 20260701121004_AddDemoTenantConfig, 20260701141656_AddUserRecipientEmail,
20260701142439_AddPredefinedActions, 20260701163243_MultiSelectPickedActions, 20260701171558_PredefinedActionNullableOwner,
20260701190410_AddInvites, 20260701222115_AddCommentBodyMaxLength, 20260701225454_AddPredefinedActionSuggestions,
20260702014430_AddPlansAndSubscriptions, 20260705041708_AddCommentPerfIndexes, 20260705173225_AddUserSecurityStamp,
20260825100006_AddPageContextCapture, 20260825122734_AddUserCommentShortcut, 20260827124245_ReassignPointerLandingOwnership,
20260827125323_ReassignRemainingSuperAdminOwnedResources, 20260827130246_EnforceOwnerIdNotNull, 20260827164229_MakeInviteOwnerIdNullable,
20260829150137_AddQuickAccessInviteFields, 20260830131134_AddRoleTenantOverrides, 20260830174750_AddAppEnvironmentsAndProjectAppUrls,
20260831153128_MakeProjectKeyIndexFilterSoftDeleted, 20260831174453_AddProjectTechStack, 20260831183037_AddUserApiKey,
20260903191419_ProjectPerEnvironmentActivation, 20260903195932_ProjectAppUrlIsActive, 20260906094008_AddProjectEnvironmentSelectorRoleIds,
20260908132329_AddAiRulesTable, 20260909093139_AddCommentCommitUrlAndProjectCommitStyle, 20260911170058_AddApiKeysTable,
20260911170808_AddAppEnvironmentEnabledFlags, 20260911170828_MigrateDefaultProjectAppUrlsToLocal, 20260911170928_AddUsageEvents,
20260911173001_AddProjectEnforceAllowedOrigins, 20260911223426_AddTenantInviteFields, 20260912112554_AddPayloadFlags,
20260912120127_AddQuickAccessLinks, 20260912120230_AddNotificationsAndCommentVerifiedAt, 20260912125359_AddProjectCaptureTextContent,
20260912193031_AddDeployAwareness, 20260915163653_FixPayloadFlagsJsonDefault, 20260916121926_AppUrlUniqueIndexIgnoresSoftDeleted,
20260916165610_AddDeviceLogins, 20260917220722_AddReplyIsAi, 20260917230734_AddCommentLanguage,
20260917234138_WidenUsageEventSource, 20260918004732_AddReplyAiAttribution, 20260918205719_SuggestionChangesRequestedAndNotifications,
20260921215630_AddCommentFieldsAndWorkspaceSettings
```

**B. `NewMigrationsWithRiskyOperationsCarryApproval`** — for every migration file **not** in the
baseline (exclude `*.Designer.cs` and the snapshot): take the text between `void Up(` and
`void Down(`; if it matches the regex
`\.(DropColumn|DropTable|RenameColumn|RenameTable|DropIndex|DropForeignKey|DropPrimaryKey|AlterColumn|Sql)\(`
then the **whole file** must match `//\s*DB-RULES:\s*(R2 contract|R3 backfill|index change|R4 constraint)\s+approved\s+\d{4}-\d{2}-\d{2}\s+by\s+\S+`.
Failure message lists the file and the first offending operation. (Historical files are exempt on
purpose — they are frozen, R10.)

Marker format every later execution doc uses, on the line above `Up(`:
`// DB-RULES: R2 contract approved 2026-09-25 by <owner>`.

## 4. Safety classification

Additive (tests only). Rules exercised: R2, R3, R7, R10, R13.

## 5. File-level tasks

1. **`Tests/MigrationSafetyTests.cs`** (new) — class `MigrationSafetyTests`; `private static readonly string MigrationsDir = Path.Combine(RepoRoot.Find(), "Infrastructure", "Migrations");`; `private static readonly HashSet<string> Baseline = new(StringComparer.Ordinal) { …58 ids… };` two `[Fact]`s as in §3. Read files with `File.ReadAllText`; enumerate with `Directory.EnumerateFiles(MigrationsDir, "*.cs")` filtering out names ending in `.Designer.cs` and `AppDbContextModelSnapshot.cs`. Use `Regex` with `RegexOptions.Singleline` for the `Up` slice.
2. **`Infrastructure/Migrations/20260827124245_ReassignPointerLandingOwnership.cs:16-17`** — change the two trailing comments to `// super-admin (operator) account` and `// dedicated tenant (Workspace Admin)`; do not touch the string values.
3. **`Infrastructure/Migrations/20260827125323_ReassignRemainingSuperAdminOwnedResources.cs:20-21`** — same two comment edits.
4. Run `just test`; both new facts pass; nothing else changes.

## 6. Tests

This doc *is* tests. Sanity check for B: temporarily create a fake `Infrastructure/Migrations/29990101000000_Probe.cs` containing `void Up(…){ migrationBuilder.DropColumn("x","y"); } void Down(…){}` → test B fails; add the marker → passes; delete the probe (never commit it).

## 7. Acceptance criteria

1. `dotnet test --filter MigrationSafetyTests` → 2 passed.
2. `grep -rn "gmail.com" Infrastructure/Migrations/` → no output.
3. `git diff --stat` touches exactly: the new test file and the two migration `.cs` files (comment lines only; `.Designer.cs` untouched).
4. `dotnet ef migrations list -p Infrastructure -s API --no-connect` still lists 58 migrations (renaming nothing).

## 8. Rollback

`git revert` of the commit. Nothing runs against a database.

## 9. Release steps

Merge to `main`. Deploy is unaffected (no migration); the next `deploy-api.sh` rebuilds the image with the edited comments — no runtime difference.

## 10. Out of scope

Any migration body, `.Designer.cs`, the snapshot, CI YAML (Q7), `DB-RULES.md`.
