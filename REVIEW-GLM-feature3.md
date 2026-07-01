# Design Review — Dashboard-managed projects + predefined comment actions

**Reviewer:** GLM · **Spec:** `docs/superpowers/specs/2026-07-01-projects-and-predefined-actions-design.md`
**Verdict:** Approve with **required changes**. The storage model and snapshot strategy are sound, but there are two correctness bugs vs. the stated behavior, one mandatory isolation gap, and several under-specified contracts. Details below, cited to `file:line`.

---

## 1. Tenant isolation — one mandatory gap + an ambiguity the spec ignores

### 1a. [BLOCKER] The new entity is not registered with an EF query filter
Every tenant-owned entity in this codebase is registered with a global query filter in `AppDbContext.OnModelCreating`:

- Strict-own: `Project`, `User`, `Comment`, `Reply` — `AppDbContext.cs:26-29`
- Own-plus-global: `Role`, `StatusPresentation` — `AppDbContext.cs:32-33`

`PredefinedAction` is described as **strict-own** (`OwnerId` "ALWAYS set", spec L51), so it must get:

```csharp
b.Entity<PredefinedAction>().HasQueryFilter(e => currentUser.IsSuperAdmin || e.OwnerId == currentUser.TenantId);
```

The spec never mentions this. Without it, the admin CRUD service (`List`/`Update`/reconcile) will **cross-read every tenant's actions** — a direct isolation breach, and inconsistent with `Project`/`Comment`. This is the single most important omission in the document. The new mapping (`PredefinedActionMapping`) and the `DbSet<PredefinedAction>` addition are implied but must be called out alongside the filter.

### 1b. [RISK] Anonymous widget-read is ambiguous because keys are owner-scoped
Keys are unique on `(key, owner_id)` only — `ProjectMapping.cs:24`, `AddTenancy.cs:122-126`. **Two tenants can both own a project keyed `my-app`.** The existing anonymous, key-only resolver already exhibits this bug:

- `RoleService.ListPublicAsync` does `IgnoreQueryFilters().FirstOrDefaultAsync(p => p.Key == keyNormalized)` — `RoleService.cs:75-79`. On a key collision it silently picks one tenant's rows (lowest `Id`) and returns another tenant's data to the widget.
- `AuthService` register resolves the same way — `AuthService.cs:149-154`.

The spec's widget-read endpoint (`GET /api/projects/{key}/predefined-actions`, spec L86-87) "resolves owner from the project key" — i.e. it inherits this exact ambiguity and would serve **tenant B's action labels to a widget whose key collides with tenant A's**.

**Recommendation: make the read endpoint `[Authorize]`, not anonymous.** The picker only renders inside the comment popover, which is already a post-login flow (`pointer.js:747` calls `init()` only `if (this.token)`). An authorized endpoint resolves the caller's tenant from the JWT, and the (now-strict) `EnsureAsync` returns the `projectId` within that tenant — no key collision, no `IgnoreQueryFilters`, and the effective-set query is naturally scoped. This also removes the need to replicate the `RoleService` anonymous dance for a third endpoint. If anonymous is truly required, the ambiguity must be documented and accepted explicitly (it is a pre-existing flaw, not introduced here — but the design is adding another instance of it).

### 1c. [OK, with note] Strict-own filter choice is correct
Because tenant-wide actions carry `OwnerId = T` (set, not null), they are strict-own rows — unlike `Role` where `OwnerId == null` means a global default. So the filter must be the `Project`/`Comment` form (1a), **not** the own-plus-global `Role` form. The spec's "OwnerId ALWAYS set" (L51) is consistent with this; just make sure no code path ever stamps `OwnerId = null` on an action (e.g. a super-admin creating a "global" action), or the strict-own filter will hide it from everyone except super-admin.

---

## 2. Prompt-leak — the dedicated endpoint is fine; the real vector is the comment DTOs

The widget read endpoint returning `[{id, text}]` (spec L87) is correct by construction. **The actual leak surface is the comment response path the widget already fetches on every load.**

- `CommentListItemDto` (`CommentListItemDto.cs:1-25`) and `CommentResponse` (`CommentResponse.cs:1-21`) are returned by `GET /api/projects/{key}/comments` and `POST .../comments`, both consumed by the widget (`pointer.js:860`, `:1290`). `CommentListItemDto.cs:20-22` explicitly documents that this DTO is "self-contained" so the AI fetch queue reads it without a second lookup — i.e. it is intentionally fat.
- The spec adds `PickedActionPrompt` to the `Comment` entity (spec L72) but **never states that the comment DTOs must exclude it.** The moment someone maps the new columns into `MapToResponse`/`MapToListItem` (`CommentService.cs:405-421`, `:387-403`), the prompt ships to every browser that lists comments.

**Required:** the spec must state, as an invariant, that `PickedActionPrompt` is **never serialized in any comment DTO** (only `PickedActionText`, the visible label, may be — and even that only if the widget needs it; it likely doesn't). Enforce it with an explicit projection in the mappers and, ideally, a unit test that asserts the serialized JSON contains no `prompt`/`PickedActionPrompt` key. Defense-in-depth: keep `Prompt` off the DTO classes entirely so a careless mapper won't compile.

**Secondary leak path — the apply export:** the spec says the snapshot feeds `pending.json` (spec L92-94). That artifact is consumed by the apply-time LLM (CLI/agent), not the browser, so it is safe — **provided** the endpoint/job that assembles `pending.json` stays admin-gated. I could find no anonymous controller exposing `Element`/snapshot today; the spec should explicitly confirm the export path remains admin-only so a future "public pending queue" endpoint doesn't regress this.

---

## 3. User-scope extensibility — storage-adequate, but "no reshape" is overstated

The nullable discrimination `(OwnerId always set; ProjectId?; UserId?)` is a clean storage model, and the effective-set query (spec L67)

```
OwnerId = T AND (ProjectId IS NULL OR ProjectId = P) AND (UserId IS NULL OR UserId = U)
```

does already carry the user dimension, so **at the query layer** user scope is additive. But the spec's claim that adding user scope is "data-only (no reshape)" (L68) is only narrowly true. It omits:

1. **Authorization surface.** Today actions are admin-managed. User scope means a *non-admin* can create/update/delete their own actions — new authz rules (`UserId == caller` on writes), new endpoints, and a check that a user cannot set `UserId` to someone else. That is a write-surface and authz reshape, not data-only.
2. **Merge semantics, not plain union.** The spec's own Non-goal (L17) already defers "per-project override/hide of tenant-wide actions." User scope will almost certainly want **override/precedence** (a user hides a tenant action, or substitutes their own prompt), not a flat union. Flat union of three scopes yields duplicates and ordering ambiguity. So the *product* reshapes even if the table doesn't.
3. **`SortOrder` is a single int with no scope weighting.** Merging tenant + project + user rows into one coherent picker order needs a deterministic tie-break (scope-weighted sort). Today that's trivial (one scope). With three scopes it becomes a behavioral spec the model doesn't encode.
4. **No dedup/uniqueness key.** Nothing prevents a user from creating an action whose `Text` duplicates a tenant action. Fine for v1, but "no reshape" assumes you won't want dedup later.

**Verdict on #3:** the *columns* extend cleanly; call the claim "data-only" accurate only for the SELECT path, and explicitly scope it. Do not let the phrase be read as "user scope is a flag-flip."

---

## 4. `EnsureAsync` strict change — service layer is robust; the widget is NOT

### 4a. [OK] Service-layer blast radius is contained
Only two consumers exist, both in `CommentService`: `CreateAsync` (`CommentService.cs:35`) and `ListAsync` (`CommentService.cs:96`). Both already branch on `IsConflict` vs. else→`NotFound` (`:37-39`, `:97-100`), so they correctly propagate the new `NotFound` from a missing project. No other service calls `EnsureAsync` (grep-confirmed). The strict contract is a safe drop-in at the service tier.

### 4b. [BLOCKER vs. stated behavior] The widget does NOT silent-hide on 404
The spec asserts (L34-35): "treat like disabled — hide silently … Consistent with what we already do." **This is false as currently coded.** The widget silent-hides **only on HTTP 409**:

- `fetchComments`: `if (r.status === 409) { this.disableSilently(); return; }` — `pointer.js:861-863`. Any other non-OK status (including the new **404** from the strict resolver) hits `if (!r.ok) throw new Error("HTTP " + r.status)` (`:865`) → catch block → `this.toast("Could not reach Pointer server", "error")` (`:875`). That is a user-visible error toast, not a silent hide.
- `saveComment`: same pattern — 409 → `disableSilently()` (`pointer.js:1294-1297`); else → throw (`:1298`) → `this.toast("Failed to save comment", "error")` (`:1309`).
- `disableSilently` itself is documented as 409-only — `pointer.js:750-753`.

**Impact:** today an unknown key returns **200** (auto-created, `ProjectService.cs:96-110`). After the change it returns **404**, which the widget surfaces as a visible "Could not reach Pointer server" / "Failed to save comment" toast — the opposite of "never a console error for the consumer's users." Existing widgets deployed against a typo'd or not-yet-created key will visibly regress.

**Required fix (spec must specify it):** extend the widget's project-resolve handling to treat **404 exactly like 409** — i.e. `if (r.status === 409 || r.status === 404) this.disableSilently()` in both `fetchComments` (`pointer.js:861`) and `saveComment` (`pointer.js:1294`). Do **not** make the strict resolver return 409 for missing projects — that conflates "disabled" with "not configured" and breaks admin telemetry. The widget is the correct place to normalize the two into one "hide" behavior.

### 4c. [RISK] Other project-key resolvers are not on the strict contract — goal only half-met
`EnsureAsync` is not the only key resolver. The "stop key spam / require dashboard-defined project" goal (spec L7-9, L40-42) is undermined because:

- `UploadsController.Upload` resolves the project independently from the `project` form field (`UploadsController.cs:53`, `[Authorize]`) — its behavior on an unknown key is unspecified here and is not touched by the `EnsureAsync` change.
- `AuthService` registration resolves by key anonymously with `IgnoreQueryFilters` and **no owner scoping** (`AuthService.cs:149-154`) — on a colliding/unknown key it behaves arbitrarily.

The spec should **enumerate every key-resolution path** (`EnsureAsync`, uploads, register, demo) and state for each whether it goes strict-and-owner-scoped or is deliberately left as-is. Otherwise "projects must be defined first" is true only for comment posting, while uploads/registration still tolerate undefined or cross-tenant keys — an inconsistent contract that will confuse integrators and weaken the isolation story.

### 4d. [NIT] Rename vs. keep
Renaming `EnsureAsync` → `ResolveAsync` is clearer, but if kept, the **interface doc comment must be rewritten** — `IProjectService.cs:12-17` currently documents the lazy-create behavior and will actively mislead. Also note the dead `projectKey` field the widget POSTs in the JSON body (`pointer.js:1292`) alongside the path key (`:1290`); the controller binds from the path and ignores the body field — the new `predefinedActionId` should be a clean body field, not inherit this duplication.

---

## 5. Migration safety — mostly safe; specify columns, indexes, and the query-filter dependency

### 5a. [OK] Additive shape is safe
- New `predefined_actions` table: pure additive, no data movement. Fine.
- Two nullable `Comment` columns (`PickedActionText`, `PickedActionPrompt`): `ALTER TABLE comments ADD COLUMN … NULL` is **metadata-only/instant in Postgres** (no table rewrite), safe even on a large `comments` table. Correct that the spec makes them nullable (spec L72). Do **not** let these become `NOT NULL DEFAULT` later without a backfill plan.
- The snapshot lives in **scalar columns**, not inside the `element` JSON blob (`CommentMapping.cs:36` maps `Element` via `OwnsOne(… ToJson)`). Keeping the prompt out of the JSON is good — it stays independently queryable and doesn't bloat the element object the widget re-serializes.

### 5b. [REQUIRED] Column types and lengths are unspecified
`Prompt` can be long (multi-paragraph LLM instructions). The spec gives no type. Specify `text` (Postgres) / no `HasMaxLength`, or a generous cap — otherwise a default `nvarchar(n)` could silently truncate prompts. `Text` should similarly be bounded (e.g. ≤256) for picker UX.

### 5c. [REQUIRED] Indexes for the effective-set query
The hot query is the widget read filtered by owner + scope. Without an index it scans. Recommend, mirroring the tenant index pattern (`ProjectMapping.cs:28`, `CommentMapping.cs:35`):
- `HasIndex(x => x.OwnerId)` (tenant list),
- `HasIndex(x => new { x.OwnerId, x.ProjectId })` (effective-set lookup; Postgres btree indexes NULLs, so tenant-wide `ProjectId IS NULL` rows are served too).
No uniqueness constraint is required (actions are identified by `Id`; `SortOrder` is per-scope and non-unique).

### 5d. [REQUIRED] Soft-delete discipline
`BaseEntity` provides `DeletedAt` (`BaseEntity.cs:10`) but the global query filter does **not** filter on it — every service hand-writes `DeletedAt == null` (e.g. `ProjectService.cs:32,57,67,92`). Every `PredefinedAction` query must do the same, or soft-deleted actions will reappear in the picker and in reconcile. State this explicitly; it's an easy regression.

### 5e. [RISK] Nested-action reconcile concurrency
Spec L82 says project update "reconciles add/update/soft-delete" for nested `predefinedActions`. The DTO carries `id?` (nullable → new), which implies id-based reconcile, but the semantics aren't defined. Two admins editing the same project form simultaneously will race; last-write-wins can resurrect a soft-deleted action another admin just removed, or duplicate. Define: (1) reconcile rule (`id` present→update, absent→add, absent from payload→soft-delete); (2) an optimistic-concurrency token on `Project` (e.g. a row version) or at minimum document that this is last-write-wins. The snapshot strategy (spec L76) correctly insulates historical comments from these edits — good — but the live definition still needs the guard.

---

## 6. Other missing / risky items

- **Client regeneration is not in the rollout.** `AGENTS.md` mandates `npm run generate-clients` after any endpoint/DTO change, and the controller `[ProducesResponseType(typeof(InnerType))]` + `[Produces("application/json")]` convention (AGENTS.md convention #1) so Orval stays clean. The spec's deploy steps (L111) omit client regen; the new controllers/DTOs must follow the convention or the Angular/React/Vue packages will mis-type the envelope. Add `npm run generate-clients` to the rollout and state the convention adherence for the new endpoints.
- **No tests specified.** `TenantQueryFilterTests.cs` exists for the tenant model; add (1) a query-filter test for `PredefinedAction` (tenant A cannot see tenant B's actions), (2) a test that the widget-read DTO contains no `prompt` field, (3) a test that an out-of-scope/inactive `predefinedActionId` on comment create is rejected.
- **Message keys.** The codebase keys all user strings via `MessageKeys.*` (e.g. `MessageKeys.Project.NotFound`, `ProjectService.cs:36,68,113`). New flows (invalid `predefinedActionId`, action text required) need keys; not mentioned.
- **Stale widget cache.** Open question 4 (L117) raises caching the widget read with revalidation "like i18n." Fine, but define the staleness window against the reconcile-soft-delete case: a stakeholder whose cached effective set still references a just-deleted action id will get a 4xx on submit. Either (a) the widget retries by refetching the effective set once on rejection, or (b) accept the one-off error. Pick one and state it.
- **`IsActive` on action vs. project.** The effective-set query must also honor project-active state. If the read endpoint funnels through the strict `EnsureAsync`, a disabled project already yields 409 — good — but make that dependency explicit so a future "fast path" that skips the resolve doesn't serve actions for a disabled project.
- **`SortOrder` defaulting.** Unspecified how a new action gets its `SortOrder` (max+1? 0?). Minor, but define it or the picker order will be non-deterministic for concurrently-added rows.

---

## Summary table

| # | Topic | Severity | One-line |
|---|---|---|---|
| 1a | No query filter on `PredefinedAction` | **Blocker** | Must add strict-own `HasQueryFilter` or admin CRUD leaks cross-tenant. |
| 1b | Anonymous read is key-ambiguous | Risk | Prefer `[Authorize]` read endpoint; key-only resolve collides across tenants. |
| 2 | Prompt leak via comment DTOs | **Blocker** | Forbid `PickedActionPrompt` in `CommentResponse`/`CommentListItemDto`; test it. |
| 3 | "No reshape" for user scope | Nit | True for SELECT only; authz/merge/sort reshape later. |
| 4b | Widget doesn't silent-hide on 404 | **Blocker** | `pointer.js:861,1294` only hide on 409; must also hide on 404. |
| 4c | Other key resolvers not strict | Risk | Uploads/register still tolerate unknown/colliding keys; enumerate all paths. |
| 5b/5c/5d | Migration: types, indexes, soft-delete | Required | Specify `text` prompt, `(owner,project)` index, `DeletedAt==null` everywhere. |
| 5e | Nested-action reconcile concurrency | Risk | Define id-based reconcile + optimistic concurrency on `Project`. |
| 6 | Client regen / tests / message keys | Required | Add `generate-clients`, filter test, prompt-leak test, `MessageKeys`. |
