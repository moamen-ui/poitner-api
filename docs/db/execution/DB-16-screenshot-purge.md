# DB-16 — Screenshot purge: deleted-comment files, orphan sweep, uploads accounting

Roadmap: R5-66 follow-up (`docs/roadmap/execution/R5-00-INDEX.md` § "Follow-ups surfaced during implementation" #1; `docs/runbooks/DSAR.md`
§ "Follow-up code items" #1), foundations report **F5** (`docs/roadmap/meetings/2026-09-22-foundations/07-final-report.md` §4: screenshots are
**kept on erase**, deleted only with the comment or the workspace), privacy page `landing/privacy.html` §6 (the public contract this doc implements).
Rules: R1 (one nullable column), R5, R6, R7 (additive → ordinary deploy), R8 (per-workspace file paths; tests prove B cannot purge A), R10
(no rename of `uploads/` paths or of `comments.element` keys), R13, R14 (erase inventory row "screenshots — kept (F5)" is unchanged).
**Class: Additive** (one migration: `comments.screenshot_purged_at`) **+ a hosted job that deletes files from the `uploads` volume** (files of
comments soft-deleted longer than the published grace period, and files no comment references). File deletion is not reversible by the
database; §8 states the dump/tarball requirement. Ships as an ordinary `bash scripts/deploy-api.sh`. **Status: written 2026-09-23, not
implemented.** Owner decisions D16.1–D16.5 have defaults (§3.8); none blocks.

**Dependencies.** None on DB-17 (either order). DB-17's expiry path calls `TenantService.HardDeleteAsync`, which already deletes the workspace's
upload folder (§2); DB-16's orphan sweep is what catches anything that path misses.

## 1. Goal

`landing/privacy.html:232-242` promises: a screenshot "is **deleted when the workspace it belongs to is deleted**. Deleting a single comment hides
it and its screenshot from every screen; the file itself is removed by a scheduled clean-up of deleted comments (see the retention table) rather
than immediately." Today only the first half is true: `CommentService.DeleteAsync` sets `DeletedAt` and never touches the file
(`CommentService.cs:1295-1313`), no job ever removes files of deleted comments, the retention table (`privacy.html:253-260`) has no row for them,
and the one code path that does try to delete a single file (`EditAsync` "remove screenshot", `CommentService.cs:1030-1033`) passes the stored
**signed URL** to `LocalFileStorage.DeleteAsync`, which cannot resolve it (§2, bug B1) — so it silently deletes nothing. This doc makes the code
match the promise: a scheduled purge of files belonging to comments soft-deleted more than N days ago, an orphan sweep for files nothing
references, a working single-file delete, uploads-volume accounting in the log, and the privacy retention row the text already points at.
User-visible: "delete comment" now really frees the image after the grace period; the privacy page's retention table gains one row; the DSAR
runbook's manual `find … -delete` step (`docs/runbooks/DSAR.md:184-186`) becomes "wait for the purge, or run it now".

**F5 stands (explicit):** erasing a *person* (`IdentityEraseService.EraseAsync`, DB-11c §3.4 step 4) still deletes **no** screenshot. This doc
adds no `IFileStorage` dependency to `IdentityEraseService` (DB-11c acceptance criterion 7 keeps holding: `grep -c "IFileStorage\|_fileStorage"
Application/Services/Implementation/IdentityEraseService.cs` → 0). A screenshot leaves the disk in exactly three ways after this doc: with its
comment (soft-delete + grace period, §3.2), with its workspace (`TenantService.HardDeleteAsync`, unchanged), or because nothing references it
(§3.3). The DSAR runbook's targeted deletion on request is the manual `find` of `DSAR.md:186` plus, after this doc, a `POST /api/admin/comments/{id}`
"remove screenshot" is **not** added — the existing `EditAsync` with `RemoveScreenshot = true` (now working, §3.4) is the targeted path.

## 2. Prerequisites (verified facts, 2026-09-23 @ `78823ce`)

**Storage layer**
- `Application/Abstractions/IFileStorage.cs:3-26` — `SaveAsync(ownerSegment, project, content, extension)` returns a relative web path
  `uploads/<ownerSegment>/<project>/<file>` (`:8-11`); `DeleteAsync(relativePathOrUrl)` "best-effort … never throws on a missing file" (`:13-18`);
  `DeleteOwnerFilesAsync(ownerSegment)` recursive delete of `wwwroot/uploads/<ownerSegment>/` (`:20-25`). No list/exists member.
- `Infrastructure/Storage/LocalFileStorage.cs` (`Scoped`, `Infrastructure/DependencyInjection.cs:43`): `SaveAsync` writes
  `Path.Combine(webRoot, "uploads", ownerSegment, project)` (`:14`) with a `Guid.NewGuid():N` file name (`:17`) and returns
  `uploads/{ownerSegment}/{project}/{fileName}` (`:26`). `DeleteAsync` reduces the argument to the substring from `"uploads/"` (`:35-38`),
  resolves it under `wwwroot`, guards traversal, and swallows every IO error (`:49-54`) — **it returns no success/failure signal**.
  `DeleteOwnerFilesAsync` deletes the directory `uploads/<ownerSegment>` recursively, guarded to one level below `uploads` (`:68-81`).
- **The only other writer under `wwwroot/uploads/` is branding:** `API/Controllers/Admin/BrandingController.cs:153-154` and
  `API/Controllers/BrandingController.cs:74-75` use `Path.Combine(webRoot, "uploads", "branding")`. **`uploads/branding/` is not a workspace
  folder and must never be swept** (§3.3 allow-list). Widget bundles live under `wwwroot/widget/<hash>` (`Application/Common/WidgetVersionInfo.cs:62,79`),
  outside `uploads/`.
- Owner segment shapes: `UploadsController.cs:87-90` — `projectEntity.OwnerId.Value.ToString("N")` (32 lower-case hex) or `"global"` when the
  project has no owner (legacy; `projects.owner_id` is NOT NULL since `20260827130246_EnforceOwnerIdNotNull`). The project segment is the
  normalised project **key** (`:76`, `keyNormalized`), matched by `ProjectPattern ^[A-Za-z0-9._-]+$` (`:50`).
- `UploadsController.Upload` (`:52-131`) saves via `fileStorage.SaveAsync(ownerSegment, keyNormalized, …)` (`:113`) and returns
  `uploadSigner.SignedUrl(relativePath)` (`:117`) — a **relative signed URL** `/api/uploads/file?p=uploads%2F…&exp=…&sig=…`
  (`Infrastructure/Storage/UploadSigner.cs:35-45`). Files are served only through `GET /api/uploads/file` (`:137-140`); direct `/uploads/*` is
  blocked (`API/Program.cs:403-408`).
- **What `comments.element->>'ScreenshotUrl'` actually holds:** the widget stores the upload response's `url` verbatim
  (`web-component/src/element.ts:1786-1797` returns `envelope.data.url`; `:2157-2159` assigns `element.screenshotUrl = url`), and
  `CommentService` maps `ScreenshotUrl = dto.ScreenshotUrl` on create with no normalisation (`CommentService.cs:1326`). So production rows hold
  the **signed relative URL** shape (a) of `IUploadSigner.ExtractRelPath` (`Application/Abstractions/IUploadSigner.cs:34-41`: (a) signed URL,
  (b) absolute/relative public URL containing `uploads/`, (c) raw `uploads/…`). Reads re-sign via `_uploadSigner.SignedUrl(_uploadSigner.ExtractRelPath(...))`
  (`CommentService.cs:1350-1354`, `:1523-1527`). `IUploadSigner` is a singleton (`DependencyInjection.cs:44`); `IUploadSigner.SignedUrl(relPath, notAfter)`
  is a **default interface member** (`IUploadSigner.cs:26`) — the precedent for adding members without breaking the 28 test doubles.
- **Bug B1 (verified):** `LocalFileStorage.DeleteAsync` given shape (a) finds `"uploads/"` inside `/api/uploads/file?p=…` (`:35`), computes
  `rel = "uploads/file?p=uploads%2F…"`, and `File.Exists` is false → no-op. `EditAsync` `RemoveScreenshot` (`CommentService.cs:1030-1033`)
  therefore never deletes the file for widget-created comments. The DSAR runbook already notes this path is the only single-file delete
  (`DSAR.md:181-183`).

**Comment layer**
- `Domain/Entity/Comment.cs:5-14,44` — `BaseEntity` (soft delete `DeletedAt`), `OwnerId Guid?` (NOT NULL in practice, `SCHEMA.md` row `comments`),
  `Element ElementCapture` owned JSON (`Infrastructure/Mappings/CommentMapping.cs:57-80`, `e.ToJson("element")`, `ScreenshotUrl` max 2000 `:72`,
  **no `HasJsonPropertyName`** → JSON key is `ScreenshotUrl`). `Domain/ValueObjects/ElementCapture.cs:12` `ScreenshotUrl`. Replies carry no
  screenshot (no `ScreenshotUrl` on `Reply`). LINQ over the JSON property translates on Npgsql (`PlatformInsightsService.cs:252`
  `c.Element.ScreenshotUrl != null`) and Sqlite (`Tests/RetentionServiceTests.cs:366-390` seeds `Comment` rows on the Sqlite fixture).
- `CommentService.DeleteAsync(int id, Guid actorId, bool isAdmin)` (`CommentService.cs:1295-1313`) — sets `DeletedAt = UtcNow` (`:1309`), no file
  access. Fields `_fileStorage` (`:23,39,54`) and `_uploadSigner` (`:25,56`) already exist on the service.
- No comment hard-delete path exists except `TenantService.HardDeleteAsync` (`TenantService.cs:511-627`): `_fileStorage.DeleteOwnerFilesAsync(workspaceId.ToString("N"))`
  **before** the transaction (`:549`, comment "outside transaction — filesystem side effect"), then `DeleteOwnedAsync<Comment>` (`:558`). So
  **workspace hard-delete already purges screenshots** (a): verified. Consequence to accept: if the transaction later fails, the files are
  gone while the rows remain; the hourly demo cleanup / operator retry deletes the rows on the next attempt. Project delete is soft
  (`ProjectService.DeleteAsync`, roadmap hold-list "project purge job") and touches no files — out of scope.
- Export: `ExportImportService.cs:259-260` writes `ScreenshotUrl = null, ScreenshotOmitted = !string.IsNullOrEmpty(e.ScreenshotUrl)`; the DSAR
  runbook relies on `screenshot_omitted` (`DSAR.md:102-103,113-116`). Nulling `ScreenshotUrl` on purge would destroy that signal → §3.1 keeps the
  URL and adds a marker column instead.

**Retention job (DB-08 / DB-15) — the host for the new sweeps**
- `API/Hosted/RetentionService.cs`: `RetentionOptions` (`:13-28`; `RollupDays :20`), `RetentionSweepResult(int,int,int,int)` (`:31-36` — DB-15 kept
  it unchanged so tests compile; this doc does the same), `BindOptions(IConfiguration)` **field by field** (`:77-89` — a new option without a
  line here is silently ignored, GLM DB-15 #1), `SweepAsync` resolves `AppDbContext` from a scope (`:96-98`), `SweepOnceAsync(db, o, log, ct)`
  (`:111-149`) runs rollup → four sweeps (`:143-146`) and returns the result (`:148`). `SweepSnapshotsAsync` (`:241-289`) is the precedent for a
  sweep whose predicate joins `Comments.IgnoreQueryFilters()` on `DeletedAt == null` (`:263-265`). Daily interval, 5-min initial delay (`:55-60`).
  `SweepOnceAsync(` is called at **9** sites in `Tests/RetentionServiceTests.cs` (0 in `Tests/UsageRollupTests.cs`).
- Config surface: `API/appsettings.json:29-36`, `docker-compose.prod.yml:41-46` (`Retention__*` env), `.env.prod.example:50-58`; `DEPLOY.md:92-104`
  documents the job. `docker-compose.prod.yml:80` mounts `uploads:/app/wwwroot/uploads`; `scripts/backup-db.sh:45-55` archives that volume beside
  every dump (`uploads-<ts>.tgz`).
- Hosted-job precedents: `API/Hosted/UsageRollup.cs` (`internal static class`, `RollupAsync(db, o, nowUtc, log, ct)`, argument validation as the
  failure-injection seam), `API/Hosted/ImpersonationSweepService.cs:63-68` (`internal static SweepOnceAsync(db, audit, log, ct)`). DB-08's sweeps
  write **no** audit rows (`grep -c AuditActions API/Hosted/RetentionService.cs` → 0) — this doc follows that precedent (§3.6).

**Tests to copy**
- Sqlite fixture: `Tests/RetentionServiceTests.cs:44-70` (`TestDb`, `MakeContext`), comment seeding `:366-390`.
- `RecordingFileStorage : IFileStorage` (records `DeleteAsync`/`DeleteOwnerFilesAsync` calls): `Tests/DeletionSemanticsTests.cs:111-126`; the F5
  test `Erase_KeepsScreenshotFile` `:1185-1208`. Hard-delete test with `NoopFileStorage`: `Tests/WorkspaceTests.cs:493-503`. Signed-URL clamp test:
  `Tests/ImpersonationTests.cs:1865`. 28 test files declare an `IFileStorage` double (`grep -l IFileStorage Tests/*.cs | wc -l` → 28) — **every new
  `IFileStorage` member must be a default interface member** or all 28 break.
- Erase inventory row for screenshots: DB-11c §3.4 table ("kept (F5)") and `DB-RULES.md` R14 ("screenshots are kept, F5").

**Docs to amend**
- `landing/privacy.html:232-242` (§6 wording is already correct — keep), `:253-260` retention table (add one row), `docs/runbooks/DSAR.md:176-190`
  and `:259-277` (follow-up #1 → resolved), `DEPLOY.md:98-104` retention paragraph, `docs/db/SCHEMA.md` `comments` row.

## 3. Design

### 3.1 Schema — one column

`comments.screenshot_purged_at timestamptz NULL` (`Comment.ScreenshotPurgedAt`, mapping `b.Property(x => x.ScreenshotPurgedAt).HasColumnName("screenshot_purged_at");`).
Non-null ⇔ the purge job removed (or confirmed absent) the file that `Element.ScreenshotUrl` still names. `ScreenshotUrl` is **never rewritten**
by this doc (keeps `screenshot_omitted` truthful for exports and DSAR; R12 "never change the meaning of an existing key"). No index: the purge
predicate runs on soft-deleted rows only and the table is 122 rows (SCHEMA.md); if `comments` ever passes ~1 M rows, add
`ix_comments_deleted_unpurged (deleted_at) WHERE deleted_at IS NOT NULL AND screenshot_purged_at IS NULL` in its own migration (R4).

Doc-comment on the property (verbatim):
```csharp
/// <summary>
/// DB-16. Non-null = the screenshot file that <see cref="Element"/>.ScreenshotUrl names was removed from the uploads volume
/// (or confirmed missing) by the retention purge, after the comment had been soft-deleted for Retention:DeletedCommentScreenshotDays.
/// ScreenshotUrl is kept so exports still report screenshot_omitted = true. Never set on a live (DeletedAt == null) comment.
/// </summary>
public DateTime? ScreenshotPurgedAt { get; set; }
```

**Migration `AddCommentsScreenshotPurgedAt`** (scaffolded): exactly one `AddColumn<DateTime>("screenshot_purged_at", "comments", "timestamp with time zone", nullable: true)`.
`Down()` = `DropColumn`. No marker, no `[ContractMigration]`. **Every existing row:** untouched (`NULL`). Additive → auto-applies on boot (R7).

### 3.2 Purge of deleted comments' files — `API/Hosted/ScreenshotPurge.cs` (`internal static class`), step A

```csharp
/// <summary>DB-16. Step A: for every comment soft-deleted before now - DeletedCommentScreenshotDays that still names a screenshot and is not
/// yet marked purged, delete the file (best-effort, per file) and stamp screenshot_purged_at ONLY when the file is confirmed gone.
/// Step B: orphan sweep over the uploads volume (see OrphanSweepAsync). Both idempotent; a storage failure never throws out of here.</summary>
internal static async Task<ScreenshotPurgeResult> PurgeDeletedAsync(AppDbContext db, IFileStorage storage, IUploadSigner signer,
    RetentionOptions o, DateTime nowUtc, ILogger log, CancellationToken ct)
```
`ScreenshotPurgeResult(int CommentsPurged, int FilesDeleted, long BytesDeleted, int Failures)` — a **new** record in the same file
(`RetentionSweepResult` stays as is, DB-15 precedent).

Algorithm:
0. `if (o.DeletedCommentScreenshotDays <= 0) { log "Retention: deleted-comment screenshots skipped (period 0)"; return zero; }` (same shape as `:159-163`).
   `if (o.BatchSize <= 0) throw new ArgumentOutOfRangeException(nameof(o));` (failure-injection seam, UsageRollup precedent).
1. `cutoff = nowUtc.AddDays(-o.DeletedCommentScreenshotDays)`. Loop:
   `rows = db.Comments.IgnoreQueryFilters().Where(c => c.DeletedAt != null && c.DeletedAt < cutoff && c.ScreenshotPurgedAt == null && c.Element.ScreenshotUrl != null && c.Element.ScreenshotUrl != "").OrderBy(c => c.Id).Take(o.BatchSize).ToListAsync(ct)`;
   `if (rows.Count == 0) break;`.
2. For each row: `rel = signer.ExtractRelPath(row.Element.ScreenshotUrl!)`; if `rel` does not start with `"uploads/"` (ordinal) → count as
   `Failures`, log Warning once per row id ("unrecognised screenshot url shape"), **stamp `ScreenshotPurgedAt = nowUtc` anyway** (there is no file
   we could ever find for it; leaving it unmarked would retry forever). Else `size = await storage.SizeAsync(rel)` (0 when missing),
   `await storage.DeleteAsync(rel)`, `gone = !await storage.ExistsAsync(rel)`; if `gone` → `row.ScreenshotPurgedAt = nowUtc; FilesDeleted += (size > 0 ? 1 : 0); BytesDeleted += size;`
   else `Failures++` (left unmarked; retried next pass). `CommentsPurged` counts rows stamped.
3. `await db.SaveChangesAsync(ct)` per batch (the hosted context has no current user — `updated_by` stays null, `updated_at` is stamped by
   `AppDbContext.SaveChangesAsync`; acceptable and noted), then `db.ChangeTracker.Clear()`. `if (rows.Count < o.BatchSize) break;`.
4. One Information line per pass: `Retention: deleted-comment screenshots purged {Comments} comment(s), {Files} file(s), {Bytes} bytes, {Failures} failure(s) older than {Cutoff:u}`.

**What soft-delete does (b):** nothing new. `CommentService.DeleteAsync` stays a pure soft delete — the privacy text says "removed by a scheduled
clean-up … rather than immediately", and the grace period is the undo window (there is no restore endpoint today; the window is for support).
Default `Retention:DeletedCommentScreenshotDays = 30` (D16.1) — the number the R5-00 index already proposed and the privacy table will publish.

**New `IFileStorage` members (default implementations, so the 28 doubles keep compiling — `IUploadSigner.cs:26` precedent):**
```csharp
/// <summary>DB-16. True when the relative path ("uploads/…") names an existing file. Default: false (test doubles).</summary>
Task<bool> ExistsAsync(string relativePath) => Task.FromResult(false);
/// <summary>DB-16. Size in bytes of the file, 0 when missing. Default: 0.</summary>
Task<long> SizeAsync(string relativePath) => Task.FromResult(0L);
/// <summary>DB-16. Every file under uploads/&lt;ownerSegment&gt;/ as (relativePath, bytes, lastWriteUtc). Default: empty.</summary>
IAsyncEnumerable<StoredFile> ListOwnerFilesAsync(string ownerSegment) => AsyncEnumerable.Empty<StoredFile>();
/// <summary>DB-16. The first-level folder names under uploads/ (owner segments, plus non-workspace folders such as "branding"). Default: empty.</summary>
Task<IReadOnlyList<string>> ListOwnerSegmentsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
```
`public sealed record StoredFile(string RelativePath, long Bytes, DateTime LastWriteUtc);` in `Application/Abstractions/IFileStorage.cs`.
`LocalFileStorage` implements all four with the same `webRoot` resolution and the same traversal guard as `DeleteAsync` (`:40-48`); `ListOwnerFilesAsync`
enumerates `Directory.EnumerateFiles(ownerDir, "*", SearchOption.AllDirectories)` and yields `uploads/<owner>/<project>/<file>` with forward slashes.
(`AsyncEnumerable.Empty` needs `System.Linq.Async`? **No** — write a tiny private static async iterator in the interface file instead:
`private static async IAsyncEnumerable<StoredFile> EmptyAsync() { await Task.CompletedTask; yield break; }` and return `EmptyAsync()`. Do not add a package.)

### 3.3 Orphan sweep — `ScreenshotPurge.OrphanSweepAsync(db, storage, signer, o, nowUtc, log, ct)` (step B)

Purpose: files on disk that no comment references — failed comment posts after a successful upload, rows hard-deleted by paths that never called
`DeleteAsync` (B1 for every "remove screenshot" ever clicked), workspaces deleted before `DeleteOwnerFilesAsync` existed, and any future miss.

1. `if (o.UploadOrphanGraceHours <= 0) skip (log)`. `graceCutoff = nowUtc.AddHours(-o.UploadOrphanGraceHours)`. Default **48 h** (D16.2): an upload
   precedes its comment POST by seconds, but a widget left open with a captured screenshot, a browser crash, or a retry can stretch that; 48 h is
   generous and still bounds the leak.
2. `segments = await storage.ListOwnerSegmentsAsync()`. **Allow-list (mandatory):** process a segment only when `Guid.TryParseExact(seg, "N", out var ownerId)`
   or `seg == "global"`. Every other segment (today: `branding`) is **skipped** and logged once per pass at Debug: `Retention: uploads segment {Seg} is not a workspace folder; skipped`.
   `uploads/branding/` holds branding assets (`Admin/BrandingController.cs:153-154`) — deleting them would break the operator's logo.
3. Per workspace segment (bounded memory — one owner at a time): `referenced = (await db.Comments.IgnoreQueryFilters().Where(c => c.OwnerId == ownerId && c.Element.ScreenshotUrl != null && c.Element.ScreenshotUrl != "" && (c.DeletedAt == null || c.ScreenshotPurgedAt == null)).Select(c => c.Element.ScreenshotUrl!).ToListAsync(ct)).Select(signer.ExtractRelPath).ToHashSet(StringComparer.Ordinal)`.
   For `global`: the same query without the `OwnerId` predicate but filtered in memory to paths starting with `uploads/global/`.
   Note the predicate: a **soft-deleted comment inside its grace period still references its file** (`ScreenshotPurgedAt == null`) — step A, not
   step B, is what removes those, on the published schedule. A soft-deleted comment already stamped purged no longer protects the path (the file
   is supposed to be gone; if a copy re-appeared it is an orphan).
4. `await foreach (var f in storage.ListOwnerFilesAsync(seg))`: `scannedFiles++; scannedBytes += f.Bytes;` if `referenced.Contains(f.RelativePath)` → continue;
   if `f.LastWriteUtc > graceCutoff` → `youngUnreferenced++`, continue; else `await storage.DeleteAsync(f.RelativePath)`; `if (!await storage.ExistsAsync(f.RelativePath)) { orphansDeleted++; orphanBytes += f.Bytes; } else failures++`.
   `ct.ThrowIfCancellationRequested()` every 100 files.
5. **Uploads accounting (d)** — one Information line per pass, machine-readable placeholders:
   `Retention: uploads volume {Segments} workspace folder(s), {Files} file(s), {Bytes} bytes scanned; {Orphans} orphan file(s) / {OrphanBytes} bytes deleted; {Young} unreferenced file(s) inside the {GraceHours} h grace window kept; {Failures} failure(s); {Skipped} non-workspace folder(s) skipped`.
   Result record `UploadSweepResult(int Segments, int Files, long Bytes, int Orphans, long OrphanBytes, int Young, int Failures, int Skipped)`.
   No table, no endpoint (D16.4): the log line is the accounting; R5-58's JSON logs make it queryable. If the owner later wants a card, the number
   comes from this line or a future `usage_daily` type — not from this doc.
6. Empty project folders left behind are removed (`Directory.Delete(dir, recursive: false)` inside a `try/catch`) — implement inside
   `LocalFileStorage.DeleteAsync` when the parent directory becomes empty **and** is at least two levels below `uploads/` (never the owner folder
   itself; never `uploads/`). Optional; if it complicates the guard, skip it and say so in the PR.

### 3.4 Fix B1 — every single-file delete goes through `ExtractRelPath`

- `LocalFileStorage` gains a constructor parameter `IUploadSigner signer` (both are Infrastructure types; `IUploadSigner` is a singleton,
  `LocalFileStorage` scoped — allowed direction) and `DeleteAsync`/`ExistsAsync`/`SizeAsync` start with `var rel = signer.ExtractRelPath(relativePathOrUrl);`
  before the existing `IndexOf("uploads/")` logic (`:35`). The interface doc-comment of `DeleteAsync` (`IFileStorage.cs:13-18`) gains the sentence
  "Accepts the signed-URL shape too (DB-16)". `CommentService.cs:1032` **also** passes `_uploadSigner.ExtractRelPath(comment.Element.ScreenshotUrl!)`
  (belt and braces; the test in §6 asserts the recorder receives an `uploads/…` path, not a `/api/uploads/file?…` string).
- `TenantService.HardDeleteAsync :549` is unchanged (owner folder, not a file).

### 3.5 Wiring into `RetentionService`

- `RetentionOptions` gains `public int DeletedCommentScreenshotDays { get; init; } = 30;` and `public int UploadOrphanGraceHours { get; init; } = 48;`
  with doc-comments naming this doc. **`BindOptions()` (`:77-89`) gains both lines** (`DeletedCommentScreenshotDays = config.GetValue("Retention:DeletedCommentScreenshotDays", 30), UploadOrphanGraceHours = config.GetValue("Retention:UploadOrphanGraceHours", 48),`).
- `SweepOnceAsync` signature becomes `SweepOnceAsync(AppDbContext db, RetentionOptions o, ILogger log, CancellationToken ct, IFileStorage storage, IUploadSigner signer)`
  (two **required** trailing parameters; update all 9 call sites in `Tests/RetentionServiceTests.cs` with a `NoopFileStorage`-style double and a
  real `new UploadSigner(config)` — `UploadSigner` needs `JWT:SigningKey` in the `IConfiguration`, copy `Tests/ResetTokenServiceTests.cs`'s
  `AddInMemoryCollection` shape). `SweepAsync` (`:91-104`) resolves both from the scope. After the four sweeps and before `return` (`:148`):
  ```csharp
  // DB-16: file purge runs LAST and is isolated — a storage failure never fails the row sweeps, and vice versa.
  try { var a = await ScreenshotPurge.PurgeDeletedAsync(db, storage, signer, o, now, log, ct);
        var b = await ScreenshotPurge.OrphanSweepAsync(db, storage, signer, o, now, log, ct); }
  catch (OperationCanceledException) { throw; }
  catch (Exception ex) { log.LogError(ex, "Retention: screenshot purge failed; rows were swept normally"); }
  ```
  `RetentionSweepResult` unchanged.
- Config: `API/appsettings.json` `"DeletedCommentScreenshotDays": 30, "UploadOrphanGraceHours": 48`; `docker-compose.prod.yml:41-46` +
  `Retention__DeletedCommentScreenshotDays: "${RETENTION_DELETED_SCREENSHOT_DAYS:-30}"` and `Retention__UploadOrphanGraceHours: "${RETENTION_UPLOAD_ORPHAN_GRACE_HOURS:-48}"`;
  `.env.prod.example:50-58` two commented lines with one comment each.

### 3.6 Idempotency, failure isolation, audit (e)

- Step A is idempotent by the marker: a stamped row is never selected again; an unstamped row whose file still exists is retried next pass; a
  missing file counts as gone (stamped). Step B is idempotent by construction (disk state).
- A storage exception inside either step is caught **per file** (`LocalFileStorage` already swallows; the job additionally wraps each file in
  `try/catch` and counts `Failures`) and **per pass** (§3.5). `CommentService.DeleteAsync` never calls storage → a storage failure cannot fail a
  comment delete (acceptance grep 5).
- No audit rows: DB-08 sweeps write none (§2); the purge changes no security state and `audit_events` must hold no per-file paths (R17: ids/counts
  only). The Information log lines are the record. If the owner wants a security-log entry, it is one `retention.screenshots_purged` row per
  workspace per pass with `count` only — a later amendment, not this doc (D16.5).
- Tenancy (R8): step A selects by row (owner-agnostic, it is the row's own file); step B builds the reference set **per owner segment with an
  explicit `OwnerId == ownerId` predicate** and lists only that segment's folder, so a bug in one workspace's paths cannot delete another's files
  (§6 test 6 proves a file referenced by A under A's folder survives a pass that purges B's deleted comment).

### 3.7 What is deliberately not changed

`IdentityEraseService` (F5), `TenantService.HardDeleteAsync :549`, `ProjectService.DeleteAsync` (soft; the hold-list "project purge job"),
`ExportImportService` (still nulls `ScreenshotUrl` on export), `UploadsController` (still returns signed URLs; the stored shape is a frozen
identifier for existing rows — R10), the widget, the `uploads/` path layout, `scripts/backup-db.sh`.

### 3.8 Owner decisions encoded here (defaults apply unless the owner says otherwise before §9)

| # | Question | Default |
|---|---|---|
| D16.1 | Grace period between a comment's soft-delete and the file purge | **30 days** (`Retention:DeletedCommentScreenshotDays`); published in the privacy retention table. 0 disables the purge (files then leave only with the workspace, and the privacy row must say "with the workspace") |
| D16.2 | Orphan grace (unreferenced file age before deletion) | **48 hours** (`Retention:UploadOrphanGraceHours`) by file mtime |
| D16.3 | Marker column vs. nulling `ScreenshotUrl` | **Column `screenshot_purged_at`**, URL kept — preserves `screenshot_omitted` in exports (DSAR) and an audit of when the file left |
| D16.4 | Uploads accounting surface | **Log line only** (JSON logs, R5-58). No table, no endpoint, no dashboard card |
| D16.5 | Audit row per purge pass | **No** (DB-08 precedent). Revisit if a customer asks for deletion evidence beyond the log |
| F5 *(founder, fixed)* | Screenshots on identity erase | **Keep** — unchanged by this doc; §1 |

## 4. Safety classification

**Additive** (R1): one nullable column; ordinary deploy (R7); `Down()` drops it. **The job deletes files** from the `uploads` volume on a schedule
the privacy page publishes: (1) files of comments soft-deleted ≥ 30 d ago, (2) files no comment references and older than 48 h. Files are not
in Postgres; the only rollback is the `uploads-<ts>.tgz` archive `scripts/backup-db.sh` writes beside every dump (`:45-55`) — R6's pre-deploy
dump therefore also covers the files. The first production pass runs 5 min after boot (`RetentionService.cs:55-56`); §9 step 1 sizes it first.
Tenancy: §3.6. No data-moving migration, no backfill.

## 5. File-level tasks

1. `Domain/Entity/Comment.cs` — add `ScreenshotPurgedAt` after `OwnerId` (`:44`) with the §3.1 doc-comment. `Infrastructure/Mappings/CommentMapping.cs` —
   `b.Property(x => x.ScreenshotPurgedAt).HasColumnName("screenshot_purged_at");` next to the other scalar properties (outside the `OwnsOne` block `:57-80`).
2. `just migrate name="AddCommentsScreenshotPurgedAt"` → open the file: exactly one `AddColumn` on `comments`; `Down()` one `DropColumn`; snapshot diff
   touches only `Comment`. Anything else → stop and report (R13).
3. `Application/Abstractions/IFileStorage.cs` — `StoredFile` record + the four default members (§3.2 verbatim, with the private `EmptyAsync()` iterator).
   Doc-comment sentence on `DeleteAsync` (§3.4).
4. `Infrastructure/Storage/LocalFileStorage.cs` — constructor `(IWebHostEnvironment env, IUploadSigner signer)`; `ExtractRelPath` first in `DeleteAsync`;
   implement `ExistsAsync`, `SizeAsync`, `ListOwnerFilesAsync`, `ListOwnerSegmentsAsync` (§3.2–3.3; reuse the `webRoot`/`uploadsRoot` resolution `:40-48`
   and the same `StartsWith(uploadsRoot + separator)` guard); optional empty-folder cleanup (§3.3 step 6). `Infrastructure/DependencyInjection.cs:43` unchanged
   (DI resolves the new parameter).
5. `Application/Services/Implementation/CommentService.cs:1032` — `await _fileStorage.DeleteAsync(_uploadSigner.ExtractRelPath(comment.Element.ScreenshotUrl!));`.
6. `API/Hosted/ScreenshotPurge.cs` (new) — `ScreenshotPurgeResult`, `UploadSweepResult`, `PurgeDeletedAsync`, `OrphanSweepAsync` (§3.2–3.3 verbatim; `internal static`).
7. `API/Hosted/RetentionService.cs` — two options + two `BindOptions` lines + `SweepOnceAsync` signature + the §3.5 block + `SweepAsync` resolving
   `IFileStorage`/`IUploadSigner` from the scope. `API/appsettings.json:29-36`, `docker-compose.prod.yml:41-46`, `.env.prod.example:50-58` (§3.5).
8. `Tests/RetentionServiceTests.cs` — update the 9 `SweepOnceAsync(` call sites (§3.5). Tests (§6).
9. Docs: `landing/privacy.html:253-260` add `<tr><td>Screenshots of deleted comments</td><td>30 days after the comment is deleted</td></tr>` as the
   last row (Arabic page, if R5-65 fix-now 5 has landed: same row); `docs/runbooks/DSAR.md:176-190` replace the manual `find` paragraph with "the
   retention job purges the file 30 days after soft-delete; to act sooner, ask the workspace admin to edit the comment with *remove screenshot*,
   or run `docker compose exec api find /app/wwwroot/uploads/<ownerN>/<projectKey> -name '<file>' -delete` where `<file>` is the last path segment
   of `element->>'ScreenshotUrl'` (decode `p=`)"; `DSAR.md:259-277` mark follow-up #1 **resolved by DB-16**; `DEPLOY.md:98-104` add one sentence:
   "It also purges screenshot files of comments soft-deleted > 30 d and orphan files > 48 h old under `uploads/` (never `uploads/branding/`), logging
   a per-pass volume line." `docs/db/SCHEMA.md` `comments` row: add `screenshot_purged_at` (DB-16).
10. `just fmt`; `just test`.

## 6. Tests

Sqlite `TestDb` (`Tests/RetentionServiceTests.cs:44-70`) unless noted; a `RecordingFileStorage` double extended with an in-memory file map
(`Dictionary<string,(long Bytes, DateTime Mtime)>`) implementing all six members, in `Tests/ScreenshotPurgeTests.cs`:
1. `PurgeDeleted_AfterGrace_DeletesFile_AndStamps` — comment soft-deleted 31 d ago with `ScreenshotUrl = "/api/uploads/file?p=uploads%2F<ownerN>%2Fproj%2Fa.webp&exp=1&sig=x"`
   and the file present → recorder `Deleted == ["uploads/<ownerN>/proj/a.webp"]`, row `ScreenshotPurgedAt != null`, `ScreenshotUrl` unchanged, result `CommentsPurged == 1, FilesDeleted == 1, BytesDeleted == <size>`.
2. `PurgeDeleted_InsideGrace_Untouched` (deleted 29 d ago → nothing deleted, unstamped); `PurgeDeleted_LiveComment_NeverTouched` (`DeletedAt == null`, 400 d old → untouched).
3. `PurgeDeleted_MissingFile_StampsWithoutCounting` (file absent → stamped, `FilesDeleted == 0`); `PurgeDeleted_IsIdempotent` (second run → zero counts, stamp unchanged);
   `PurgeDeleted_StorageStillHasFile_LeavesUnstamped` (double whose `DeleteAsync` does nothing → `Failures == 1`, unstamped, retried next run);
   `PurgeDeleted_UnrecognisedUrl_StampsAndCountsFailure` (`ScreenshotUrl = "https://elsewhere.example/x.png"`).
4. `PurgeDeleted_PeriodZero_Skips`; `PurgeDeleted_BatchSizeZero_Throws` (`ArgumentOutOfRangeException` before any query — the §3.5 isolation seam).
5. `OrphanSweep_DeletesUnreferencedOldFile_KeepsReferenced_KeepsYoung` — files `a` (referenced by a live comment), `b` (unreferenced, mtime 3 d ago), `c` (unreferenced, mtime 1 h ago)
   → only `b` deleted; result `Orphans == 1, Young == 1, Files == 3`. `OrphanSweep_SoftDeletedInsideGrace_StillProtectsFile` (deleted 1 d ago, unpurged → kept);
   `OrphanSweep_StampedPurgedRow_NoLongerProtects` (stamped row, file re-appeared, old → deleted).
6. **R8** `OrphanSweep_ProcessesOnlyThatOwnersFolder_TenantIsolation` — two owner segments A and B; a live comment in A references `uploads/A/p/x.png`;
   B's folder holds an unreferenced old file; run → B's file deleted, A's kept, and the recorder shows `ListOwnerFilesAsync` was called with each segment once.
   Also `PurgeDeleted_TenantB_FileUnderTenantA_NotDeleted`: a soft-deleted comment in B whose URL points into A's folder → the path is deleted only if it
   is the row's own URL (it is) — assert the test double received exactly that one path and nothing else from A (documents that step A trusts the
   row's URL; the R8 guarantee for step A is the row's own `OwnerId` folder in normal writes, `UploadsController.cs:87-90`).
7. `OrphanSweep_SkipsBrandingAndUnknownSegments` — segments `["branding", "not-a-guid", "<ownerN>", "global"]` → files under `branding` and `not-a-guid`
   never listed or deleted; `Skipped == 2`.
8. `OrphanSweep_GlobalSegment_UsesReferencedGlobalPaths` — a comment with `uploads/global/p/x.png` protects that file; another old global file is deleted.
9. `Tests/RetentionServiceTests.cs` additions: `Sweep_RunsPurge_AfterRowSweeps_AndStorageFailureDoesNotFailRows` (storage double whose `ListOwnerSegmentsAsync` throws →
   `SweepOnceAsync` still returns the four row counts; the error is logged); `BindOptions_ReadsDeletedCommentScreenshotDays_AndOrphanGrace` (`ConfigurationBuilder` with `7`/`12` → options `7`/`12`).
10. `Tests/LocalFileStorageTests.cs` (new; real temp directory as `WebRootPath` via a fake `IWebHostEnvironment`, real `UploadSigner` with a 32+-char key):
    `DeleteAsync_AcceptsSignedUrl` (save → sign → `DeleteAsync(signedUrl)` → file gone: **the B1 regression test**); `DeleteAsync_AcceptsRawRelPath`;
    `DeleteAsync_RejectsTraversal` (`uploads/../appsettings.json` untouched); `ListOwnerFilesAsync_ReturnsForwardSlashRelPaths_AndSizes`;
    `ListOwnerSegmentsAsync_ReturnsFirstLevelFolders`; `ExistsAsync_SizeAsync_RoundTrip`.
11. `CommentService` (InMemory fixture, e.g. `Tests/CommentVerifyTests.cs:34` `FakeFileStorage` shape but recording): `Edit_RemoveScreenshot_PassesRelPathToStorage`
    (stored signed URL → recorder receives `uploads/…`); `Delete_SoftDeletes_TouchesNoFile` (recorder empty after `DeleteAsync`).
12. **F5 unchanged:** `Tests/DeletionSemanticsTests.cs:1185-1208` `Erase_KeepsScreenshotFile` keeps passing; add one assertion there that the
    `ScreenshotPurgedAt` of the erased person's comment is still `null` after erase.
13. Existing data survives: the migration adds a NULL column; rehearsal (§7 criterion 7) proves 0 rows purged on the first production pass unless
    soft-deleted comments older than 30 d with screenshots exist (query in §9 step 1 tells the number beforehand).

## 7. Acceptance criteria

1. `dotnet ef migrations list -p Infrastructure -s API --no-connect` ends with `_AddCommentsScreenshotPurgedAt`; `grep -c "AddColumn" Infrastructure/Migrations/*_AddCommentsScreenshotPurgedAt.cs` → 1; `grep -c ContractMigration` on it → 0.
2. `grep -c "ExtractRelPath" Infrastructure/Storage/LocalFileStorage.cs` → ≥ 1; `grep -n "ExtractRelPath" Application/Services/Implementation/CommentService.cs | grep -c 1032` → 1 (line may shift; the reviewer confirms the remove-screenshot call passes through it).
3. `grep -c "ExistsAsync\|SizeAsync\|ListOwnerFilesAsync\|ListOwnerSegmentsAsync" Application/Abstractions/IFileStorage.cs` → 4; `grep -c "=> " Application/Abstractions/IFileStorage.cs` → ≥ 3 (default members); `just test` compiles **without editing any of the 28 existing `IFileStorage` doubles** (`git diff --stat Tests/ | grep -c "FileStorage"` → only the new files).
4. `grep -c "Retention:DeletedCommentScreenshotDays\|Retention:UploadOrphanGraceHours" API/Hosted/RetentionService.cs` → 2; `grep -c "RETENTION_DELETED_SCREENSHOT_DAYS\|RETENTION_UPLOAD_ORPHAN_GRACE_HOURS" docker-compose.prod.yml .env.prod.example` → 2 each.
5. **Failure isolation:** `grep -c "_fileStorage" Application/Services/Implementation/CommentService.cs` → exactly the existing count + 0 in `DeleteAsync` (reviewer reads `:1295-1313`: no storage call); `grep -c "IFileStorage\|_fileStorage" Application/Services/Implementation/IdentityEraseService.cs` → 0 (F5).
6. `grep -c '"branding"' API/Hosted/ScreenshotPurge.cs` → 0 **and** `grep -c 'TryParseExact' API/Hosted/ScreenshotPurge.cs` → ≥ 1 (the allow-list is by shape, not by naming `branding`; test 7 proves `branding` is skipped).
7. Rehearsal (R11) on a same-day dump **plus the matching `uploads-<ts>.tgz` extracted into the rehearsal container's `wwwroot/uploads`**: set
   `Retention__InitialDelayMinutes=0`, wait one pass → the log shows both new Information lines; `SELECT count(*) FROM comments WHERE screenshot_purged_at IS NOT NULL` equals the
   pre-count from §9 step 1; `find wwwroot/uploads/branding -type f | wc -l` unchanged before/after; every path in
   `SELECT element->>'ScreenshotUrl' FROM comments WHERE deleted_at IS NULL AND element->>'ScreenshotUrl' IS NOT NULL` still exists on disk (script: decode `p=`, `test -f`).
8. `grep -c "Screenshots of deleted comments" landing/privacy.html` → 1; `grep -c "resolved by DB-16" docs/runbooks/DSAR.md` → ≥ 1.
9. `just test` green with the ~25 new facts; DB-10 green.

## 8. Rollback

Migration `Down()` drops `screenshot_purged_at` (the marker only; nothing else changes). Code rollback = revert + ordinary redeploy. **Files
deleted by the job are not restored by rollback** — restore them from the `uploads-<ts>.tgz` written by `scripts/backup-db.sh` (`DEPLOY.md:176`:
`docker run --rm -v pointer-api_uploads:/u -v ~/backups:/b alpine:3 sh -c 'cd /u && tar -xzf /b/uploads-<ts>.tgz'`). Because the first pass runs 5 min
after boot, the R6 `pre-deploy` dump + tarball taken by `deploy-api.sh` is the restore point; **no labelled dump is required**, but §9 step 1 must be
read before deploying so the size of the first purge is known.

## 9. Release steps

1. **Size the first pass on production (read-only):**
   `SELECT count(*) AS to_purge FROM comments WHERE deleted_at < now() - interval '30 days' AND element->>'ScreenshotUrl' <> '' AND element->>'ScreenshotUrl' IS NOT NULL;`
   and `docker compose exec api sh -c 'find /app/wwwroot/uploads -type f | wc -l; du -sh /app/wwwroot/uploads; ls /app/wwwroot/uploads'` — confirm the
   first-level folders are 32-hex segments, `global` (if present) and `branding` only. Paste both into the PR.
2. Ordinary R11 rehearsal with the uploads tarball (§7 criterion 7).
3. `bash scripts/deploy-api.sh` (takes the dump + tarball first, R6). Expect one `Applying migration … AddCommentsScreenshotPurgedAt` line; 5 min later
   the two new `Retention:` lines.
4. Verify: `SELECT count(*) FROM comments WHERE screenshot_purged_at IS NOT NULL;` equals step 1's `to_purge` (± rows deleted in between); branding
   assets still render on the login page; a live comment's screenshot still opens in the widget.
5. Watch for `Retention: screenshot purge failed` (must not appear) and for a non-zero `{Failures}` in either line (investigate paths; the job retries next day).
6. Dashboard: none. Publish the privacy row (task 9) in the same deploy — the landing is served by the same image.

## 10. Out of scope

Deleting screenshots on identity erase (F5 = keep); hard-deleting comment **rows** (privacy §7 says rows go only with the workspace); a restore-comment
endpoint; a project purge job (roadmap hold-list); moving screenshots to object storage (report §2 "DEFER"); per-workspace storage quotas or a
dashboard storage card (D16.4); `ExportImportService`; the widget; the CLI; `clients/`; `scripts/backup-db.sh`; DB-17 (demo expiry uses the
unchanged `HardDeleteAsync` path).

## 11. Dashboard / widget / CLI tasks

**Dashboard:** none (no endpoint or DTO changes). **Widget:** none. **CLI:** none.
**Docs/landing (same PR, task 9):** privacy retention row; DSAR runbook; DEPLOY.md sentence; SCHEMA.md row.
