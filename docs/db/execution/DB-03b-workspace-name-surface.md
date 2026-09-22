# DB-03b — Workspace name surface: read/rename endpoints, `TenantResponse.WorkspaceName`, Settings card

Companion to [DB-03](DB-03-workspaces-table.md) (owner decision Q3, 2026-09-22: the workspace name
is its own attribute, not the admin's display name). Rules: R8 (tenancy invariant on the new
service). **Class: code only — no migration, no schema change.** Requires DB-03 merged (the
`Workspace` entity, `IUnitOfWork.Workspaces`, the query filter). Ships in the same deploy as DB-03
or any later ordinary deploy; the dashboard half follows the once-per-phase client regeneration
(CLAUDE.md rules 1–5, `dashboard-agent`).

## 1. Goal

After DB-03 every workspace is named `Workspace` (the placeholder). This doc gives the workspace
admin a way to read and set the real name from the dashboard Settings page, shows super admins the
workspace name next to the admin's name in the Tenants list, and flags a still-unnamed workspace so
the UI can prompt. User-visible reason: the header label and the anonymous invite preview stop
saying `Workspace` as soon as the admin types a name.

## 2. Prerequisites (verified facts, 2026-09-22 @ `ff25a8d`, plus DB-03's additions)

- Controller precedent: `API/Controllers/Admin/WorkspaceController.cs:15-20` — `[Route("api/admin/workspace")]`, `[Tags("Workspace")]`, `[Authorize(Policy = Policies.Admin)]`, `[Produces("application/json")]`, primary-constructor injection; actions annotate the **inner** type (`:24,34`). `Workspace` is already in `orval.config.ts:6` `filters.tags`; so is `Tenants`.
- Service guard precedent: `Application/Services/Implementation/CommentFieldService.cs:246-257` — Forbidden for `IsSuperAdmin`, for `IsQuickAccess`, and when `TenantStamp.OwnerFor(_currentUser) ?? _currentUser.Id` is null; the owner is always derived server-side. Interface doc-comment style: `Application/Services/Interfaces/ICommentFieldService.cs`.
- DI: Scrutor registers every class whose name ends in `Service` against its interface (`Application/DependencyInjection.cs:11-12`); nothing to add.
- Validator precedent: `Application/Validators/UpdateCommentFieldDefinitionsValidator.cs` (FluentValidation; the service re-validates regardless).
- Messages: `Application/Resources/MessageKeys.cs` — nested static classes per area (`User.DisplayNameRequired` at `:32`, `Common.Forbidden` used by CommentFieldService). No `Workspace` class exists yet.
- DTO folder: `Application/DTOs/Workspace/` (three comment-field DTOs today).
- `TenantService.ListAsync` (`TenantService.cs:36-126`): `tenantIds` at `:57`, batch loads at `:60-95`, `TenantResponse` construction `:102-124`; `CreateAsync` returns a `TenantResponse` at `:187-198`. `TenantResponse` (`Application/DTOs/Tenant/TenantResponse.cs`) has `DisplayName` (the admin's) and no workspace field.
- DB-03 facts this doc relies on: `Workspace.PlaceholderName == "Workspace"`; `Workspace.Name` `varchar(120)` NOT NULL with check `length(btrim(name)) > 0`; query filter on `Workspaces` (super admin sees all, tenant sees own); `AuthService.ResolveTenantNameAsync` reads `workspaces.name`.
- Dashboard (read-only knowledge, `../pointer-dashboard/react/src/`): Settings page composes cards and gates them by role — `features/settings/SettingsPage.tsx:489` (`useAuth()` → `isAdmin`, `isSuperAdmin`), `:984` `{isAdmin && !isSuperAdmin && <CommentFieldsCard />}`; card precedent `features/settings/CommentFieldsCard.tsx` (hooks `useGetApiAdminWorkspaceCommentFields`, `usePutApiAdminWorkspaceCommentFields`, `getGetApiAdminWorkspaceCommentFieldsQueryKey`, toast + `extractMessage`). Header label: `features/shell/Shell.tsx:85` (`useGetApiAuthMe`) and `:89,140-143` (`me.tenantName`). Tenants list column: `features/tenants/TenantsPage.tsx:368` renders `row.original.displayName`. Translations: `public/assets/i18n/en.json` and `ar.json` (loaded over HTTP, `src/i18n/index.ts:25`).

## 3. Design

### 3.1 API

| Method | Route | Tag | Body | 200 inner type | Who |
|---|---|---|---|---|---|
| `GET` | `/api/admin/workspace` | `Workspace` | — | `WorkspaceResponse` | workspace admin (policy `Admin`; super admin / quick access → 403) |
| `PUT` | `/api/admin/workspace/name` | `Workspace` | `UpdateWorkspaceNameRequest` | `WorkspaceResponse` | same |

`Application/DTOs/Workspace/WorkspaceResponse.cs`:
```csharp
public class WorkspaceResponse
{
    public Guid Id { get; set; }                 // == the JWT tenant claim / owner_id
    public string Name { get; set; } = string.Empty;
    /// <summary>True while Name is still the DB-03 placeholder ("Workspace") — the UI prompts.</summary>
    public bool IsPlaceholderName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
```
`Application/DTOs/Workspace/UpdateWorkspaceNameRequest.cs`: `public string Name { get; set; } = string.Empty;`

Validation (service **and** validator, same rules): trim; required after trim; ≤ 120 chars; no
control characters (`char.IsControl`). Messages: new `MessageKeys.Workspace` class with
`NameRequired = "Workspace name is required."`, `NameTooLong = "Workspace name must be 120 characters or fewer."`,
`NameInvalid = "Workspace name contains unsupported characters."`, `NotFound = "Workspace not found."`.

`IWorkspaceService` / `WorkspaceService` (`Application/Services/Interfaces/`, `…/Implementation/`),
constructor `(IUnitOfWork unitOfWork, ICurrentUser currentUser)`:

- `Task<Result<WorkspaceResponse>> GetAsync()` — guards copied from `CommentFieldService.cs:248-257`
  (super admin → Forbidden; quick access → Forbidden; no owner → Forbidden); then
  `_unitOfWork.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == owner)` **through the
  query filter** (no `IgnoreQueryFilters()` — the filter is the second line of defence); null →
  `NotFound(MessageKeys.Workspace.NotFound)`; map to the DTO with
  `IsPlaceholderName = row.Name == Workspace.PlaceholderName`.
- `Task<Result<WorkspaceResponse>> RenameAsync(UpdateWorkspaceNameRequest request)` — same guards;
  validate; load tracked through the filter; set `Name = trimmed`, `UpdatedAt = DateTime.UtcNow`,
  `UpdatedBy = _currentUser.Id`; `SaveChangesAsync`; return the DTO. Setting the name **to** the
  placeholder string is allowed (it just re-flags the prompt).

`TenantResponse.WorkspaceName` (`string`, super-admin list): in `ListAsync` add one batch load
after `:95` — `var wsNames = await _unitOfWork.Workspaces.IgnoreQueryFilters().AsNoTracking().Where(w => nonNullTenantIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name);`
— and set `WorkspaceName = wsNames.GetValueOrDefault(t.OwnerId!.Value, Workspace.PlaceholderName)`
in the projection at `:102-124`. In `CreateAsync` (`:187-198`) set `WorkspaceName = Workspace.PlaceholderName`.
Doc-comment on the property: "The workspace's own name (`workspaces.name`); `DisplayName` above is
the admin's."

### 3.2 Dashboard tasks (for the dashboard-agent, after the client is regenerated)

1. `features/settings/WorkspaceNameCard.tsx` (new; copy the card chrome and mutation pattern of
   `CommentFieldsCard.tsx`): `useGetApiAdminWorkspace()`; one `Input` bound to `name`, a Save
   button, `usePutApiAdminWorkspaceName` → on success `toast(t('settings.workspaceName.saved'))` and
   invalidate **both** `getGetApiAdminWorkspaceQueryKey()` and `getGetApiAuthMeQueryKey()` (so the
   header label in `Shell.tsx:89` updates without a reload). When `isPlaceholderName` is true, show
   a muted hint `t('settings.workspaceName.placeholderHint')`.
2. `features/settings/SettingsPage.tsx:984` — render `{isAdmin && !isSuperAdmin && <WorkspaceNameCard />}` **above** `<CommentFieldsCard />` (same gate).
3. `features/tenants/TenantsPage.tsx:368` — show `row.original.workspaceName` on its own line above the admin's `displayName` (or as a separate column; the agent picks what fits the table).
4. `public/assets/i18n/en.json` + `ar.json` — keys `settings.workspaceName.title`, `.label`,
   `.save`, `.saved`, `.placeholderHint` ("This workspace has not been named yet."), plus
   `tenants.workspaceName`. Arabic text supplied by the agent (RTL layout already handled globally).
5. Client regeneration is **once per phase** (`npm run generate-clients` → `npm run build-clients`;
   for local dev `npm run clients:local`) — not per PR. The `Workspace` and `Tenants` tags are already
   listed, so both new hooks and the new `TenantResponse.workspaceName` property appear automatically.

## 4. Safety classification

Code only. No migration, no data change except rows an admin explicitly renames. R8: the new
service reads through the tenant query filter; §6 test 4 proves tenant B cannot rename A.

## 5. File-level tasks (API repo)

1. `Application/DTOs/Workspace/WorkspaceResponse.cs`, `Application/DTOs/Workspace/UpdateWorkspaceNameRequest.cs` — per §3.1.
2. `Application/Resources/MessageKeys.cs` — add `public static class Workspace { … }` with the four constants (place it after the `User` class).
3. `Application/Services/Interfaces/IWorkspaceService.cs` — two methods, doc-comments in the `ICommentFieldService` style (name the routes).
4. `Application/Services/Implementation/WorkspaceService.cs` — per §3.1. Class name must end in `Service` (Scrutor).
5. `Application/Validators/UpdateWorkspaceNameValidator.cs` — copy `UpdateCommentFieldDefinitionsValidator.cs`'s shape; rules: `NotEmpty` (after `Trim`), `MaximumLength(120)`, `Must(no control chars)` with the `MessageKeys.Workspace` texts.
6. `API/Controllers/Admin/WorkspaceController.cs` — change the primary constructor to `(ICommentFieldService commentFields, IWorkspaceService workspaces)`; add before `GetCommentFields`:
   ```csharp
   /// <summary>The caller's workspace: id, own name, placeholder flag.</summary>
   [HttpGet]
   [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
   public async Task<IActionResult> Get()
   {
       var result = await workspaces.GetAsync();
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       if (result.IsNotFound) return NotFound(result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }

   /// <summary>Renames the caller's workspace (1–120 chars). Header label and invite preview follow.</summary>
   [HttpPut("name")]
   [ProducesResponseType(typeof(WorkspaceResponse), StatusCodes.Status200OK)]
   public async Task<IActionResult> Rename([FromBody] UpdateWorkspaceNameRequest request)
   {
       var result = await workspaces.RenameAsync(request);
       if (result.IsForbidden) return StatusCode(StatusCodes.Status403Forbidden, result);
       if (result.IsNotFound) return NotFound(result);
       return result.IsSuccess ? Ok(result) : BadRequest(result);
   }
   ```
7. `Application/DTOs/Tenant/TenantResponse.cs` — add `public string WorkspaceName { get; set; } = string.Empty;` after `DisplayName` with the doc-comment from §3.1. `Application/Services/Implementation/TenantService.cs` — the batch load and the two assignments from §3.1.
8. Tests (§6). 9. `just fmt`, `just test`. 10. Record the dashboard tasks (§3.2) in the PR description under **Dashboard tasks** (CLAUDE.md rule 1c); do **not** regenerate `clients/`.

## 6. Tests

New `Tests/WorkspaceServiceTests.cs` (InMemory fixture from `Tests/CommentFieldsTests.cs:28-36,61-62`; seed a `Workspace` row + admin per DB-03 test 7):

1. `Get_ReturnsPlaceholderFlag_UntilRenamed` — seeded name `Workspace` → `IsPlaceholderName == true`; after `RenameAsync("Acme Inc")` → `Name == "Acme Inc"`, flag false, `UpdatedAt != null`.
2. `Rename_Blank_Or_TooLong_Or_ControlChars_Rejected` — `"   "`, 121 chars, `"Acme"` → `IsSuccess == false`, row unchanged.
3. `Rename_SuperAdmin_And_QuickAccess_Forbidden` — `FakeCurrentUser { IsSuperAdmin = true }` and `{ IsQuickAccess = true, TenantId = A }` → `IsForbidden`.
4. `Rename_TenantB_CannotSeeOrRenameTenantA` — context for tenant B (`TenantId = B`), workspaces A and B seeded; `GetAsync()` returns B's row; `RenameAsync` changes only B; A's name unchanged. (R8 point 5.)
5. `MeResponse_TenantName_FollowsRename` — after rename, `AuthService` `MeAsync` (as in DB-03 test 7) returns the new name.
6. `TenantService_ListAsync_IncludesWorkspaceName` — extend an existing `ListAsync` test (`Tests/UserGovernanceTests.cs:349` area or `WorkspaceAdminOwnershipTests.cs`) with one assertion: `WorkspaceName == "Acme Inc"` for the seeded tenant, `DisplayName` still the admin's.

## 7. Acceptance criteria

1. `grep -c "ProducesResponseType(typeof(WorkspaceResponse)" API/Controllers/Admin/WorkspaceController.cs` → 2; `grep -n "\[Tags(\"Workspace\")\]" API/Controllers/Admin/WorkspaceController.cs` → 1 line (unchanged).
2. `curl -s http://localhost:8090/swagger/v1/swagger.json | jq '.paths["/api/admin/workspace"].get.tags, .paths["/api/admin/workspace/name"].put.tags'` → `["Workspace"]` twice; `jq '.components.schemas.TenantResponse.properties.workspaceName'` → non-null.
3. `just test` green; the 6 tests above pass.
4. Manual on the rehearsal API: login as the workspace admin → `GET /api/admin/workspace` → `{"name":"Workspace","isPlaceholderName":true,…}`; `PUT /api/admin/workspace/name {"name":"Acme Inc"}` → 200; `GET /api/auth/me` → `"tenantName":"Acme Inc"`; `GET /api/invites/<code>` (existing-workspace invite, anonymous) → `"workspaceName":"Acme Inc"`; super admin `GET /api/admin/tenants` → `"workspaceName":"Acme Inc"` next to the admin's `displayName`. A super-admin `PUT` → 403.
5. `psql`: `SELECT name, updated_at, updated_by FROM workspaces;` → the new name, a timestamp, the admin's `public_id`.
6. PR description has a **Dashboard tasks** section reproducing §3.2.

## 8. Rollback

Revert the commit; ordinary redeploy. No migration. Names already set stay in `workspaces.name`
(harmless; DB-03's `ResolveTenantNameAsync` keeps showing them). No dump requirement beyond the
ordinary `pre-deploy` one.

## 9. Release steps

1. Merge after DB-03 (same batch is fine — it is code only, `deploy-api.sh` applies no migration for it).
2. Ordinary deploy (`bash scripts/deploy-api.sh`) **or** ride along in the DB-03 contract deploy.
3. Verify criterion 4 against production with the real admin login; then the owner (or the admin) sets the real name from the Settings page once the dashboard has shipped — until then, `PUT /api/admin/workspace/name` via Swagger with the admin's token works.
4. Dashboard: the `dashboard-agent` regenerates the client from **production** (the publish workflow generates from the live API, so this step is after step 2), then implements §3.2.

## 10. Out of scope

A workspace-name field on `RegisterRequest`, `CreateTenantRequest`, `CreateTenantInviteRequest`
(already has `DisplayName` = workspace name) or `AcceptInviteRequest` — a follow-up once the owner
decides whether signup should ask for it; renaming by super admins on behalf of a tenant (add a
`PATCH /api/admin/tenants/{id}/name` later if asked); uniqueness of names (deliberately not
unique); any migration; `ON-DISK-CONTRACT.md`; the CLI; the widget.
