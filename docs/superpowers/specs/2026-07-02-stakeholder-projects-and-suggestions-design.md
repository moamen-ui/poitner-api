# Design: Stakeholder project permissions + predefined-prompt suggestions

**Status:** Draft for GLM review. **Date:** 2026-07-02.

## Context
Today only **admins** (GrantsAdmin roles) can touch projects — `ProjectsController` is
`[Authorize(Policy=Admin)]` at `/api/admin/projects` with List/Create/Update (no Delete). Stakeholders
(non-admin roles: Developer/PM/Tester/Client) are tenant members with **no dashboard project access**.
Predefined actions are admin-managed; the widget read exposes labels only (prompt stays server-side).

Requested change: let **stakeholders manage their own projects** and **suggest predefined prompts** on
projects they can't edit (admin-approved), while keeping admin fully in control.

## Requirements (verbatim intent)
1. A stakeholder can **see his workspace's projects**.
2. Stakeholder can **add** projects.
3. Stakeholder can **update his own** projects only.
4. Stakeholder can **delete his own** project **only if it has no comments yet**; otherwise **only admin** can delete.
5. **Admin has full access** to all projects.
6. Stakeholders can **"suggest" predefined prompts** for projects they **cannot** edit.
7. Adding a suggestion **notifies the admin** for review + approve.
8. **All users can see** the predefined prompts (including admin-set ones).

## Key definitions / decisions (for GLM to challenge)
- **"Own project" = `Project.CreatedBy == currentUser.Id`.** `BaseEntity.CreatedBy` (Guid) is already
  auto-stamped from the JWT `sub` on insert (`AppDbContext.cs:67,71`) — no new column needed. `OwnerId`
  stays the **tenant** (isolation boundary, unchanged).
- **"Admin" throughout = `ICurrentUser.IsAdmin`** (GrantsAdmin), which includes scoped Workspace-Admins
  and super-admins. Super-admin additionally bypasses tenant scope (existing).
- **Endpoint approach:** broaden the existing `ProjectsController` from `[Authorize(Policy=Admin)]` to
  plain `[Authorize]` and enforce fine-grained rules in `ProjectService` per operation. Rationale:
  the 3 dashboards already consume `/api/admin/projects` (generated hooks) — this avoids a parallel
  controller + client re-wire. **Cosmetic caveat:** the route keeps `/api/admin/` though non-admins now
  use it; acceptable (the CORS "dashboard" allow-list already covers `/api/admin/*`). *GLM: agree, or
  prefer a new `/api/projects` surface?*
- **Prompt visibility (reconciles #8 with the earlier "prompt never to the browser" rule):** the
  earlier rule protects the **widget** (runs on arbitrary customer pages → prompt must not reach that
  DOM). #8 is about the **dashboard**, whose users are **authenticated tenant members**. So: a new
  **authenticated dashboard read** may return prompts to tenant members; the **widget read stays
  label-only**. *GLM: confirm this split is acceptable.*

## Authorization matrix (enforced server-side in ProjectService)
| Op | Stakeholder (non-admin) | Admin | Notes |
|---|---|---|---|
| List projects | ✅ tenant's projects | ✅ | tenant query filter already scopes; super-admin sees all |
| Create project | ✅ (CreatedBy=self, OwnerId=tenant) | ✅ | |
| Update project (name, predefined actions, isActive) | ✅ only if `CreatedBy==self` | ✅ any | else `NotFound` (don't reveal) |
| Delete project | ✅ only if `CreatedBy==self` **AND** 0 comments | ✅ any (cascade soft-delete comments+actions) | non-owner or has-comments → `Forbidden`/`Conflict` for stakeholder |
| Add predefined action to a project | ✅ if can Update it (own/admin) | ✅ | via project edit (existing reconcile) |
| Suggest predefined action | ✅ on a project he **can't** edit | (admin just adds directly) | creates Pending suggestion |
| Approve/reject suggestion | ❌ | ✅ | admin-only review |
| View predefined prompts (dashboard) | ✅ (tenant's) | ✅ | authenticated read incl. prompt |

## Data model
- **No change** to `Project` (reuse `CreatedBy`).
- **New entity** `PredefinedActionSuggestion : BaseEntity` (table `predefined_action_suggestions`):
  ```
  OwnerId    Guid?   // tenant (nullable to match Project/PredefinedAction owner model)
  ProjectId  int     // the target project (required — suggestions are always project-scoped)
  Text       string  // proposed label (≤256)
  Prompt     string  // proposed LLM prompt (text)
  Status     enum SuggestionStatus { Pending=1, Approved=2, Rejected=3 }
  ReviewedBy Guid?   // admin who approved/rejected
  ReviewedAt DateTime?
  ```
  `SuggestedBy` = `BaseEntity.CreatedBy` (reuse). Strict-own query filter (like Project/Comment).
  Index `(OwnerId, Status)`.

## API changes
**Projects (broaden `ProjectsController` → `[Authorize]`, enforce in `ProjectService`):**
- `GET /api/admin/projects` — now any tenant member; returns tenant projects, each flagged with
  `canEdit`/`canDelete` (computed: `IsAdmin || CreatedBy==caller`, and delete also needs 0 comments for
  non-admin) so the UI can render correctly. Add `CreatedBy` + `commentsCount` + `canEdit`/`canDelete`
  to `ProjectResponse`.
- `POST` create — any tenant member (unchanged logic; CreatedBy auto).
- `PATCH {id}` update — `ProjectService.UpdateAsync` gains an authorization guard: `IsAdmin ||
  project.CreatedBy == _currentUser.Id`, else `Result.NotFound`. (Predefined-action reconcile inside
  update is likewise gated by this.)
- **NEW** `DELETE {id}` — `ProjectService.DeleteAsync`:
  - `IsAdmin` → cascade soft-delete (project + its comments + replies + predefined actions +
    suggestions), mirror the scoping in `TenantService.HardDeleteAsync` but **soft** + single-project.
  - else if `project.CreatedBy == caller` **and** `commentsCount == 0` → soft-delete project (+ its
    predefined actions/suggestions).
  - else → `Result.Forbidden` (not owner) / `Result.Conflict` (has comments).

**Predefined-prompt suggestions (new `SuggestionService` + endpoints):**
- `POST /api/projects/{id}/predefined-action-suggestions` — `[Authorize]`; body `{text, prompt}`.
  Guard: caller must be a tenant member who **cannot edit** that project (i.e., not admin and not the
  creator — admins/owners just add directly, so a suggestion from them is rejected with guidance).
  Validate the project is in the caller's tenant. Create `Pending`. **Notify admin** (see below).
- `GET /api/admin/predefined-action-suggestions?status=Pending` — `[Authorize(Policy=Admin)]`; list.
- `POST /api/admin/predefined-action-suggestions/{id}/approve` — creates a real `PredefinedAction` on
  the target project (OwnerId=tenant, ProjectId set), marks suggestion `Approved` + `ReviewedBy/At`.
- `POST /api/admin/predefined-action-suggestions/{id}/reject` — marks `Rejected`.
  (Approve/reject loaded via an explicit own-owner scope, mirroring `LoadOwnTenantWideAsync`.)

**Predefined-prompt dashboard read (#8):**
- `GET /api/projects/{key}/predefined-actions/full` (or extend admin list) — `[Authorize]` (any tenant
  member); returns the project's effective predefined actions **including prompt** for dashboard display.
  Distinct from the existing widget read (`/api/projects/{key}/predefined-actions`, label-only). Keep the
  widget one unchanged.

**Notifications (#7):** on a new suggestion, notify the tenant's admin(s):
- Best-effort **email** via `IEmailService` to the workspace admin(s) (resolve the tenant's GrantsAdmin
  user emails), quota-guarded (same pattern as approve/reject/demo emails).
- In-dashboard: the pending-suggestions count powers a badge on the admin review page (polled via the
  list endpoint). *GLM: email + badge sufficient, or need a persistent notification entity? (Proposing
  no new notification table for v1 — the Pending suggestions list IS the queue.)*

## Dashboard (×3: Angular / React / Vue)
- **Projects page access for stakeholders:** the SPA routers currently gate `projects` behind the
  admin guard — allow authenticated non-admins to reach it. Render: Create always; per-row **Edit**
  only when `canEdit`; **Delete** only when `canDelete` (with tooltip "has comments — ask an admin"
  when blocked); admins see all controls.
- **Suggest predefined prompt:** on projects the user can't edit, a "Suggest prompt" action → dialog
  (text + prompt) → POST suggestion → toast "sent for admin review".
- **Admin suggestions review:** a new section/page listing Pending suggestions (project, suggester,
  text, prompt) with Approve/Reject; badge with the pending count.
- **View predefined prompts:** all users can open a project's predefined prompts (read, incl. prompt).
- i18n en + ar for all new strings; keep the 3 apps consistent.

## Migration & rollout
- One migration: `predefined_action_suggestions` table + `SuggestionStatus` (int). `ProjectResponse`
  gains fields (no DB change for CreatedBy — already stored). Additive.
- Coordinated deploy: API → publish clients (adds suggestion + project-delete hooks) → bump/build/deploy
  dashboards → **full E2E before deploy is trusted** (see below).

## Verification / E2E (must pass before trusting deploy)
- **Projects authz:** stakeholder A creates project P → can update/delete P (0 comments); adds a comment →
  can no longer delete P (Conflict), admin still can (cascade). Stakeholder B **cannot** update/delete A's
  project (NotFound/Forbidden). Admin can do everything. Cross-tenant isolation intact.
- **Suggestions:** stakeholder suggests on a non-owned project → Pending created + admin email attempted;
  admin approves → a real PredefinedAction appears on the project + shows in the widget picker; reject →
  no action created. A stakeholder cannot approve/reject. Cross-tenant: can't suggest/see another tenant's.
- **Prompt visibility:** authenticated dashboard read returns prompts to tenant members; the **widget read
  still returns labels only** (no prompt) — assert both.
- **Regression:** existing admin project create/edit + predefined-action multi-select + comment flows still work.

## v2 — GLM review folded in (BINDING — governs where it conflicts with the above)
GLM verdict was NEEDS-CHANGES; these 6 corrections are now part of the spec and MUST be implemented:

1. **[HIGH security] Keep the apply-queue admin-gated.** When `ProjectsController` class attribute is
   broadened `[Authorize(Policy=Admin)]` → `[Authorize]`, the `GET {key}/apply-queue` action (the ONLY
   endpoint that emits `PickedActionPrompt`, via `CommentService.ListApplyQueueAsync`/`MapToApplyItem`)
   MUST carry its own action-level `[Authorize(Policy=Policies.Admin)]`. Add a test: non-admin → 403 on
   `/api/admin/projects/{key}/apply-queue`.
2. **[HIGH correctness] Cascade deletes key off `ProjectId`, NOT `OwnerId`.** Do NOT mirror
   `TenantService.HardDeleteAsync` (it scopes by `OwnerId == tenantId` = tenant-wide → would wipe the
   whole tenant). `ProjectService.DeleteAsync` cascade (admin path), inside `ExecuteInTransactionAsync`,
   `IgnoreQueryFilters`, soft (`DeletedAt=now, DeletedBy=caller`): comments `Where(c => c.ProjectId==pid)`;
   replies `Where(r => commentIds.Contains(r.CommentId))` (from the loaded comment ids — NOT by OwnerId);
   predefined actions `Where(a => a.ProjectId==pid)`; suggestions `Where(s => s.ProjectId==pid)`; then the
   project row itself. The **initial project load** for both Update and Delete uses the NORMAL query
   filter (`GetByIdAsync`) — IgnoreQueryFilters only inside the cascade sub-queries.
3. **[MEDIUM-HIGH] `ProjectResponse` already carries prompts** (`PredefinedActions:List<PredefinedActionResponse>`
   incl. `Prompt`), so broadening `List`/`Create`/`Update` already delivers prompts to authenticated tenant
   members — that IS the dashboard prompt surface. **DROP the redundant `/predefined-actions/full` endpoint.**
   Add a regression test: the widget read (`PredefinedActionsController.GetForProject` → `PredefinedActionOption`)
   and any `[AllowAnonymous]` surface NEVER return `Prompt`.
4. **[MEDIUM] Approve re-validates the target project.** `approve` must assert the project still exists
   (`DeletedAt==null`) and is active before minting the `PredefinedAction`; else fail (no action dangling
   off a deleted project).
5. **[MEDIUM] `PredefinedActionSuggestion` uses a STRICT-OWN query filter** (`superAdmin || OwnerId==TenantId`)
   — never own-plus-global. Never write a null-owner suggestion. Approve/reject load via explicit own-owner
   scope (mirror `LoadOwnTenantWideAsync`).
6. **[MEDIUM] Batch `commentsCount` in `ListAsync`** (one `GroupBy(ProjectId).Count()`, no N+1) and
   **re-enforce the delete gate server-side** (`CreatedBy==caller && commentsCount==0`) in `DeleteAsync` —
   `canEdit`/`canDelete` in `ProjectResponse` are UI HINTS ONLY.

Minor (fold in, non-blocking): resolve `CreatedBy` to a display name in `ProjectResponse` rather than raw
Guid (PII); use `Forbidden` (not `NotFound`) for in-tenant non-owner update since the project is already
visible in List; exclude soft-deleted projects' suggestions from the admin review list; map `SuggestionStatus`
as int (existing enum convention).

## Open questions for GLM (ANSWERED by review — recorded)
Q1 broaden in place ✓ · Q2 dashboard prompt read OK (widget label-only; apply-queue stays admin) ✓ ·
Q3 "can't edit → may suggest" ✓ · Q4 email + pending-badge, no notification entity ✓ ·
Q5 admin cascade delete-with-comments, **ProjectId-scoped** ✓.

## Open questions (original)
1. Broaden `/api/admin/projects` in place vs. a new `/api/projects` stakeholder surface?
2. Dashboard-read of prompts to authenticated tenant members — acceptable given the widget stays
   label-only? Any leak path?
3. Suggestion authorization: is "can't edit → may suggest" the right gate, or should any member
   (incl. owners) be allowed to suggest for consistency?
4. Notifications: email + pending-list badge enough for v1, or a persistent notification entity?
5. Delete cascade: soft-delete project + children (proposed) vs. block delete when comments exist even
   for admin (requirement says admin CAN delete-with-comments → cascade). Confirm cascade shape.
