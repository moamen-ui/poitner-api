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

## v2 — revisions folded in from GLM design review (2026-07-01)
Resolved before implementation:

**Blockers**
- **EF query filter on `PredefinedAction` (was missing).** Register a strict-own global query filter in
  `AppDbContext.OnModelCreating` (same form as `Project`/`Comment`): `superAdmin || OwnerId == tenant`.
  Add the `DbSet` + a `PredefinedActionMapping`. Without this, admin CRUD cross-reads every tenant.
  `OwnerId` is ALWAYS set (never null) — do not add a super-admin "global action" path, or strict-own hides it.
- **Prompt must never serialize in comment DTOs.** `PickedActionPrompt` stays OFF `CommentResponse` and
  `CommentListItemDto` entirely (don't just omit in the mapper — keep it off the class so a careless map
  won't compile). Only `PickedActionText` may appear, and only if the widget needs it. Add a unit test
  asserting the serialized comment JSON contains no `prompt`/`PickedActionPrompt` key.
- **Widget must hide on 404 too.** The widget currently silent-hides ONLY on 409 (`pointer.js:861,1294`);
  a 404 today throws a visible error toast. The strict resolver returns 404 for a missing project, so
  extend both `fetchComments` and `saveComment` to `if (r.status === 409 || r.status === 404) disableSilently()`.
  Do NOT make the resolver return 409 for missing (conflates "disabled" vs "not configured").

**Isolation / endpoint shape**
- **Widget-read endpoint becomes `[Authorize]`, not anonymous.** Keys are owner-scoped (`key+ownerId`), so a
  key-only anonymous resolve collides across tenants (pre-existing flaw in `RoleService.ListPublicAsync` /
  `AuthService` register). The picker only renders post-login (`pointer.js:747` inits only with a token), so
  resolve the tenant from the JWT and scope the effective-set query through the strict resolver — no
  `IgnoreQueryFilters`, no collision.
- **Enumerate every key-resolution path** and state its contract: `EnsureAsync`/`ResolveAsync` (strict, owner-
  scoped), `UploadsController.Upload` (`:53`), `AuthService` register (`:149`, anonymous+`IgnoreQueryFilters`),
  demo provisioning. Decide per-path whether it goes strict+owner-scoped now or is explicitly left as-is; the
  "projects must be pre-defined" guarantee otherwise holds only for comment posting.

**Migration / data**
- `Prompt` column type = Postgres `text` (no `HasMaxLength` — prompts are multi-paragraph). `Text` bounded ≤256.
- Indexes: `HasIndex(OwnerId)` and `HasIndex(OwnerId, ProjectId)` (btree indexes NULLs → tenant-wide rows served).
- **Soft-delete discipline:** the global filter does NOT filter `DeletedAt`; every `PredefinedAction` query must
  add `DeletedAt == null` (mirrors `ProjectService`), or deleted actions reappear in the picker/reconcile.
- **Nested reconcile rule** (project update): `id` present → update, absent → add, absent-from-payload → soft-delete.
  Document as last-write-wins (or add a row-version optimistic token on `Project`). Snapshots insulate history.
- New `Comment` columns are nullable — never later make `NOT NULL DEFAULT` without a backfill.
- `SortOrder` defaulting: new action = `max(scope)+1`.

**Rollout / quality**
- Add `npm run generate-clients` to the rollout; new controllers follow the `[ProducesResponseType(typeof(Inner))]`
  + `[Produces("application/json")]` convention (AGENTS.md) so Orval types the envelope correctly.
- Tests: (1) query-filter isolation for `PredefinedAction`, (2) comment JSON has no prompt, (3) out-of-scope /
  inactive `predefinedActionId` on comment-create is rejected.
- User strings via `MessageKeys.*` (invalid action id, text required).
- **Stale-cache on submit:** if a cached picker references a just-deleted action id → server 4xx; the widget
  refetches the effective set once and retries (chosen over accepting a one-off error).
- Effective-set read must honor project `IsActive` (routing through the strict resolver already yields 409 on a
  disabled project — keep that dependency explicit).

**Scope claim correction:** user-scope extensibility is "data-only" for the SELECT path ONLY. Adding user scope
later still reshapes the authz/write surface (non-admin creates own actions; `UserId == caller` guard) and the
merge semantics (override/precedence + scope-weighted sort, not a flat union). Not a flag-flip.

## Open questions (for GLM + owner)
1. Confirm **silent-hide** on unknown project (vs a visible "project not configured" notice).
2. `EnsureAsync` rename → `ResolveAsync`, or keep name with new strict contract?
3. Tenant-wide actions home: a section on the existing Settings page, or a dedicated page?
4. Widget read endpoint shape/caching (these are small, cache with revalidation like i18n).
5. Any concern with snapshotting prompt onto the comment for tenants that later edit the wording?
