# Diff Review — `fix/predefined-nullable-owner` (nullable `PredefinedAction.OwnerId`)

Reviewer: GLM-5.2 (adversarial diff review, not implementation).
Scope: `git diff --cached` on branch `fix/predefined-nullable-owner`.
Goal of the change: make `PredefinedAction.OwnerId` nullable so predefined actions resolve on
null-owner (global) projects (marketing landing / pre-ownership projects); previously the code
guarded on `OwnerId is Guid owner` and silently dropped actions for null-owner projects.

## Verdict

**Ship-blocking issues: none.** The core mechanism is correct and the headline tenant-isolation
paths (widget read + comment-create resolve) are sound. There is **one genuine isolation
regression** (MEDIUM) introduced by widening the query filter, plus a few latent/coverage items.
Recommend addressing the MEDIUM before merge; the rest can be follow-ups.

---

## Findings

### [MEDIUM] Admin CRUD-by-id now reaches null-owner rows → cross-tenant R/W on global actions
`Infrastructure/AppDbContext.cs:35` widens the `PredefinedAction` query filter from strict-own to
own-plus-global:

```csharp
b.Entity<PredefinedAction>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId || e.OwnerId == null);
```

This is required for the feature, but it interacts badly with the **id-based** admin CRUD endpoints.
`Application/Services/Implementation/PredefinedActionService.cs:71` (`UpdateAsync`) and `:96`
(`DeleteAsync`) load by id through the *filtered* `Query()`:

```csharp
var entity = await _unitOfWork.Repository<PredefinedAction>()
    .Query().Where(a => a.Id == id && a.DeletedAt == null).FirstOrDefaultAsync();
```

Because the filter now passes `OwnerId == null` for **every** tenant, any tenant admin hitting
`PATCH/DELETE /api/admin/predefined-actions/{id}` (`API/Controllers/Admin/PredefinedActionsController.cs:39,48`)
will successfully load a null-owner action whose integer id they guess. Consequences:

1. **Confidentiality**: `UpdateAsync` returns `MapToResponse(entity)` (`PredefinedActionService.cs:91`)
   which **includes `Prompt`** — the apply-time LLM prompt that is "NEVER exposed to the browser."
   Tenant B's admin can read the global landing's action prompts by enumerating sequential ids.
2. **Integrity**: the same admin can mutate (`UpdateAsync`) or soft-delete (`DeleteAsync`) the
   landing's actions. `ReconcileActionsAsync` (`ProjectService.cs:144`) cannot be abused this way
   because the project load goes through the filtered `GetByIdAsync`
   (`Infrastructure/Repository/Repository.cs:19` — confirmed it applies query filters, *not*
   `FindAsync`), so a tenant admin cannot load a null-owner project. The CRUD-by-id surface has no
   such guard.

This is a **regression**: under the old strict filter, `OwnerId == null` never matched a tenant, so
the id-based endpoints 404'd on any non-owned row. Note `ListTenantAsync`
(`PredefinedActionService.cs:27`) is *not* affected because it filters `ProjectId == null`, and no
null-owner tenant-wide rows are ever created (see next finding) — so the list itself does not leak.
The exposure is purely the id-based PATCH/DELETE.

**Recommended fix (reviewer suggestion, not applied):** scope the id-based admin CRUD explicitly,
e.g. add `&& a.ProjectId == null && a.OwnerId == currentUser.TenantId` to the load query (these
endpoints are documented as tenant-wide-only at `PredefinedActionsController.cs:11-13`), or
`IgnoreQueryFilters()` + an explicit owner match mirroring `ResolveInScopeAsync`. At minimum,
confirm null-owner actions are never reachable from these endpoints.

---

### [MEDIUM→ latent] "No null-owner tenant-wide rows" is an undocumented, load-bearing invariant
The own-plus-global filter is only safe for `ListTenantAsync` and `GetEffectiveForProjectAsync`
because **no row with `(OwnerId == null, ProjectId == null)` is ever created.** That property holds
solely because `CreateTenantAsync` (`PredefinedActionService.cs:44-46`) stamps a non-null owner:

```csharp
var ownerId = TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id;
if (ownerId is not Guid owner)
    return Result<...>.Forbidden(...);
```

`TenantStamp.OwnerFor` returns `null` only for super-admin (`Application/Common/TenantStamp.cs:11`),
and the `?? _currentUser.Id` then falls back to the super-admin's user id. So tenant-wide actions
are always non-null-owner. **This guard is now the single thing preventing a null-owner tenant-wide
action from becoming globally visible/editable by every tenant** (via both the list and the
widened filter). That coupling is non-obvious and the guard's comment (`:42-43`) does not mention
isolation. If a future change removes the guard or a seed/migration inserts a null-owner
tenant-wide row, every tenant silently gains read+write access to it.

**Recommended:** (a) add an inline comment at `PredefinedActionService.cs:44-46` stating this guard
is isolation-load-bearing; (b) add a test asserting `ListTenantAsync` never returns null-owner rows
and that a (force-seeded) null-owner tenant-wide row is *not* editable by another tenant's admin.

---

### [LOW] Isolation test coverage gaps
- `Tests/PredefinedActionCommentTests.cs` adds a positive regression (`NullOwnerProject_ActionResolvesOnCommentCreate`,
  `:202`) for the comment-create path — good. But there is **no negative test** that:
  - a tenant-B stakeholder is *denied* (`NotFound`) by `EnsureAsync` on a null-owner project, and
  - `ResolveInScopeAsync` cannot resolve a null-owner action scoped to a *different* null-owner
    project (the `ProjectId` guard at `PredefinedActionService.cs:156` is the protection and is
    untested).
- The widget-read path `GetEffectiveForProjectAsync` (`PredefinedActionService.cs:113`) — the
  primary read surface — has **no null-owner test at all**. It is safe by analysis (the EF filter
  reduces to `OwnerId IS NULL` for a null-tenant stakeholder, and `EnsureAsync`'s
  `p.OwnerId == ownerId` blocks cross-tenant project resolution at `ProjectService.cs:214`), but it
  should be pinned by a test.

### [LOW] Stale test comment
`Tests/TenantQueryFilterTests.cs:205` still reads:
> `// PredefinedAction — strict-own filter (super OR own); OwnerId is ALWAYS set`

The filter is now own-plus-global and `OwnerId` is nullable. `PredefinedAction_TenantA_CannotSeeTenantB`
(`:209`) still **passes** (it seeds no null-owner rows, so tenantA still sees exactly its 2 rows),
but the comment is now misleading. Add a sibling test asserting tenantA *also* sees null-owner
(global) rows under the new filter, to document the intentional behavior change.

### [LOW] Migration `Down` corrupts null rows on rollback
`Infrastructure/Migrations/20260701171558_PredefinedActionNullableOwner.cs:24-31` rolls back with
`defaultValue: new Guid("00000000-0000-0000-0000-000000000000")`. If any null-owner rows exist when
`Down` runs, they are all rewritten to `Guid.Empty`, collapsing every global action onto one fake
owner (semantically corrupt, though not a constraint violation — the `(OwnerId, ProjectId)` index is
non-unique, `Infrastructure/Mappings/PredefinedActionMapping.cs:34`). The `Up` (`:16-22`) is safe:
it is a metadata-only `DROP NOT NULL` on a `uuid` column, instant, and all existing rows are
non-null under the old invariant. Consider making `Down` either fail when null rows exist or omit the
default (let it error loudly instead of silently corrupting).

---

## Verified-safe (explicit checks)

- **EF null semantics in `ResolveInScopeAsync`** — `PredefinedActionService.cs:159`:
  `ownerId is Guid oid ? q.Where(a => a.OwnerId == oid) : q.Where(a => a.OwnerId == null)` is the
  correct pattern. The null branch compiles to `owner_id IS NULL` (literal null on a nullable
  property), and the non-null branch to `owner_id = @oid` (`Guid? == Guid` lifts cleanly). This is
  preferable to the old single `a.OwnerId == ownerId` parameter comparison. ✓
- **Query-filter null semantics** — `AppDbContext.cs:35` `|| e.OwnerId == null` is correct for both
  cases: a normal tenant (`TenantId != null`) gets `owner_id = @t OR owner_id IS NULL`; a null-owner
  stakeholder (`TenantId == null`) reduces (under EF C# null semantics) to `owner_id IS NULL`, so it
  sees only null-owner rows and **not** other tenants' rows. ✓
- **Widget read tenant isolation** — `GetEffectiveForProjectAsync` is isolated: project resolution
  via `EnsureAsync` (`ProjectService.cs:214`, `p.OwnerId == OwnerFor(caller)`) blocks a tenant from
  resolving a null-owner project (returns `NotFound`), and the action query's
  `(a.ProjectId == null || a.ProjectId == projectId)` (`PredefinedActionService.cs:132`) prevents a
  null-owner action scoped to project P1 from surfacing on project P2. No cross-tenant/cross-project
  leak. ✓
- **Comment-create tenant isolation** — `ResolveInScopeAsync` uses `IgnoreQueryFilters()` + an
  explicit owner match *against the resolved project's owner* (`CommentService.cs:46-49,97`), so it
  cannot resolve an action belonging to a different owner. Combined with `EnsureAsync` blocking
  cross-tenant project access, this path is isolated even for null-owner projects. ✓
- **No null-owner tenant-wide rows are created** — `CreateTenantAsync` always stamps non-null owner
  (super-admin falls back to `_currentUser.Id`); `ProjectService.CreateAsync`/`ReconcileActionsAsync`
  stamp `project.OwnerId`, which is non-null via the dashboard create path
  (`TenantStamp.OwnerFor ?? Id`, `ProjectService.cs:42`). Null-owner rows only arise for legacy/global
  projects and are always project-scoped (`ProjectId` set). ✓
- **No remaining non-null `OwnerId` deref on `PredefinedAction`** — `MapToResponse`
  (`PredefinedActionService.cs:177`) does not touch `OwnerId`; no `.OwnerId.Value` / `is Guid` remains
  against `PredefinedAction` outside the intentional `ResolveInScopeAsync` branch. ✓
- **Landing (null-owner project) read/upload/import paths** — already null-safe independent of this
  diff: `UploadsController.cs:86-88` (`HasValue ? ... : "global"`), `CommentService.cs:53` and
  `ExportImportService.cs:451` (`is Guid owner` demo-cap guards skip null-owner cleanly). ✓
- **`GetByIdAsync` applies query filters** — `Infrastructure/Repository/Repository.cs:19` uses
  filtered `FirstOrDefaultAsync` (not `FindAsync`), so `ProjectService.UpdateAsync`'s reconcile path
  cannot be driven against a null-owner project by a non-super-admin. ✓

---

## Suggested pre-merge actions
1. Fix or explicitly justify the MEDIUM (admin CRUD-by-id reaching null-owner rows).
2. Add a comment at `PredefinedActionService.cs:44` flagging the `CreateTenantAsync` guard as
   isolation-load-bearing.
3. Add negative isolation tests (tenant denied on null-owner project; cross-project
   null-owner resolve denied) and a widget-read null-owner test.
4. Refresh the stale comment at `Tests/TenantQueryFilterTests.cs:205`.
