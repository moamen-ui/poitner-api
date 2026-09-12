# R1-09 — Project URLs belong to enabled workspace environments (§51 / R1.9 · Release 1 · 3–4 days)

## Goal
A project no longer has a single nameless "app URL". Every URL it has is attached to a **named
environment from the workspace's own catalog** (`AppEnvironment`), and only to an environment the
workspace has **enabled**. The seeded `default` environment — which exists only because the old
single-field model needed somewhere to put its one URL — is retired in favour of a seeded **`local`**,
and every existing `default` row migrates to `local`. Creating a project still takes one URL in one
step: the create dialog shows the environment-URL table from the start, pre-populated with a single
row (`local` + the URL you typed), exactly as if you had clicked "add environment URL" once.

**This item is about where the app is deployed, not about how comments are tagged.** Comment tagging
keeps using the fixed `EnvironmentTag` enum (Local/Staging/Production) and the three
`Project.IsActiveLocal/Staging/Production` bools. Those are a *different concept* and are untouched
here — `Project.cs:9-15` already carries a warning comment about the two being confused, and this doc
must not blur them. The named catalog answers "which deployment is at this URL"; the fixed enum
answers "which environment was this comment left in".

## Out of scope
- The fixed `EnvironmentTag` enum, the three activation bools, the widget's environment switcher, and
  `EnvironmentSelectorRoleIds`. Untouched.
- Ephemeral per-PR preview environments (§35, held) — this doc makes them *possible* (a tenant can
  already create a named environment) but implements none of their lifecycle.
- Renaming/merging existing tenant-defined environments, or a bulk URL editor.
- Removing the `Project.AppUrl` **column** (Decision 4 keeps it this release; the drop is a follow-up
  release per 01-OVERVIEW's additive-only migration rule).

## Prerequisites
- **None inside R1** — but see the cross-doc obligation below, which is a *sequencing* constraint, not
  a dependency on unmerged work.
- **Cross-doc obligation (R1-02):** `npx pointer-feedback init` creates projects
  (`docs/roadmap/execution/R1-02-cli-init.md` §C step 3, `POST /api/admin/projects {key, name}`).
  `CreateProjectRequest` changes shape here (Decision 5), so **R1-09 must land before or together with
  R1-02**; if R1-02 lands first it must be revisited. R1-02 owns its own edits — this doc only records
  the obligation:
  1. `init` sends `appEnvironmentId` (or omits it and accepts the `local` default) alongside `appUrl`.
  2. R1-02 §C's create step and its `--json` summary (`project` object) gain the resolved environment
     name, so a caller can see which environment the URL landed on.
  3. R1-02's acceptance criteria re-verified against the new response shape.
- Facts verified in code (2026-09-11):
  - `AppEnvironment` (`Domain/Entity/AppEnvironment.cs`) = `Name`, `OwnerId` (null ⇒ global), nav to
    `ProjectAppUrls`. **No enabled/active flag today.** Unique index `(name, owner_id)`
    (`Infrastructure/Mappings/AppEnvironmentMapping.cs:22`). Query filter: **own-plus-global**
    (`Infrastructure/AppDbContext.cs:111`).
  - `ProjectAppUrl` (`Domain/Entity/ProjectAppUrl.cs`) = `ProjectId`, `AppEnvironmentId`, `Url`,
    `IsActive`, `OwnerId` (non-null). Unique `(project_id, app_environment_id)`
    (`ProjectAppUrlMapping.cs:31`). Query filter: **strict-own** (`AppDbContext.cs:114`).
    `IsActive` here means "this project's mapping for this environment is on" — **not** the same thing
    as the new flag (Decision 1).
  - Seeded globals: `{ "default", "prod", "staging", "testing" }` (`API/Seed/AdminSeeder.cs:30`),
    back-filled on **every** boot for any missing name (`:70-78`) — **there is no `local` today.**
  - `Project.AppUrl` (`Project.cs:23-24`) is the legacy single field, synced to the `default`
    environment by `ProjectService.SyncDefaultAppUrlAsync:557-590` on create (`:89-90`) and update
    (`:224-227`), and written back from `SetAppUrlAsync` when the environment is literally named
    `"default"` (`:324-330`).
  - `AppEnvironmentService` (`Application/Services/Implementation/AppEnvironmentService.cs`):
    `ListAsync`, `CreateAsync`, `UpdateAsync` (rename only), `DeleteAsync` (soft, **refuses when any
    `ProjectAppUrl` references it** — `:97-102`), `CanManage` = super admin, or the environment's own
    tenant. Routes `api/admin/environments` under `Policies.Admin`
    (`API/Controllers/Admin/AppEnvironmentsController.cs:10-11`).
  - `MaxEnvironments` exists in `PlanEntitlements.cs:28` and is registered **display-only**
    (`EntitlementCatalog.cs:73`, `enforced: false`, default 3) — nothing calls `CheckCountAsync` for it.

### Callers of `Project.AppUrl` (complete list, verified by grep)
| Caller | Line | Use |
|---|---|---|
| `ProjectService.CreateAsync` | `:79`, `:89-90` | writes the field, then syncs it to `default` |
| `ProjectService.UpdateAsync` | `:224-227` | same |
| `ProjectService.SetAppUrlAsync` | `:324-330` | writes the field back when the env is named `default` |
| `ProjectService.SyncDefaultAppUrlAsync` | `:557-590` | the sync itself |
| `ProjectService.MapToResponse` | `:941` | exposes it on `ProjectResponse` |
| `InviteService` (quick-access invite) | `:525-526` | **hard requirement** — refuses the invite with `MessageKeys.Invite.QuickAccessAppUrlRequired` when the project has no `AppUrl` |
| `InviteService` | `:595`, `:599` | the URL in the invite e-mail body and in `InviteResponse` |
| `ExtensionService.FindProjectForOriginAsync` | `:109-120` | legacy fallback when no `ProjectAppUrl` matches the origin |
| `AdminSeeder` | `:81-105` | boot backfill into the `default` environment |
| `ProjectMapping` | `:34` | column `app_url` |
| `Tests/ProjectAppUrlSyncTests.cs` | whole file | asserts the `default` sync behaviour |

### Callers of the `"default"` environment (complete list)
`ProjectService.cs:87` (comment), `:324-330` (write-back), `:552-566` (`SyncDefaultAppUrlAsync`
lookup) · `AdminSeeder.cs:27-30` (seed list), `:82-105` (backfill) · `AppDbContext.cs:109` (comment) ·
`Tests/ProjectAppUrlSyncTests.cs:15,38,56,116` · `Tests/WidgetActivationTests.cs:38` ·
`Tests/AppEnvironmentServiceTests.cs:14` (comment).

## Design

### Decisions

**Decision 1 — the flag is `AppEnvironment.IsEnabled` (bool, default `true`), and it *blocks* rather
than *hides*.** Named `IsEnabled`, deliberately **not** `IsActive`, because `ProjectAppUrl.IsActive`
already exists one table away and means something different (that single project↔environment mapping
is on). Two `IsActive`s in the same join would be a permanent source of confusion; the entity comment
must say so.
- Default **`true`** for every existing row, so the migration changes no behaviour on its own.
- **Blocks, does not hide:** a disabled environment is still returned by
  `GET /api/admin/environments` (with `isEnabled: false`) and its existing `ProjectAppUrl` rows are
  still listed by `GET /api/admin/projects/{id}/app-urls` (with the environment's `isEnabled` echoed
  through). What it blocks is *writing a new URL to it* (Decision 6) and *resolving through it*
  (Decision 7). Hiding would silently orphan URLs an admin can no longer see or delete, which is worse
  than showing them greyed out with a "this environment is disabled" hint.

**Decision 2 — `default` is retired by disabling and hiding it, never by deleting it.** It is a
**global** row (`OwnerId == null`) shared by every tenant, and tenants may already have URLs on it —
`AppEnvironmentService.DeleteAsync:97-102` would refuse to delete it for exactly that reason. Retirement:
1. The boot seeder stops listing `default` in `DefaultAppEnvironments` (so a **fresh** database never
   gets one) but keeps back-filling the rest.
2. An idempotent boot step sets `IsEnabled = false` on the **global** `default` row if it exists
   (mirroring the existing idempotent role upgrades at `AdminSeeder.cs:53-67`).
3. A new `AppEnvironment.IsRetired` (bool, default `false`) is set on that same row, so the dashboard
   can filter it out of the "add URL" picker while still rendering existing rows. A separate flag from
   `IsEnabled` because a tenant may legitimately disable-and-re-enable its own environment, whereas
   `default` never comes back.
   **Decision 2a:** a tenant's *own* environment literally named `default` (possible today — the unique
   index is `(name, owner_id)`) is **left alone**. It is that tenant's environment, not ours; only the
   global row is retired. The migration (Decision 3) targets `owner_id IS NULL` only.

**Decision 3 — `local` is seeded global, and the migration moves `default` rows onto it.** Added to
`DefaultAppEnvironments` so the existing every-boot backfill (`AdminSeeder.cs:70-78`) creates it on
already-running instances too — no separate migration needed for the row itself. The data move **is** a
migration, and must be additive and idempotent:
- `just migrate name="MigrateDefaultProjectAppUrlsToLocal"` — for every `project_app_urls` row whose
  `app_environment_id` is the **global** `default`, repoint it at the **global** `local`.
- **Conflict case:** a project that already has a `local` row *and* a `default` row would violate the
  unique `(project_id, app_environment_id)` index. Rule: **keep the existing `local` row, soft-delete
  the `default` one** (`deleted_at = now()`) — an explicitly-set `local` URL is more intentional than
  one that landed on `default` implicitly. The migration must do this in SQL, not blindly `UPDATE`.
- The migration runs at boot (`Program.cs:123-129`) **before** the seeder, so it must handle "the global
  `local` row does not exist yet": create it in the migration if absent (`INSERT … WHERE NOT EXISTS`),
  and let the seeder's backfill be the no-op it already is when the name is present.

**Decision 4 — `Project.AppUrl` is deprecated-but-kept this release, synced to `local`.** It cannot
vanish: `InviteService:525-526` *refuses to issue a quick-access invite* without it, and
`ExtensionService:109-120` uses it as a legacy origin fallback. Per 01-OVERVIEW's additive-only rule the
column drop is a later release. This release:
- `SyncDefaultAppUrlAsync` is renamed `SyncPrimaryAppUrlAsync` and targets **`local`** (same
  own-then-global preference it uses today for `default`).
- `SetAppUrlAsync`'s write-back fires when the environment is `local` instead of `default` (`:326`).
- `InviteService` keeps reading `Project.AppUrl` — **unchanged behaviour**, because after the migration
  every project that had a URL has a `local` URL, and `AppUrl` still mirrors it.
- `ProjectResponse.AppUrl` is kept and marked `[Obsolete]`-in-docs (an XML comment saying "mirrors the
  `local` environment URL; use `appUrls`"), and `ProjectResponse` gains **`AppUrls: List<ProjectAppUrlResponse>`**
  so the dashboard and CLI can stop reading the scalar.
- **Removal plan** (one line in Rollout): drop `Project.AppUrl` in R2 once `InviteService` reads the
  `local` `ProjectAppUrl` row and `ExtensionService`'s legacy fallback is deleted. Not this doc.

**Decision 5 — `CreateProjectRequest` keeps one URL field and gains an optional environment id.**
```csharp
public class CreateProjectRequest
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Where this project's widget is embedded. Optional except for a quick-access client
    /// invite. Written to <see cref="AppEnvironmentId"/> (default: the "local" environment).</summary>
    public string? AppUrl { get; set; }

    /// <summary>Which enabled workspace environment <see cref="AppUrl"/> belongs to. Omitted ⇒ the
    /// tenant's own "local" environment if it has one, else the global "local".</summary>
    public int? AppEnvironmentId { get; set; }

    public List<PredefinedActionInput> PredefinedActions { get; set; } = new();
}
```
- `AppUrl` null/empty ⇒ `AppEnvironmentId` is ignored; no `ProjectAppUrl` row is written (unchanged
  from today).
- `AppEnvironmentId` given but **not visible** to the tenant ⇒ `404 AppEnvironment.NotFound` (the query
  filter already makes a foreign id invisible — same behaviour and message as `SetAppUrlAsync:295-297`).
- `AppEnvironmentId` given but **disabled or retired** ⇒ `400` with a new
  `MessageKeys.AppEnvironment.NotEnabled` (Decision 6).
- `AppEnvironmentId` omitted and **no `local` environment resolves** (a super admin deleted the global
  one) ⇒ the project is still created and `AppUrl` still stored on the entity, but no `ProjectAppUrl`
  row is written — exactly today's `SyncDefaultAppUrlAsync:565-566` behaviour ("no default environment
  exists — nothing to sync"). Do not fail the create; a missing catalog row is an operator problem, not
  the caller's.
- **Browser extension:** it creates with only a URL and no environment id (`ProjectService.cs:86-88`
  documents this), so it takes the `local` default unchanged. No extension code changes.

**Decision 6 — writing a URL to a disabled environment returns `400`, not 403/404.** `SetAppUrlAsync`
already uses `404` for "environment not visible" (`:295-297`) and `403` for "you may not edit this
project" (`:286-287`). "The environment exists, you can see it, but it is switched off" is neither — it
is a validation failure of the request, which this file expresses as `Result<T>.Failure` → `400` (see
`:290-291`, `"URL is required."`). New key `MessageKeys.AppEnvironment.NotEnabled`. Same rule in
`CreateAsync`.
- **Toggling an environment off with URLs already on it is allowed** and does not delete or deactivate
  them (Decision 1: blocks, does not hide). The rows stay, stop resolving (Decision 7), and start
  resolving again if it is re-enabled. The dashboard warns with the count of affected projects.

**Decision 7 — resolution ignores disabled environments, and this is a live behaviour change.**
Two readers must now join to `AppEnvironment` and require `IsEnabled`:
- `ExtensionService.FindProjectForOriginAsync:96-103` — a URL on a disabled environment no longer
  matches an origin. **Risk:** an admin who disables an environment silently stops the extension from
  resolving those sites. Mitigated by the dashboard warning in Decision 6 and by the fact that
  `IsEnabled` defaults `true`, so nothing changes until someone deliberately switches one off.
- `ProjectService.CheckWidgetActiveAsync:821-834` — this is the **anonymous widget render gate**, so
  the risk is higher. Today: an origin matching a row with `IsActive == false` blocks the widget; an
  origin matching nothing is allowed.

  **REVERSED 2026-09-12 by the product owner.** The rule is now: **a row whose environment is disabled
  BLOCKS that origin**, exactly as an explicit per-mapping `IsActive == false` does. Disabling an
  environment means the widget stops appearing on the sites that environment describes; a setting that
  is silently ignored is worse than one with consequences.

  The original rationale — that disabling an environment must never take a customer's widget offline —
  is answered by the blast radius rather than by ignoring the setting: an origin with NO mapping is
  still allowed (a site nobody described is not a site anybody turned off), and each environment's
  origins match their own rows, so disabling staging cannot reach production. Both bounds are pinned
  in `Tests/ProjectAppUrlEnvironmentGuardTests.cs`.

  The superseded rule is left above deliberately: the reasoning behind it is still the reason the
  blast radius has to stay narrow.
  The existing `IgnoreQueryFilters()` on that query must be preserved, and the join to
  `AppEnvironment` needs it too (anonymous caller, no tenant claim).

**Decision 8 — `MaxEnvironments` stays display-only; enabling does not consume it.** Turning
`enforced: false` → `true` (`EntitlementCatalog.cs:73`) would immediately start rejecting
`POST /api/admin/environments` for any tenant already above the default of 3 — a silent, breaking
monetization change smuggled in under a URL-model refactor. Out of scope; if it is ever wanted, the
count to enforce is *enabled, tenant-owned* environments, and it belongs in the plan's entitlement
wiring (§20, dissolved into feature-attached wiring), not here.

**Decision 9 — `AppEnvironmentService.DeleteAsync` keeps refusing to delete an in-use environment.**
Unchanged (`:97-102`). Disabling is the new soft alternative, which is precisely why Decision 1 makes
the flag block rather than hide.

### Entity changes
```csharp
public class AppEnvironment : BaseEntity
{
    public string Name { get; set; } = string.Empty;

    // Whether this workspace environment may receive new project URLs and take part in origin
    // resolution. NOT the same thing as ProjectAppUrl.IsActive, which toggles ONE project's mapping
    // for ONE environment — this toggles the environment itself for the whole workspace.
    // Disabling blocks writes and resolution; it never hides or deletes existing URLs.
    public bool IsEnabled { get; set; } = true;

    // Set only on the global "default" row (R1-09): the environment the old single-AppUrl model used
    // as a dumping ground. Retired rows are hidden from the "add URL" picker but still render for
    // any URL still attached to them. A tenant's own environment named "default" is never retired.
    public bool IsRetired { get; set; } = false;

    public Guid? OwnerId { get; set; }
    public ICollection<ProjectAppUrl> ProjectAppUrls { get; set; } = new List<ProjectAppUrl>();
}
```
Mapping (`AppEnvironmentMapping.cs`): `b.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true);`
and `b.Property(x => x.IsRetired).HasColumnName("is_retired").HasDefaultValue(false);` — explicit column
names, matching every other property in that file.

### Endpoints
| Verb & route | Change |
|---|---|
| `GET /api/admin/environments` | `AppEnvironmentResponse` gains `IsEnabled`, `IsRetired`, and **`ProjectUrlCount`** (how many non-deleted `ProjectAppUrl` rows reference it — the dashboard's "disabling this affects N projects" warning). Ordering unchanged (global first, then name), plus retired rows last. |
| `PATCH /api/admin/environments/{id}` | `UpdateAppEnvironmentRequest` gains `bool? IsEnabled`. Null ⇒ leave unchanged (patch semantics, matching `UpdateProjectRequest`'s nullable style). Renaming still requires `CanManage`; **so does toggling** — a tenant cannot disable a *global* environment for everyone. **Decision 10:** a tenant that wants a global environment off for itself must not be able to affect other tenants, so this release simply forbids it (`403 NotManageable`, the existing path); a per-tenant override of a global environment is the `RoleTenantOverride` pattern and is **out of scope**, noted in the plan's hold list. |
| `POST /api/admin/projects` | `CreateProjectRequest` gains `AppEnvironmentId` (Decision 5). |
| `PUT /api/admin/projects/{id}/app-urls/{environmentId}` | Now rejects a disabled/retired environment with `400 AppEnvironment.NotEnabled` (Decision 6). Otherwise unchanged. |
| `GET /api/admin/projects/{id}/app-urls` | `ProjectAppUrlResponse` gains `EnvironmentIsEnabled` so the dashboard can grey the row. |
| `GET /api/admin/projects` / `POST` / `PATCH` | `ProjectResponse` gains `AppUrls: List<ProjectAppUrlResponse>`; `AppUrl` kept, documented as a mirror of `local`. |

### Data
- New columns `app_environments.is_enabled` (bool, not null, default true), `app_environments.is_retired`
  (bool, not null, default false) — additive.
- Migration `MigrateDefaultProjectAppUrlsToLocal` (Decision 3) — ensures the global `local` row exists,
  repoints global-`default` URL rows onto it, soft-deletes losers of the unique-index conflict, and
  marks the global `default` row `is_enabled = false, is_retired = true`.
- No column is dropped this release.

## Tasks
1. `Domain/Entity/AppEnvironment.cs` — add `IsEnabled`, `IsRetired` with the comments above.
2. `Infrastructure/Mappings/AppEnvironmentMapping.cs` — explicit `HasColumnName` + `HasDefaultValue`
   for both.
3. `just migrate name="AddAppEnvironmentEnabledFlags"` — columns only (additive, defaults make it a
   no-op for existing rows).
4. `just migrate name="MigrateDefaultProjectAppUrlsToLocal"` — per Decision 3, in this order:
   ensure the global `local` row; soft-delete `default` rows for projects that already have a `local`
   row; repoint the remainder; set `is_enabled=false, is_retired=true` on the global `default`.
   Idempotent — re-running finds nothing to do.
5. `API/Seed/AdminSeeder.cs` — `DefaultAppEnvironments` becomes `{ "local", "prod", "staging", "testing" }`
   (drop `default`); keep the every-boot backfill; add the idempotent "retire the global `default`"
   step next to the existing role upgrades (`:53-67`); **rewrite the `:81-105` backfill** to target
   `local` instead of `default`.
6. `Application/DTOs/AppEnvironment/AppEnvironmentResponse.cs` — `IsEnabled`, `IsRetired`,
   `ProjectUrlCount`. `UpdateAppEnvironmentRequest.cs` — `bool? IsEnabled`.
7. `Application/Services/Implementation/AppEnvironmentService.cs` — `ListAsync` projects the three new
   fields (count via a grouped sub-query, not N+1) and orders retired last; `UpdateAsync` applies
   `IsEnabled` when non-null under the existing `CanManage` gate; `CreateAsync` sets `IsEnabled = true`.
8. `Application/Resources/MessageKeys.cs` — `AppEnvironment.NotEnabled`.
9. `Application/DTOs/Project/CreateProjectRequest.cs` — `AppEnvironmentId` (Decision 5);
   `ProjectAppUrlResponse.cs` — `EnvironmentIsEnabled`; `ProjectResponse.cs` — `AppUrls` + the XML
   comment deprecating `AppUrl`.
10. `Application/Services/Implementation/ProjectService.cs` —
    (a) rename `SyncDefaultAppUrlAsync` → `SyncPrimaryAppUrlAsync`, target `local`, update both
    call-sites (`:89-90`, `:224-227`) and the method comment;
    (b) `CreateAsync` honours `request.AppEnvironmentId` with the four outcomes in Decision 5;
    (c) `SetAppUrlAsync` rejects disabled/retired with `400` and moves the legacy write-back from
    `default` to `local` (`:324-330`);
    (d) `ListAppUrlsAsync` includes `EnvironmentIsEnabled`;
    (e) `MapToResponse` fills `AppUrls`;
    (f) `CheckWidgetActiveAsync` joins `AppEnvironment` (keeping `IgnoreQueryFilters`) and treats a
    disabled environment's row as absent — with the comment from Decision 7.
11. `Application/Services/Implementation/ExtensionService.cs` — `FindProjectForOriginAsync` requires
    `u.AppEnvironment.IsEnabled`; legacy `Project.AppUrl` fallback unchanged.
12. `Application/Validators/` — `CreateProjectValidator`: `AppEnvironmentId`, when present, must be
    `> 0`. (Existence/enabled checks stay in the service, where the tenant scope is available.)
13. Tests (task 15 lists the classes).
14. `e2e/scripts/seed.mjs` — keeps sending `appUrl` on create (`:67`) and via `PATCH` (`:71`); add the
    resolved environment to `state/expected.json` if the probe asserts on it. No behaviour change
    expected — this is the regression check that the `local` default works for existing callers.
15. Docs: `AGENTS.md` one line on the two environment concepts, pointing at this doc.

## Dashboard tasks
- Regenerate services: `AppEnvironmentResponse` (`isEnabled`, `isRetired`, `projectUrlCount`),
  `UpdateAppEnvironmentRequest.isEnabled`, `CreateProjectRequest.appEnvironmentId`,
  `ProjectAppUrlResponse.environmentIsEnabled`, `ProjectResponse.appUrls`.
- **Create-project dialog (the founder's explicit requirement):** the environment-URL table is
  rendered **from the start**, not behind an "add URL" button, pre-populated with one row —
  environment select (enabled, non-retired; `local` pre-selected) + URL input. Adding more rows before
  the project exists is optional; if the dialog only submits one, the rest are added on the project
  page afterwards. The old bare "App URL" input is gone.
- Environments screen: an **Enabled** toggle per environment, disabled for rows the caller cannot
  manage; a confirm dialog when disabling one with `projectUrlCount > 0` naming the count; retired rows
  shown greyed, last, and excluded from every environment picker.
- Project page: URL rows whose environment is disabled render greyed with a "environment disabled"
  hint and a link to the environments screen. Rows on retired environments are still editable/deletable.
- Remove any remaining UI that writes `project.appUrl` directly.

## Docs
**Updates `landing/docs/project-settings.html`** with an *Environments* section: what a workspace environment is (and that it is **not** the Local/Staging/Production tag on a comment), how to enable one, how to give a project a URL per environment, that `local` is the default for a new project, and — for existing projects — that a URL previously on `default` now appears under `local`. Answers: *“where do I tell it my app lives?”*

## Tests
- Unit: `Tests/AppEnvironmentEnabledFlagTests.cs` (default true; `PATCH` toggles under `CanManage`; a
  tenant cannot toggle a global environment → 403; `ProjectUrlCount`) ·
  `Tests/ProjectAppUrlEnvironmentGuardTests.cs` (create/set against enabled, disabled, retired, foreign,
  missing environments; the four Decision-5 outcomes) ·
  `Tests/DefaultEnvironmentMigrationTests.cs` (repoint, unique-index conflict keeps `local`, idempotent
  re-run, tenant-owned `default` untouched) ·
  `Tests/WidgetActivationTests.cs` **extended** (disabled environment ⇒ row treated as absent ⇒ widget
  stays active; per-mapping `IsActive=false` still blocks) ·
  `Tests/ProjectAppUrlSyncTests.cs` **rewritten** for `local` (its whole premise is the `default` sync —
  name it explicitly as a file that must change) ·
  `Tests/AppEnvironmentServiceTests.cs` extended (new fields in `ListAsync`).
- Integration: `ExtensionService.FindProjectForOriginAsync` skips URLs on disabled environments.
- E2E: `docs/roadmap/testing/R1-09-tests.md`, scenarios `R1-09-01` … `R1-09-10`.

## Acceptance criteria
- [ ] A fresh database seeds `local`, `prod`, `staging`, `testing` and **no** `default`.
- [ ] An upgraded database: the global `default` is `isEnabled=false, isRetired=true`, and every
      project that had a `default` URL now has the same URL on `local`, with no duplicate rows.
- [ ] A project that already had both `default` and `local` URLs keeps its `local` one; the `default`
      row is soft-deleted; re-running the migration changes nothing.
- [ ] A tenant's **own** environment named `default` is untouched (still enabled, not retired).
- [ ] `POST /api/admin/projects { key, name, appUrl }` with no `appEnvironmentId` creates the project
      and one `ProjectAppUrl` row on `local`; the response's `appUrls` has exactly that row and
      `appUrl` mirrors it.
- [ ] The same call with an explicit enabled `appEnvironmentId` lands on that environment; with a
      disabled or retired one → `400 AppEnvironment.NotEnabled`; with another tenant's id → `404`.
- [ ] `PUT …/app-urls/{environmentId}` on a disabled environment → `400`; on an enabled one → 200 and
      the row appears in `GET …/app-urls` with `environmentIsEnabled: true`.
- [ ] Disabling an environment that has URLs succeeds, leaves the rows in place and listed
      (`environmentIsEnabled: false`), and re-enabling restores resolution.
- [ ] With an environment disabled, the extension's origin lookup no longer matches its URLs; the
      widget gate still returns `active: true` for an origin matching only that row (it is treated as
      absent, not as a block), and still returns `active: false` when a matching row has
      `IsActive == false` on an **enabled** environment.
- [ ] A quick-access invite still works for a project whose URL came through the migration
      (`InviteService:525-526` sees a non-empty `Project.AppUrl`).
- [ ] A tenant-defined environment (e.g. `qa`) accepts a URL, resolves, and is invisible to tenant B.
- [ ] `just fmt`, `dotnet build`, `just test` green; dashboard regenerated.

## Rollout / compatibility
- **Additive only.** Two new columns with defaults; no drop. `Project.AppUrl` survives this release —
  its removal is an R2 follow-up gated on `InviteService` and `ExtensionService` reading the `local`
  `ProjectAppUrl` row instead.
- **Existing installs:** nothing changes until an admin disables an environment. The `default` → `local`
  move is transparent: same URL, same origin resolution, different environment name in the UI.
- **Self-hosters:** the migration and the seeder step both run on boot (`Program.cs:123-129`) and are
  idempotent; a re-run or a rollback-then-forward is safe. A rollback to the previous binary leaves the
  URLs on `local` — the old code looks for `default` and would find no row to sync, so
  `Project.AppUrl` (untouched, still populated) keeps the extension's legacy fallback working. State
  this in the release note.
- **API consumers:** `ProjectResponse.AppUrl` is unchanged in shape and meaning; `appUrls` is additive.
  `CreateProjectRequest.AppEnvironmentId` is optional, so every existing caller — including the browser
  extension and `e2e/scripts/seed.mjs` — keeps working untouched.
- **R1-02 sequencing** — see Prerequisites.

## Report template
Files changed (grouped: domain/migration/service/api/tests/e2e) · `just test` summary · the migration's
before/after row counts for `default` vs `local` on a database seeded with the old code · curl
transcript for the four Decision-5 create outcomes and the disabled-environment `400` · confirmation
that `Tests/ProjectAppUrlSyncTests.cs` was rewritten rather than deleted · dashboard follow-ups.
