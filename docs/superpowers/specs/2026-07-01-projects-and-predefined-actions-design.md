# Design: Dashboard-managed projects + predefined comment actions

**Status:** Draft for review (GLM + owner). **Date:** 2026-07-01.

## Goal
Two coupled changes:
1. **Stop consumer-side project auto-creation.** Projects must be defined in the dashboard before
   the widget can post to them. (Today `ProjectService.EnsureAsync` lazily self-registers an unknown
   key — `ProjectService.cs:96-110`. We remove that.)
2. **Predefined comment actions.** Admin-defined `{ text, prompt }` options a stakeholder picks when
   leaving a comment. The **visible text** is shown to the user; the **prompt** is what the apply-time
   LLM receives. Scoped at **tenant** and **project** level now, with the model **built to add user
   scope later** (when users can define their own configs).

## Non-goals (v1)
- Multi-select picking (design stores a single picked action; note the extension path).
- Per-project override/hide of tenant-wide actions (start with plain union).
- User-scoped actions UI (only the data model is future-proofed for it).

## Current architecture (verified)
- `Project { Id, Key, Name, IsActive, OwnerId }`; keys are **owner-scoped** (`Key + OwnerId`,
  `EnsureAsync` `ProjectService.cs:92`). `ProjectsController` already has `List`, `POST Create`,
  `PATCH Update`.
- `CommentService.CreateAsync` calls `EnsureAsync(projectKey)` first; a disabled project returns
  `Conflict` (→ the widget already **hides silently** on 409).
- Comments carry an `Element` capture value object. Tenant isolation is via EF global query filters
  (default-deny); anonymous/system paths use `IgnoreQueryFilters()` + explicit `OwnerId`.
- The apply workflow reads self-contained `pending.json` entries (selectors, snapshot, replies, etc.).

## Change 1 — dashboard-managed projects
- `EnsureAsync(key)` becomes a **strict resolver**: look up by `Key + OwnerId`; if missing → return
  `NotFound` (do NOT create). Disabled stays `Conflict`. Rename to `ResolveAsync` for clarity, or keep
  the name with the new contract.
- **Widget behavior on unknown project:** treat like disabled — **hide silently** (only a 404/409 in
  the network tab, never a console error for the consumer's users). Consistent with what we already do.
- **Existing projects are safe** — everything auto-created so far already exists in the DB. Only *new*
  undefined keys stop working (the intended behavior).
- Dashboard already creates projects (`ProjectsController.Create`); the create/edit **form** is where
  predefined actions are also edited (Change 2).
- **Onboarding tradeoff (deliberate):** a developer must create the project in the dashboard and copy
  its key into the widget before it works. This is the point — it enables attaching predefined actions
  and stops key spam.

## Change 2 — predefined actions

### Data model (future-proofed for user scope)
New entity `PredefinedAction`:
```
PredefinedAction {
  Id            int
  OwnerId       Guid       // tenant — ALWAYS set (isolation boundary)
  ProjectId     int?       // null = not project-specific
  UserId        Guid?      // null = not user-specific   (FUTURE: user-defined configs)
  Text          string     // visible label the stakeholder picks
  Prompt        string     // sent to the apply-time LLM — NEVER exposed to the browser
  IsActive      bool
  SortOrder     int
  + BaseEntity (timestamps, soft-delete)
}
```
**Scope is derived from which of `ProjectId` / `UserId` is set:**
- Tenant-wide: `ProjectId = null, UserId = null`.
- Project: `ProjectId` set.
- User (FUTURE): `UserId` set.

**Effective set** for project `P` (owner `T`), requesting user `U`:
`OwnerId = T AND (ProjectId IS NULL OR ProjectId = P) AND (UserId IS NULL OR UserId = U)`.
This query already includes the user dimension, so adding user scope later is data-only (no reshape).

### Attaching a picked action to a comment (snapshot, not FK)
When a user picks an action, the server **snapshots** its `{text, prompt}` onto the comment:
- v1 (single-select): two nullable columns on `Comment` — `PickedActionText`, `PickedActionPrompt`.
- Future (multi-select): a child collection `CommentAction[]`; v1's two columns migrate into it.

Why snapshot: (1) the **prompt never reaches the browser** (only text does), (2) editing/deleting an
action definition later does not rewrite historical comments.

### API
- Admin CRUD (auth, tenant-scoped) for predefined actions:
  - Nested in project create/edit for **project-scoped** ones: `CreateProjectRequest` /
    `UpdateProjectRequest` gain `predefinedActions: [{ id?, text, prompt, sortOrder, isActive }]`;
    update reconciles add/update/soft-delete.
  - A **tenant-scoped** management endpoint set (e.g. `GET/POST/PATCH/DELETE /api/admin/predefined-actions`
    with `ProjectId = null`) for workspace-wide actions.
- **Widget-facing read** (consumer, keyed by project key, returns the *effective* set **without
  prompts**): `GET /api/projects/{key}/predefined-actions` → `[{ id, text }]`. Resolves owner from the
  project key; returns only active, in-scope actions.
- **Comment create** accepts an optional `predefinedActionId`; the server validates it is active and
  in-scope for the resolved project, then snapshots `{text, prompt}` onto the comment. An invalid/out-
  of-scope id is rejected (not silently dropped).

### Apply workflow
The snapshotted `PickedActionPrompt` is included in the self-contained `pending.json` entry (e.g.
`predefined_prompt`) so the apply-time LLM receives it alongside the comment body/selectors.

### Dashboard (×3: Angular / React / Vue)
- **Project create/edit form:** a repeatable "form group" (add/remove rows of `{text, prompt}`) for
  project-scoped actions.
- **Tenant-wide management:** a "Predefined actions" section at workspace level (candidate home: the
  admin Settings page we just built, or a dedicated page).
- (FUTURE) user-level management lives on a user profile/config page — out of scope now.

### Widget (`pointer.js`)
- Fetch the effective actions for its project (`/api/projects/{key}/predefined-actions`) and render a
  **single-select picker** in the comment popover. On submit, send the chosen `predefinedActionId`.
- Hide silently if the project is unknown/disabled (Change 1).

## Migration & rollout
- New `PredefinedAction` table + two nullable `Comment` columns (snapshot). Additive; existing data
  untouched. `EnsureAsync` change is code-only.
- Coupled deploy (like the last one): API → publish clients → dashboards → widget.

## Open questions (for GLM + owner)
1. Confirm **silent-hide** on unknown project (vs a visible "project not configured" notice).
2. `EnsureAsync` rename → `ResolveAsync`, or keep name with new strict contract?
3. Tenant-wide actions home: a section on the existing Settings page, or a dedicated page?
4. Widget read endpoint shape/caching (these are small, cache with revalidation like i18n).
5. Any concern with snapshotting prompt onto the comment for tenants that later edit the wording?
