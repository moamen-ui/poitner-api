# Design + Plan: Page-context capture (console + network) for AI-facing comment JSON

**Status:** Draft for review. **Date:** 2026-08-25.

## Context

Today the AI apply-skill (`API/wwwroot/skill.md`) only gets DOM/CSS/screenshot context per comment
(`ElementCaptureDto`) — no signal about *why* something is actually broken, and no browser/OS info.
Console errors and failed network calls are exactly the missing debugging signal: a stack trace or a 500
on an API call turns "this looks off" into a verifiable root cause. Confirmed via research: zero
console/network capture exists anywhere today, frontend or backend (`web-component/src/capture.ts`,
`Domain/Entity/Comment.cs`, all `Comment*Dto`s) — same for `navigator.userAgent`.

## Goals

- Give the AI apply-skill console errors/warnings and failed/slow network requests for comments the
  reporter explicitly flags as a bug, without bloating every comment's JSON.
- Never duplicate page-level data across every comment left on the same page — console/network context
  is per-*page*, not per-*comment*.
- Stay privacy-conscious: metadata only (no headers/bodies, no query strings), opt-in per project.
- Add `navigator.userAgent` to the existing per-comment element capture (currently missing).
- Fix the widget's environment picker to not offer a switch when the environment was already fixed at
  install time.

## Non-goals (v1)

- `XMLHttpRequest` interception (fetch-only for v1; XHR is a fast-follow, same schema).
- Perfectly atomic snapshot dedup (a unique constraint + retry is deferred; a rare race creating two
  snapshot rows for the same page/session is an accepted MVP limitation).
- Retention/purge job for `PageContextSnapshot` rows (volume is already bounded by the opt-in checkbox;
  revisit only if it becomes an observed problem).
- Denormalizing page context into `CommentExportDto` (export flow untouched for now).

## Design decisions

1. **Console scope**: `console.error` + `console.warn` only — no `log`/`info`.
2. **Network scope**: only failed (4xx/5xx/network-error) or notably slow (≥3000ms) requests — method,
   URL (query string stripped), status, duration. Never headers or bodies.
3. **Two-layer opt-in**:
   - A **project-level toggle** (`Project.PageContextCaptureEnabled`, default off) controls whether the
     widget buffers anything in the browser at all.
   - A **per-comment "Report as a bug" checkbox** (same UI pattern as the existing "Attach screenshot"
     checkbox, only rendered when the project toggle is on) controls whether *this* comment's buffered
     data actually gets attached and sent.
   - Buffering itself must run continuously from page load — you can't retroactively capture an error
     that already happened before the visitor opens the popover. The checkbox gates *transmission*, not
     *collection*.
   - Side effect: since most comments are cosmetic and won't check the box, far fewer
     `PageContextSnapshot` rows get created in the first place.
4. **Dedup**: console/network data lives in its own `PageContextSnapshot` record keyed by
   `(ProjectId, Route, Environment, SessionId)` — flagged comments on the same page/visit reference it by
   ID instead of embedding a copy each.
5. **Toggle location**: on `Project`, not the tenant-wide `Settings`/`AppSetting` store. Verified
   `Settings` (`/api/admin/settings`) is instance-wide, super-admin-only platform config with no tenant
   scoping column at all. The widget installs per project, and `PredefinedAction`'s per-project scoping
   is the existing precedent for project-level widget config.

## Data model

New value objects (`Domain/ValueObjects/`):
```
ConsoleLogEntry    { string Level; string Message (≤2000); string? Stack (≤4000); int Count = 1; DateTime OccurredAt; }
NetworkFailureEntry{ string Method; string Url (query-stripped, ≤2000); int? StatusCode; int DurationMs; DateTime OccurredAt; }
```

`ElementCapture` (existing value object) — add `string? UserAgent`.

New entity `PageContextSnapshot : BaseEntity` (`Domain/Entity/PageContextSnapshot.cs`), **only created
when a comment is submitted with `IsBugReport = true`**:
```
PageContextSnapshot {
  int ProjectId; Project Project;
  EnvironmentTag Environment;
  string Route;         // path only, no query/hash — /checkout?step=1 and ?step=2 share one snapshot
  string SessionId;      // client-generated, one per browser tab
  Guid? OwnerId;         // tenant isolation, mirrors Comment.OwnerId
  DateTime LastEventAt;
  List<ConsoleLogEntry> ConsoleEntries;
  List<NetworkFailureEntry> NetworkEntries;
  ICollection<Comment> Comments;
}
```

`Comment` (existing) — add:
```
int? PageContextSnapshotId; PageContextSnapshot? PageContextSnapshot;
bool IsBugReport = false;   // stamped regardless of whether context ended up non-empty — cheap triage signal
```

`Project` (existing) — add `bool PageContextCaptureEnabled = false;`.

**Mappings** (`Infrastructure/Mappings/`):
- `PageContextSnapshotMapping.cs` (new) — `ToTable("page_context_snapshots")`, `OwnsMany(...).ToJson(...)`
  for both entry lists with `HasMaxLength` bounds (untrusted page-derived data, same discipline as
  `ElementCapture`), cascade-delete on `Project`, index on `OwnerId`, composite index on
  `(ProjectId, Route, Environment, SessionId)` for the dedup lookup.
- `CommentMapping.cs` — new FK column (`OnDelete(DeleteBehavior.SetNull)` — pruning a snapshot must never
  cascade-delete comments), `IsBugReport` column, `ElementCapture.UserAgent` column.
- `ProjectMapping.cs` — new bool column, `HasDefaultValue(false)`.

**`AppDbContext`** — new `DbSet<PageContextSnapshot>` + the same tenant query filter already applied to
`Comment` (this is tenant data; must not leak across tenants through any future admin listing).

**Dedup/merge (`CommentService.CreateAsync`)**: only when `IsBugReport && Project.PageContextCaptureEnabled`
— look up an existing snapshot by `(ProjectId, Route, Environment, SessionId)`; merge/append entries and
bump `LastEventAt` if found, else create one. A rare simultaneous-create race is accepted for v1.

**Server-side enforcement**: `CommentService.CreateAsync` ignores any incoming `PageContext` payload
unless *both* `IsBugReport` and `Project.PageContextCaptureEnabled` are true, regardless of client intent
— the widget hiding the checkbox is UX, not the security boundary.

## API

New DTOs (`Application/DTOs/Comment/`):
```
PageContextDto          { int Id; string Route; EnvironmentTag Environment; DateTime LastEventAt;
                           List<ConsoleEntryDto> ConsoleEntries; List<NetworkEntryDto> NetworkEntries; }
ConsoleEntryDto          { string Level; string Message; string? Stack; int Count; DateTime OccurredAt; }
NetworkEntryDto          { string Method; string Url; int? StatusCode; int DurationMs; DateTime OccurredAt; }

PageContextCaptureDto    { string SessionId; List<ConsoleEntryInputDto> ConsoleEntries;
                           List<NetworkEntryInputDto> NetworkEntries; }   // ingestion side
ConsoleEntryInputDto     { string Level = "error"; string Message; string? Stack; int Count = 1; DateTime? OccurredAt; }
NetworkEntryInputDto     { string Method; string Url; int? StatusCode; int DurationMs; DateTime? OccurredAt; }
```

- `CreateCommentRequest` — add `bool IsBugReport`, `PageContextCaptureDto? PageContext` (sibling of
  `Element`, not nested in it).
- `ElementCaptureDto` — add `string? UserAgent`.
- `CommentListItemDto` / `CommentApplyItemDto` (paged) — add `bool IsBugReport`, `int? PageContextId`
  (**reference only** — the dedup mechanism).
- `CommentResponse` (single-item) — add `bool IsBugReport`, embed the full `PageContextDto? PageContext`
  inline (no dedup concern for one item; keeps every response self-contained, matching `skill.md`'s
  existing philosophy).
- `PagedData<T>` — add one more optional trailing param, following the existing `HiddenPrivateCount`
  precedent: `IReadOnlyDictionary<int, PageContextDto>? PageContexts` (null except on the
  comment-list/apply-queue endpoints). `CommentService.ListAsync`/`ListApplyQueueAsync` collect distinct
  non-null `PageContextSnapshotId`s from the page of results, batch-load, and populate this dict once —
  N flagged comments sharing a page context cost one dictionary entry, not N copies.
- New endpoint, mirroring `PredefinedActionsController.cs`'s pattern:
  `[Authorize] GET /api/projects/{key}/capture-config` → `{ pageContextCaptureEnabled }`.
- `Project` DTOs (`ProjectResponse`/`CreateProjectRequest`/`UpdateProjectRequest`) — add
  `PageContextCaptureEnabled` (bool on response/create, `bool?` nullable-means-untouched on update,
  matching the existing `IsActive` treatment).
- `[ProducesResponseType(typeof(Inner), 200)]` on every touched action (Swagger/Orval convention), then
  regenerate clients after deploy.

### Example response (list endpoint)

```json
{
  "items": [
    { "id": 12, "isBugReport": true, "pageContextId": 5, "element": { "route": "/checkout?step=1" } },
    { "id": 13, "isBugReport": true, "pageContextId": 5, "element": { "route": "/checkout?step=2" } }
  ],
  "pagination": { "pageNumber": 1, "pageSize": 20, "totalItems": 2, "totalPages": 1 },
  "pageContexts": {
    "5": {
      "id": 5, "route": "/checkout", "environment": 2, "lastEventAt": "2026-08-25T10:03:11Z",
      "consoleEntries": [{ "level": "error", "message": "TypeError: cannot read 'total' of undefined", "count": 3, "occurredAt": "2026-08-25T10:02:58Z" }],
      "networkEntries": [{ "method": "POST", "url": "https://api.example.com/checkout/quote", "statusCode": 500, "durationMs": 812, "occurredAt": "2026-08-25T10:02:59Z" }]
    }
  }
}
```

## Widget (`pointer.js`)

New file `web-component/src/pagecontext.ts`:
- Buffering starts at `connectedCallback()`/`_boot()` in `element.ts`, but only if the new
  `capture-config` endpoint reports `pageContextCaptureEnabled: true`.
- Console: override `console.error`/`console.warn` (restore originals in `disconnectedCallback()`);
  filter out the widget's own `[pointer-feedback]`-prefixed logs to avoid self-pollution; stringify args
  defensively; collapse consecutive identical `(level, message)` into one entry with an incrementing
  `Count`.
- Network: patch `window.fetch` (XHR deferred to a fast-follow); record only `!response.ok`, a
  rejected/errored fetch, or elapsed ≥3000ms; never record calls to the widget's own `server` origin or
  static assets; strip query strings before recording.
- Session id: one UUID per browser tab in `sessionStorage`, created lazily.
- Ring buffers capped (~20 entries each), age-trimmed (~30 min).
- "Report as a bug" checkbox in the comment popover (`templates.ts`, next to "Attach screenshot"), only
  rendered when the project toggle is on.
- `navigator.userAgent` added to the `element` payload unconditionally (not gated by the checkbox — as
  low-risk as the viewport size already sent today).
- `createComment()` in `element.ts` sets `isBugReport` from the checkbox; only when checked does it
  attach the buffer snapshot + session id as a `pageContext` field, sibling to `element`.

### Environment dropdown fix (bundled into this same widget release)

`templates.ts`'s `TPL.chrome(...)` unconditionally renders a `<select id="pf-env">` even when the host
page already fixed the environment via the widget's `environment` attribute or an injected config value.
Fix: capture a `hasFixedEnvironment` boolean in `element.ts` before the existing default-to-staging
fallback overwrites the "was it explicitly set" signal; `TPL.chrome` renders a plain read-only label
instead of the `<select>` when true.

## Dashboard (×3: Angular / React / Vue — separate `pointer-dashboard` repo)

Out of scope for this repo's changes. Each dashboard's project settings form needs a checkbox bound to
`pageContextCaptureEnabled`, added after this repo's client packages are republished.

## Migration & rollout

1. Backend schema — new entities/value objects, `Comment`/`Project` fields, mappings, `AppDbContext`,
   one migration `AddPageContextCapture`.
2. DTOs + service logic — new DTO family, `PagedData<T>.PageContexts`, `CommentService` dedup/merge +
   enable-flag enforcement, `Project` DTO/service changes, new `capture-config` endpoint, Swagger/Orval
   regeneration.
3. Widget capture — `pagecontext.ts`, wiring in `element.ts`/`templates.ts`/`types.ts`, environment
   dropdown fix, rebuild `API/wwwroot/pointer.js`.
4. `skill.md` — security section + fetch/apply step updates.
5. Dashboard toggle UI (external repo, deferred until client packages are republished).

## Verification

1. `just migrate name="AddPageContextCapture"` — inspect the generated migration.
2. `just test` — existing suite plus new unit tests for the dedup-merge path and the
   `IsBugReport`/`PageContextCaptureEnabled` gating (payload ignored unless both are true).
3. `cd web-component && npm run typecheck && npm run build` — manually verify in a browser that a
   flagged comment's POST body includes `pageContext` with query-stripped URLs and no headers/bodies, an
   unflagged comment omits it entirely, `element.userAgent` is present, and the environment dropdown is
   replaced by a label when the widget has a fixed `environment` attribute.
4. Exercise `GET /api/projects/{key}/comments` with two flagged comments on the same route/session and
   confirm one shared `pageContexts` entry, not duplicated per item.
5. Confirm Swagger regenerates cleanly.

## Open questions

- Should `PageContextSnapshot` rows ever be purged/retained on a timer, or is "no purge, revisit if it
  becomes a problem" acceptable indefinitely?
- Should XHR interception ship in the same pass as fetch, or genuinely wait for a fast-follow?
