# Review of DB-REVIEW-2026-09-22 and Execution Docs

This report reviews the schema and execution plans proposed in `DB-REVIEW-2026-09-22.md` for the Pointer API pre-launch database schema. 

## 1. Missed Schema Problems

### 1.1 Tenant Isolation Leak in `PredefinedActionSuggestion`
- **Severity**: Blocker
- **Evidence**: `Infrastructure/AppDbContext.cs:108-111`
- **Finding**: The review missed a critical tenant isolation leak. While the comment in `AppDbContext.cs` states `STRICT-OWN (BINDING #5): suggestions are visible only to the owning tenant or super-admin — NEVER own-plus-global`, the actual lambda contains `|| (currentUser.TenantId == null && !strict && e.OwnerId == null)`. This incorrectly allows null-tenant callers to see null-owner suggestions if `strict` is off, contradicting the strict-own invariant.
- **Suggested Change to Doc**: Add a new finding to the DB-REVIEW. Include a fix in an execution doc to update the query filter for `PredefinedActionSuggestion` in `AppDbContext.cs` to strictly enforce the stated invariant by removing the fallback clause.

### 1.2 Cascade Delete Wiping Data Across Tenants
- **Severity**: Blocker
- **Evidence**: `Infrastructure/Mappings/ProjectAppUrlMapping.cs:25`
- **Finding**: The review missed that the foreign key from `ProjectAppUrl` to `AppEnvironment` is configured with `OnDelete(DeleteBehavior.Cascade)`. Since `AppEnvironment` is own-plus-global, a super-admin hard-deleting a global catalog environment (e.g., "prod") will inadvertently trigger a cascade delete that wipes out every tenant's `ProjectAppUrl` records referencing it.
- **Suggested Change to Doc**: Add this finding to DB-REVIEW. Update DB-06 to change `OnDelete(DeleteBehavior.Cascade)` to `OnDelete(DeleteBehavior.Restrict)` for the `AppEnvironmentId` foreign key on `ProjectAppUrlMapping`.

### 1.3 Unique Indexes Broken by Soft Delete
- **Severity**: Major
- **Evidence**: `Infrastructure/Mappings/PlanMapping.cs:25-27` and `Infrastructure/Mappings/AppSettingMapping.cs:25`
- **Finding**: DB-05 explicitly excludes `plans` (Name, Slug) and `app_settings` (Key) unique indexes, claiming they are "not soft-deleted in practice." However, both `Plan` and `AppSetting` inherit from `BaseEntity` and thus fully support soft deletion. A soft-deleted plan would permanently burn its name and slug, triggering unique constraint violations on re-creation.
- **Suggested Change to Doc**: Remove the out-of-scope exclusions in DB-05. Update DB-05 to recreate these unique indexes as partial indexes with `.HasFilter("deleted_at IS NULL")`.

### 1.4 Migration History Hazard with Auto-Migrate-on-Boot
- **Severity**: Blocker
- **Evidence**: `API/Program.cs:165-169` and `execution/DB-02-migration-safety-guard.md`
- **Finding**: The review falsely claims DB-02 implements a "migration safety guard". DB-02 only adds a CI test that verifies the presence of a comment string (`// DB-RULES...`). `API/Program.cs` still unconditionally executes `db.Database.MigrateAsync()` on boot when `DBMigrationEnabled` is true. If a destructive migration (like DB-04 or DB-07) is merged with the required comment, it will still automatically execute unattended on the next restart, bypassing the "explicit step" procedure documented in DB-RULES R7.
- **Suggested Change to Doc**: Update DB-02 to modify `API/Program.cs` to either completely disable `MigrateAsync()` in production (requiring a separate rollout container) or to defensively check pending migrations for a safe attribute before applying them automatically.

## 2. Inaccurate Claims in the Review

### 2.1 Soft Delete Predicate Counts
- **Severity**: Minor
- **Evidence**: The entire `Application/`, `API/`, and `Infrastructure/` directories.
- **Finding**: DB-REVIEW finding S-4 claims there are exactly 162 `IgnoreQueryFilters()` calls and 229 hand-written `DeletedAt == null` predicates. The actual codebase contains 178 `IgnoreQueryFilters()` and 231 `DeletedAt == null` checks.
- **Suggested Change to Doc**: Update the DB-REVIEW S-4 text to reflect the accurate numbers.

## 3. Execution Docs Flaws

### 3.1 DB-03 Implementation Logic Flaw
- **Severity**: Major
- **Evidence**: `execution/DB-03-workspaces-table.md:118-121`
- **Finding**: DB-03 instructs the developer to write a `loop over a new internal static readonly Type[] HardDeleteOrder` to call a generic helper `Task DeleteOwnedAsync<T>(Guid ownerId) where T : BaseEntity`. A mid/low-tier model or a developer taking this literally will write a `foreach` loop over the `Type` array and attempt to call the generic method directly without reflection, which will not compile. The parenthetical note is contradictory.
- **Suggested Change to Doc**: Update DB-03 §3.4 to explicitly require 22 explicit method calls to `await DeleteOwnedAsync<T>(...)` sequentially instead of a runtime loop, reserving the `Type[]` array strictly for the reflection-based unit test.

### 3.2 DB-RULES Verification
- **Finding**: DB-RULES is correctly specified for PostgreSQL 15 + EF Core 8.
- **Details**: Rule R4 mandates `migrationBuilder.Sql("CREATE INDEX CONCURRENTLY …", suppressTransaction: true)`, which is the correct pattern for EF Core. Rule R9 and DB-05 use `.AreNullsDistinct(false)`, which perfectly aligns with PG15+'s `NULLS NOT DISTINCT` behavior.

## 4. Verdict

- **DB-01**: **Safe**. Can be handed to an implementer as-is.
- **DB-02**: **Needs Edits**. The doc requires an update to actually prevent `MigrateAsync` from running destructive scripts on boot in `API/Program.cs`.
- **DB-03**: **Needs Edits**. The `HardDeleteAsync` implementation loop needs to be clarified to prevent uncompilable code.
- **DB-04**: **Safe**. Can be handed to an implementer as-is.
- **DB-05**: **Needs Edits**. Needs to incorporate the soft-delete fixes for `Plan` and `AppSetting` unique indexes.
- **DB-06**: **Needs Edits**. Must incorporate the restrict cascade fix for `ProjectAppUrl.AppEnvironmentId`.
- **DB-07**: **Safe**. Can be handed to an implementer as-is.
- **DB-08**: **Safe**. Can be handed to an implementer as-is.

**Execution Order Recommendation**: The proposed order (`DB-01 → DB-02 → DB-04 → DB-05 → DB-03 → DB-06 → DB-07 → DB-08`) is appropriate for a pre-launch product. Warm-up migrations (DB-04/05) accurately exercise the rehearsal process before major structural changes (DB-03).
