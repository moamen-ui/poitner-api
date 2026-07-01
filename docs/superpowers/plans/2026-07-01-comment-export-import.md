# Comment Export / Import — Spec + Implementation Plan

> **STATUS: ⬜ NOT STARTED.**
> Branch: `feat/comment-export`.
> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use
> checkbox (`- [ ]`). Implementer tags: **[Claude]** = implement carefully (auth/tenant-boundary
> concerns); **[GLM]** = mechanical/low-risk (DTOs, schema wiring).

---

## 1. Scope Clarification — What This Feature Is NOT

A separate feature ("upgrade demo→registered user") will perform an in-place tenant upgrade where
all demo data (comments, projects, replies) carries over automatically with the **same OwnerId**
— no data movement, no re-ID, no file copy. That path does **not** need export/import.

This feature is **general-purpose export/import** for two use-cases:

1. **Backup / archival** — a workspace owner downloads a portable, human-readable snapshot of
   their comments (and replies) at any point in time.
2. **Cross-workspace migration** — moving a project's comments to a different Pointer workspace
   (different tenant, different server, or after a workspace reset).

---

## 2. Verified Domain Facts (grounded in the code)

| Fact | Source |
|---|---|
| `Comment` carries: `ProjectId`, `Environment` (enum), `Status` (enum), `AuthorId` (Guid), `Body`, `IsPrivate`, `AppliedAt/By/Label`, `EditedAt/By`, `OwnerId` (tenant), `Element` (owned JSON), `Replies` (collection) | `Domain/Entity/Comment.cs` |
| `Reply` carries: `CommentId`, `AuthorId`, `Body`, `OwnerId` | `Domain/Entity/Reply.cs` |
| `ElementCapture` carries: `Selector`, `Snapshot`, `Classes`, `ComputedStyles`, `AppliedCssRules`, `SourcePath`, `ParentInfo`, `ScreenshotUrl`, `PageUrl`, `Route`, `PageTitle`, `ViewportWidth`, `ViewportHeight`, `DeviceType`, `DevicePixelRatio` | `Domain/ValueObjects/ElementCapture.cs` |
| `ScreenshotUrl` is stored as a raw relative path (`uploads/<ownerSeg>/<project>/<file>`); the service re-signs it on every read via `IUploadSigner.SignedUrl()`. Files live at `wwwroot/uploads/<ownerSeg>/<project>/`. | `Application/Services/Implementation/CommentService.cs` (MapElementToDto), `Infrastructure/Storage/LocalFileStorage.cs` |
| `BaseEntity` has auto-stamped `CreatedAt`, `CreatedBy`, `UpdatedAt`, `UpdatedBy`, `DeletedAt`, `DeletedBy` (int `Id` PK). | `Domain/Entity/BaseEntity.cs` |
| Tenant isolation: every Comment/Reply query is scoped to `OwnerId == currentUser.TenantId` by an EF **global query filter**. Super-admin bypasses with `.IgnoreQueryFilters()`. | `Infrastructure/AppDbContext.cs` |
| `OwnerId` on a Comment is the **project's** OwnerId, not the caller's. Replies inherit the parent comment's OwnerId. Writes never trust `OwnerId` from request body — it is always server-stamped. | `Application/Services/Implementation/CommentService.cs` CreateAsync, AddReplyAsync |
| Project key is unique per tenant (composite index `(key, owner_id)`). | `Infrastructure/Migrations/20260629131255_AddTenantRoleNameIndex.cs` (pattern), `Infrastructure/Mappings/ProjectMapping.cs` |
| Auth is JWT bearer. Policies: `Admin` = `is_admin == "true"`, `SuperAdmin` = `is_super_admin == "true"`. All comment endpoints require `[Authorize]`. | `API/Auth/Policies.cs`, `API/Controllers/CommentsController.cs` |

---

## 3. Export Feature Spec

### 3.1 Endpoint Design

```
GET  /api/projects/{key}/export          # per-project export (recommended)
GET  /api/export                         # whole-workspace export
```

Both require `[Authorize]` (any authenticated user in the tenant). No Admin policy needed for
read-only export — a stakeholder should be able to export their own workspace's comments for
their own backup. An admin guard can be added later if tenant policy demands it.

Query parameters on both endpoints:

| Param | Type | Default | Meaning |
|---|---|---|---|
| `includePrivate` | bool | `false` | Include private comments authored by others (requires admin role; enforced server-side; non-admins always get `false` regardless of the param). |
| `includeDeleted` | bool | `false` | Include soft-deleted comments (requires admin). |
| `status` | `CommentStatus?` | all | Filter to a single status. |
| `environment` | `EnvironmentTag?` | all | Filter to a single environment. |

Response: `Content-Type: application/json`, `Content-Disposition: attachment; filename="pointer-export-<project|workspace>-<utcdate>.json"`.
HTTP 200 with the JSON body streamed; no pagination (export = full dump). Large workspaces are
warned about in docs; a future size cap can be enforced as a 400 if `totalComments > N`.

### 3.2 Export JSON Schema (versioned)

```jsonc
{
  "schema_version": "1.0",           // bumped on breaking changes
  "exported_at": "2026-07-01T00:00:00Z",
  "source_project": "my-project",    // null for workspace exports
  "source_server": "https://api.pointer.moamen.work",  // informational
  "comments": [
    {
      // --- identity (remapped on import) ---
      "export_id": "c-1",            // stable key within THIS export file; NOT the DB id
      "project_key": "my-project",   // used on import to route to the right project
      // --- content (preserved verbatim) ---
      "body": "…",
      "environment": "Staging",      // enum name (string), not int
      "status": "Open",              // enum name (string), not int
      "is_private": false,
      // --- timing (preserved verbatim) ---
      "created_at": "2026-06-01T10:00:00Z",
      "applied_at": null,
      "applied_by_label": null,
      "edited_at": null,
      // --- author (informational; NOT a Guid that resolves in the target workspace) ---
      "author_display_name": "Alice",  // resolved at export time; see §3.3
      "applied_by_display_name": null,
      "edited_by_display_name": null,
      // --- element capture (preserved verbatim, screenshots nulled out) ---
      "element": {
        "selector": "…",
        "snapshot": "…",
        "classes": "…",
        "computed_styles": "…",
        "applied_css_rules": "…",
        "source_path": "…",
        "parent_info": "…",
        "screenshot_url": null,          // ALWAYS null in export (see §3.3)
        "screenshot_omitted": true,      // flag so importers know a screenshot existed
        "page_url": "…",
        "route": "…",
        "page_title": "…",
        "viewport_width": 1440,
        "viewport_height": 900,
        "device_type": "desktop",
        "device_pixel_ratio": 2.0
      },
      // --- replies ---
      "replies": [
        {
          "export_id": "r-1",
          "body": "…",
          "author_display_name": "Bob",
          "created_at": "2026-06-01T11:00:00Z"
        }
      ]
    }
  ]
}
```

Key schema decisions:

- **Enum names, not ints** — forward-compatible if enum values are reordered.
- **`export_id`** — a synthetic key (`"c-<n>"`, `"r-<n>"`) local to the file; used for
  dedup on idempotent re-import (see §4.5). Never the DB `id`.
- **`screenshot_url` is always `null`** — see §3.3.
- **`screenshot_omitted` flag** — tells the importer a screenshot existed but was dropped.
- **Author fields are display names, not Guids** — Guids are meaningless in another workspace;
  display names are preserved as informational text.
- **`schema_version`** — the importer checks this; future minor changes bump the patch version
  and are ignored gracefully; breaking changes bump the major version and the importer rejects
  with a clear error message.

### 3.3 Screenshot Handling (Export Side)

Screenshots are stored at `wwwroot/uploads/<ownerSeg>/<project>/<file>` on the **source server**.
They are owner-scoped and inaccessible from another server or workspace.

**Decision: screenshots are NOT transferred in the export file.** The export always sets
`element.screenshot_url = null` and sets `element.screenshot_omitted = true` when a screenshot
existed. Rationale:

1. Files can be arbitrarily large (screenshots) and embedding base64 in JSON is impractical for
   bulk exports.
2. A cross-server move cannot transfer files anyway.
3. A cross-workspace move on the same server could in theory copy files, but the added complexity
   (atomic file copy, new storage path, ownership change) is out of scope for v1.
4. The screenshot is supplementary context; the element selector, snapshot, applied CSS rules, and
   source path carry the actionable information the AI needs.

Future v2 scope: an optional `includeScreenshots=true` flag that embeds files as
`data:image/png;base64,…` for same-server single-comment backups.

---

## 4. Import Feature Spec

### 4.1 Endpoint Design

```
POST /api/projects/{key}/import        # import into a specific project
POST /api/import                       # bulk import; project routed by export_file.comments[].project_key
```

Both require `[Authorize]`. The Admin policy (`[Authorize(Policy = Policies.Admin)]`) is
**required for import** — importing is a write operation that bulk-creates data in the tenant,
and it must be restricted to project admins.

Request: `multipart/form-data` with field `file` (the `.json` export file).
Alternatively, accept `Content-Type: application/json` with the body being the export schema
directly (simpler for scripted/CLI use).

Response: `200 OK` with an import summary:

```jsonc
{
  "imported_comments": 42,
  "imported_replies": 17,
  "skipped_duplicates": 3,
  "warnings": [
    "3 comments had screenshots that were omitted (screenshot_omitted=true).",
    "2 comment authors could not be resolved; re-attributed to importer."
  ]
}
```

### 4.2 ID Remapping

- **Comment IDs and Reply IDs** are database auto-increment integers that have no meaning in the
  target workspace. On import, new `Comment` and `Reply` rows are inserted; they receive new DB
  ids assigned by PostgreSQL.
- **`export_id`** is stored in a transient import-session map; it is used only for
  dedup (§4.5) and is not persisted.

### 4.3 Author Remapping

Original author Guids are meaningless in the target workspace (a different tenant's users). The
import service applies the following re-attribution policy:

- All imported comments and replies have their `AuthorId` set to the **importing user's id**
  (`ICurrentUser.Id`).
- The original `author_display_name` from the export is **appended to the comment body** as a
  blockquote-style footnote:

  ```
  <original body>

  *(Imported — originally by: Alice)*
  ```

  This preserves attribution in a human-readable way without creating phantom user accounts.
- The `AppliedByLabel` field (if present) is preserved verbatim as a string (it is already a
  display label, not a Guid).
- `EditedAt` and `EditedBy` are **not carried over** — the import itself is not an edit; the
  field is left null on the imported row.

### 4.4 OwnerId Stamping

The import service **never trusts OwnerId from the JSON**. All imported rows are stamped:

- `comment.OwnerId = project.OwnerId` (resolved via `ProjectService.EnsureAsync`, which is
  already tenant-scoped by the EF global query filter — the importer can only resolve projects
  they own).
- `reply.OwnerId = comment.OwnerId` (inherited, same pattern as the live service).

This is identical to the live `CommentService.CreateAsync` path and inherits its security
properties.

### 4.5 Idempotency / Dedup

There is no globally unique key on a comment (no external ID carried across workspaces). The
import is therefore **not inherently idempotent** for repeated uploads of the same file.

Recommended dedup strategy: the importer hashes the tuple
`(export_id, project_key, body, created_at_utc)` for each comment in the uploaded file and
checks whether a comment with that hash already exists (stored in a new nullable column
`import_dedup_hash TEXT`). If found, the comment is skipped and counted in `skipped_duplicates`.

**Open decision #1:** Whether to add the `import_dedup_hash` column (requires a migration) or
skip dedup for v1 and warn users to avoid double-importing.

**Recommended for v1:** skip dedup, document the risk, add a clear warning in the UI ("Uploading
the same file twice will create duplicate comments."). Add the dedup column in v2.

### 4.6 Validation of Untrusted Input

Import files are untrusted user input. The following checks run before any DB writes:

| Check | Response on failure |
|---|---|
| File size > 10 MB (configurable via `AppSettings`) | 400 "Export file too large." |
| JSON parse failure | 400 "Invalid JSON." |
| `schema_version` major version not supported | 400 "Unsupported export schema version X. Supported: 1.x." |
| `comments` array missing or not an array | 400 "Missing or invalid comments array." |
| Per-comment: `body` null or empty | 400 with the offending `export_id`. |
| Per-comment: `environment` not a valid enum name | 400 with the offending `export_id`. |
| Per-comment: `status` not a valid enum name | 400 with the offending `export_id`. |
| `comments` array length > 5000 (configurable) | 400 "Too many comments in a single import." |
| Nested `replies` array length > 500 per comment | 400 with the offending `export_id`. |

All validation runs in a single pass before the first DB insert (fail-fast, atomic: nothing is
written if any comment fails validation).

### 4.7 Risk Register

| Risk | Likelihood | Recommended Handling |
|---|---|---|
| **Cross-tenant data injection** — attacker crafts JSON claiming comments belong to another tenant's project | High (without mitigation) | Mitigated by OwnerId stamping (§4.4): importer can only write to projects they own, enforced by EF filter on `EnsureAsync`. |
| **Oversized payload / DoS** — huge JSON file exhausts memory | Medium | Enforce 10 MB file size limit at the controller level before deserializing (read stream length / Content-Length header). |
| **Schema injection in `body`** — XSS payloads in comment bodies | Medium | No change from live comment creation; bodies are stored raw and sanitized at render time in the dashboard. No extra risk. |
| **Phantom author proliferation** — importing creates comments attributed to a non-existent user | Low (mitigated) | Author remapping (§4.3) re-attributes to the importer. No user lookup needed. |
| **Duplicate import** — user uploads the same file twice | High | Document the risk; add dedup in v2 (§4.5). |
| **Stale `created_at` timestamps** — imported comments appear at the top/bottom of sorted lists because their `created_at` is in the past | Low | `created_at` is preserved verbatim (historical accuracy is valuable for backup/restore). Document that sort order may differ. |
| **Screenshot loss** — users expect screenshots to transfer | Medium | `screenshot_omitted` flag + warning in import summary (§4.1). Dashboard UI shows a "no screenshot" placeholder with the flag context. |
| **Invalid project key in bulk import** — `project_key` in the file refers to a project that doesn't exist in the target workspace | Medium | `EnsureAsync` lazy-creates the project (existing behaviour). If lazy creation is undesirable for import, a future strict-mode flag can change this to a 404. |
| **Comment cap hit during import (demo tenants)** | Medium | The import service calls `CommentService.CreateAsync` per comment (or an equivalent batch path) which already enforces the demo comment cap. A partial import is possible (some succeed, then cap is hit). Batch-check the cap upfront and reject early if `existingCount + importCount > cap`. |

---

## 5. JSON Schema Versioning Contract

Schema version follows `<major>.<minor>`:

- **Minor bump** (e.g., 1.0 → 1.1): additive-only change (new optional field). The importer
  ignores unknown fields (`JsonSerializer` with `PropertyNameCaseInsensitive=true` and
  `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`). No code change needed on the
  importer for a minor bump.
- **Major bump** (e.g., 1.x → 2.0): breaking change (field renamed/removed/type changed).
  The importer returns 400 if `schema_version` major digit does not match a supported major.
  A multi-version importer can be implemented later with a dispatcher.

The exporter always writes the **current** schema version. The importer stores supported major
versions as a compile-time constant:

```csharp
// Application/Services/Implementation/ExportImportService.cs
private const string CurrentSchemaVersion = "1.0";
private static readonly int[] SupportedMajorVersions = [1];
```

---

## 6. Dashboard UI Notes

### 6.1 Export Button (per-project view)

- Location: project comments page, toolbar area (near "Filter" / status controls).
- Component: a split-button or dropdown — primary action "Export all", secondary items
  "Export Open only", "Export ReadyToApply only".
- On click: trigger `GET /api/projects/{key}/export` with auth bearer token; browser downloads
  the JSON file via `Content-Disposition: attachment`.
- Workspace-level export: settings or account page → "Export workspace" → downloads the full
  workspace JSON.

### 6.2 Import Upload (per-project view)

- Location: project comments page, same toolbar area, right of export.
- Component: a file-input button labeled "Import comments". On selection, shows a confirmation
  dialog: "Import from file `pointer-export-my-project-2026-07-01.json`? This will create
  [N] new comments in project [key]. Screenshots are not transferred."
- On confirm: POST to `/api/projects/{key}/import` with `multipart/form-data`.
- On success: show an inline toast with the import summary (imported/skipped/warnings).
- On error: show error message from the API response body.

### 6.3 Screenshot Omission Indicator

- In the comment card, when `element.screenshot_omitted === true` and `element.screenshot_url`
  is null: show a subtle badge or tooltip "Screenshot not available (omitted on export)" in
  place of the screenshot thumbnail. This prevents user confusion ("where did the screenshot go?").

---

## 7. Implementation Plan

> **For agentic workers:** Use `superpowers:subagent-driven-development`. Tags:
> **[Claude]** = implement; **[GLM]** = mechanical wiring. Every task lists exact file paths.
> Do NOT deploy until the user asks. `dotnet build` + `dotnet test` after each task.

---

### Task 1 [GLM]: Export DTOs + schema model

**Files to create/modify:**
- `Application/DTOs/Export/CommentExportDto.cs` (new)
- `Application/DTOs/Export/ReplyExportDto.cs` (new)
- `Application/DTOs/Export/ElementCaptureExportDto.cs` (new)
- `Application/DTOs/Export/ExportFileDto.cs` (new — the root object with `schema_version`, `exported_at`, `source_project`, `source_server`, `comments`)
- `Application/DTOs/Export/ImportResultDto.cs` (new — `imported_comments`, `imported_replies`, `skipped_duplicates`, `warnings`)

**What to build:**
- [ ] Create `ExportFileDto` matching the schema in §3.2 exactly. Use `string` for enum names
  (not `int`), `DateTime` for timestamps, `string?` for nullable strings.
- [ ] `ElementCaptureExportDto` mirrors `ElementCaptureDto` but adds `bool ScreenshotOmitted`.
  `ScreenshotUrl` is `string?` (always null on export, but the type stays for schema symmetry).
- [ ] `ReplyExportDto`: `export_id` (string), `body`, `author_display_name`, `created_at`.
- [ ] `CommentExportDto`: all fields from §3.2 schema.
- [ ] `ImportResultDto`: `ImportedComments`, `ImportedReplies`, `SkippedDuplicates`, `Warnings`.
- [ ] `dotnet build`.

---

### Task 2 [Claude]: Export service + per-project and workspace-level export

**Files to create/modify:**
- `Application/Services/Interfaces/IExportImportService.cs` (new)
- `Application/Services/Implementation/ExportImportService.cs` (new — export half only in this task)

**What to build:**
- [ ] `IExportImportService`:
  ```csharp
  Task<Result<ExportFileDto>> ExportProjectAsync(string projectKey, ExportOptions options);
  Task<Result<ExportFileDto>> ExportWorkspaceAsync(ExportOptions options);
  ```
  where `ExportOptions { bool IncludePrivate; bool IncludeDeleted; CommentStatus? Status; EnvironmentTag? Environment; }`.

- [ ] `ExportProjectAsync`:
  1. Resolve project via `_projectService.EnsureAsync(key)` — this is tenant-scoped by EF filter,
     so the caller can only export their own project.
  2. Query `Comment` with `.Include(c => c.Replies)` filtered by `ProjectId`, `DeletedAt == null`
     (unless `IncludeDeleted`), and optionally `Status`/`Environment`.
  3. Apply private-comment filter: if `!options.IncludePrivate || !_currentUser.IsAdmin`, exclude
     `IsPrivate && AuthorId != currentUser.Id`. (Matches the live `ListAsync` logic.)
  4. Map each `Comment` → `CommentExportDto`:
     - `export_id = "c-{sequentialIndex}"`.
     - Resolve display names in a single batched user query (same pattern as `ResolveNamesAsync`).
     - `element.screenshot_url = null`; `element.screenshot_omitted = !string.IsNullOrEmpty(entity.Element.ScreenshotUrl)`.
     - Map enum to name: `comment.Status.ToString()`, `comment.Environment.ToString()`.
  5. Map each reply → `ReplyExportDto` with `export_id = "r-{sequentialIndex}"`.
  6. Return `ExportFileDto { SchemaVersion = "1.0", ExportedAt = DateTime.UtcNow, SourceProject = key, ... }`.

- [ ] `ExportWorkspaceAsync`: same as above but without the `ProjectId` filter. Queries all
  comments in the current tenant. `SourceProject = null`.

- [ ] `dotnet build`.

---

### Task 3 [GLM]: Export controller endpoints

**Files to create/modify:**
- `API/Controllers/ExportImportController.cs` (new)

**What to build:**
- [ ] Class-level `[Authorize]` (any authenticated tenant user).
- [ ] `GET /api/projects/{key}/export` → calls `ExportProjectAsync`; on success, returns
  `File(json bytes, "application/json", $"pointer-export-{key}-{date:yyyyMMdd}.json")`.
  On `IsNotFound` return 404; on `IsConflict` return 409.
- [ ] `GET /api/export` → calls `ExportWorkspaceAsync`; on success returns attachment download
  `pointer-export-workspace-{date:yyyyMMdd}.json`.
- [ ] Both accept `[FromQuery] ExportQueryParams` (maps to `ExportOptions`).
  Add `ExportQueryParams` DTO in `Application/DTOs/Export/`.
- [ ] `dotnet build`.

---

### Task 4 [Claude]: Import service — validation + ID remapping + OwnerId stamping

**Files to create/modify:**
- `Application/Services/Implementation/ExportImportService.cs` (add import half)
- `Application/Services/Interfaces/IExportImportService.cs` (add import method)

**What to build:**
- [ ] Add to `IExportImportService`:
  ```csharp
  Task<Result<ImportResultDto>> ImportProjectAsync(string projectKey, ExportFileDto file);
  Task<Result<ImportResultDto>> ImportWorkspaceAsync(ExportFileDto file);
  ```

- [ ] `ImportProjectAsync` (the core path; workspace variant iterates by `project_key` field):
  1. **Schema version check**: parse `major = int.Parse(schemaVersion.Split('.')[0])`;
     return `Result.Failure("Unsupported…")` if not in `SupportedMajorVersions`.
  2. **Size guard**: check `file.Comments.Count > MaxImportCommentCount` (default 5000, read from
     `ISettingsService` or hard-coded constant for v1). Return 400 if exceeded.
  3. **Validation pass** (iterate all comments before any write):
     - `body` not empty; `environment` parses to `EnvironmentTag`; `status` parses to
       `CommentStatus`.
     - Collect all validation errors; if any, return `Result.Failure(joined message)`.
  4. **Demo cap pre-check**: if the project's owner is a demo tenant, batch-check
     `existingCount + importCount <= cap`; return 400 if exceeded.
  5. **Project resolution**: `_projectService.EnsureAsync(projectKey)` → get project `Id` and
     `OwnerId` (load via `_unitOfWork.Repository<Project>().Query().Select(p=>new{p.Id,p.OwnerId})`).
  6. **Batch insert loop** (all in one `SaveChangesAsync` call):
     - For each `CommentExportDto`:
       - Parse `Environment` and `Status` enums from string names.
       - Construct author attribution footnote if `author_display_name` is set:
         `body = dto.Body + "\n\n*(Imported — originally by: " + dto.AuthorDisplayName + ")*"`.
       - Build `Comment { ProjectId, Environment, Status, AuthorId = _currentUser.Id!, Body, IsPrivate = dto.IsPrivate, OwnerId = project.OwnerId, AppliedByLabel = dto.AppliedByLabel, Element = MapElement(dto.Element), CreatedAt = dto.CreatedAt }`.
       - **Note:** `CreatedAt` must be set explicitly on the entity **before** `SaveChangesAsync`,
         but `AppDbContext.SaveChangesAsync` will overwrite it for `Added` state entities
         (it stamps `now` unconditionally). To preserve original timestamps, call
         `dbContext.Entry(comment).Property(x => x.CreatedAt).IsModified = false` after
         `AddAsync`, or set the value and mark it as not-modified — see open decision #2.
       - Add replies to `comment.Replies` (same author re-attribution).
       - `await _unitOfWork.Repository<Comment>().AddAsync(comment)`.
  7. `await _unitOfWork.SaveChangesAsync()`.
  8. Return `ImportResultDto { ImportedComments, ImportedReplies, SkippedDuplicates=0, Warnings }`.

- [ ] `MapElement(ElementCaptureExportDto dto)` helper: maps all fields to `ElementCapture`;
  always sets `ScreenshotUrl = null` (never trust a screenshot reference from an import file).

- [ ] `dotnet build`.

---

### Task 5 [GLM]: Import controller endpoints + file size guard

**Files to create/modify:**
- `API/Controllers/ExportImportController.cs` (add import endpoints)

**What to build:**
- [ ] `POST /api/projects/{key}/import` → `[Authorize(Policy = Policies.Admin)]`. Accepts:
  - `application/json` body deserialized to `ExportFileDto` (for scripted use), OR
  - `multipart/form-data` with `file` field (`IFormFile`).
  For `multipart`: enforce `Request.ContentLength > 10_485_760` → 400 before reading; read
  `IFormFile` into a `MemoryStream`, deserialize.
- [ ] `POST /api/import` → same, workspace-level; routes comments by `project_key` inside the
  file.
- [ ] Both return 200 with `ImportResultDto`; 400/404/409 on errors following existing pattern.
- [ ] `dotnet build`.

---

### Task 6 [Claude]: Open Decision #2 — CreatedAt preservation strategy

**Context:** `AppDbContext.SaveChangesAsync` stamps `now` on every `Added` entity
(`e.Entity.CreatedAt = now`). This overwrites the original comment's `created_at` from the
export file, which is undesirable for backup/restore accuracy.

**Resolution options:**

**Option A (recommended):** After `AddAsync`, use EF's change tracker to unmark `CreatedAt` as
modified:
```csharp
await _unitOfWork.Repository<Comment>().AddAsync(comment);
// Access the underlying DbContext through IUnitOfWork.DbContext (needs exposing, or
// go through a new IRepository method) to:
_dbContext.Entry(comment).Property(x => x.CreatedAt).IsModified = false;
```
This requires exposing `DbContext` access from `IUnitOfWork` or adding a new
`IRepository<T>.SetPropertyUnmodified(T entity, Expression<Func<T,object>> prop)` method.

**Option B (simpler):** Add a boolean `PreserveTimestamps` flag to the import entity before
save, checked in `AppDbContext.SaveChangesAsync`:
```csharp
// In AppDbContext.SaveChangesAsync — check a flag set by the import service
if (e.State == EntityState.Added && !e.Entity.PreserveCreatedAt)
    e.Entity.CreatedAt = now;
```
This pollutes `BaseEntity` with an application-layer concern.

**Option C (pragmatic v1):** Do not preserve `created_at`; all imported comments get `ImportedAt`
as `CreatedAt`. Add `original_created_at` as a note in the attribution footnote.

**File for this task:** `Application/Abstractions/IUnitOfWork.cs`, `Infrastructure/Repository/UnitOfWork.cs`, `Infrastructure/AppDbContext.cs`.

- [ ] Choose an option (Claude), document the decision in a comment, implement it.
- [ ] `dotnet build` + `dotnet test`.

---

### Task 7 [GLM]: Wire service into DI + add integration smoke test

**Files to create/modify:**
- `Application/DependencyInjection.cs` — `ExportImportService` will be auto-registered by
  Scrutor (`t.Name.EndsWith("Service")`) — verify no explicit registration needed.
- `Tests/ExportImportServiceTests.cs` (new)

**What to build:**
- [ ] Add at minimum: one test that exports a seeded project (with two comments + one reply each)
  and asserts `schema_version == "1.0"`, comment count, reply count, `screenshot_omitted` flag,
  and author attribution footnote.
- [ ] One test that imports the export result back into a fresh project and asserts row counts and
  `OwnerId` stamping.
- [ ] One test that imports a file with an unsupported `schema_version` and gets `IsSuccess=false`.
- [ ] `dotnet test`.

---

### Task 8 [GLM]: Swagger / OpenAPI annotations

**Files to modify:**
- `API/Controllers/ExportImportController.cs` — add `[ProducesResponseType]`, `[Consumes]`,
  `[Produces]`, XML doc comments for Swagger.

- [ ] Export endpoints: `[Produces("application/json")]`, `[ProducesResponseType(typeof(ExportFileDto), 200)]`.
- [ ] Import endpoints: `[Consumes("application/json", "multipart/form-data")]`,
  `[ProducesResponseType(typeof(ImportResultDto), 200)]`.
- [ ] `dotnet build`.

---

## 8. Open Decisions (summary)

| # | Decision | Recommendation |
|---|---|---|
| **1** | Dedup via `import_dedup_hash` column (requires migration) or skip for v1 | Skip for v1; add in v2. Document the double-import risk in the UI. |
| **2** | Preserve original `created_at` on import — which strategy | Option A (EF change tracker, cleanest); implement in Task 6. |
| **3** | Admin-only import or any authenticated user can import | Admin-only (`[Authorize(Policy = Policies.Admin)]`). Rationale: import is a bulk write; a stakeholder should not be able to flood the workspace. |
| **4** | File size limit: hard-coded 10 MB or configurable via `AppSettings` | Hard-coded constant for v1 (`const int MaxImportFileSizeBytes = 10_485_760`); add `AppSettings` config key in v2. |
| **5** | `EnsureAsync` on import: lazy-creates unknown project keys — desired? | Acceptable for v1 (consistent with live behaviour). Add a strict mode flag in v2. |

---

## 9. Out of Scope (v2+)

- Screenshot embedding in exports (`includeScreenshots=true` flag with base64).
- Idempotent re-import via `import_dedup_hash` column.
- Streaming / chunked import for very large files (> 5000 comments).
- Import dry-run mode (validate + count without writing).
- Cross-server export/import wizard in the dashboard.
- CLI `pointer pull/push` integration with the export format.
