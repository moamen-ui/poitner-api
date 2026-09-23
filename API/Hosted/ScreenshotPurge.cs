using System.Linq;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure;

namespace Pointer.API.Hosted;

/// <summary>Row/file counts for one <see cref="ScreenshotPurge.PurgeDeletedAsync"/> pass (step A).</summary>
public sealed record ScreenshotPurgeResult(
    int CommentsPurged,
    int FilesDeleted,
    long BytesDeleted,
    int Failures,
    int Skipped,
    int WouldDelete,
    long WouldBytes
);

/// <summary>File/byte counts for one <see cref="ScreenshotPurge.OrphanSweepAsync"/> pass (step B).</summary>
public sealed record UploadSweepResult(
    int Segments,
    int Files,
    long Bytes,
    int Orphans,
    long OrphanBytes,
    int Young,
    int Failures,
    int Skipped,
    bool DryRun
);

/// <summary>
/// DB-16. Step A: for every comment soft-deleted before now - DeletedCommentScreenshotDays that still names a screenshot and is not
/// yet marked purged, delete the file (best-effort, per file) and stamp screenshot_purged_at ONLY when the file is confirmed gone.
/// Step B: orphan sweep over the uploads volume (see OrphanSweepAsync). Both idempotent; a storage failure never throws out of here.
/// </summary>
internal static class ScreenshotPurge
{
    /// <summary>
    /// The day 20260827130246_EnforceOwnerIdNotNull made a "global" upload segment impossible for any
    /// NEW upload (every project now has a real OwnerId). A comment created before this instant may
    /// legitimately still name a file under uploads/global/; one created after it never can, so such
    /// a row is treated as foreign (Opus DB-16 #1/#3) and left untouched.
    /// </summary>
    private static readonly DateTime LegacyGlobalCutoff = new(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);

    internal static async Task<ScreenshotPurgeResult> PurgeDeletedAsync(
        AppDbContext db,
        IFileStorage storage,
        IUploadSigner signer,
        RetentionOptions o,
        DateTime nowUtc,
        ILogger log,
        CancellationToken ct
    )
    {
        if (o.DeletedCommentScreenshotDays <= 0)
        {
            log.LogInformation("Retention: deleted-comment screenshots skipped (period 0)");
            return new ScreenshotPurgeResult(0, 0, 0, 0, 0, 0, 0);
        }
        if (o.BatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(o));

        var dryRun = o.ScreenshotPurgeDryRun;
        var cutoff = nowUtc.AddDays(-o.DeletedCommentScreenshotDays);

        var commentsPurged = 0;
        var filesDeleted = 0;
        long bytesDeleted = 0;
        var failures = 0;
        var skipped = 0;
        var wouldDelete = 0;
        var wouldDeleteFiles = 0;
        long wouldBytes = 0;
        var skippedIds = new List<int>();

        var lastId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var rows = await db
                .Comments.IgnoreQueryFilters()
                .Where(c =>
                    c.Id > lastId
                    && c.DeletedAt != null
                    && c.DeletedAt < cutoff
                    && c.ScreenshotPurgedAt == null
                    && c.Element.ScreenshotUrl != null
                    && c.Element.ScreenshotUrl != ""
                )
                .OrderBy(c => c.Id)
                .Take(o.BatchSize)
                .Select(c => new
                {
                    c.Id,
                    c.OwnerId,
                    c.CreatedAt,
                    c.Element.ScreenshotUrl,
                })
                .ToListAsync(ct);

            if (rows.Count == 0)
                break;
            lastId = rows[^1].Id;

            var stampIds = new List<int>();

            foreach (var row in rows)
            {
                var rel = signer.ExtractRelPath(row.ScreenshotUrl!);

                // DB-16 review fix #1 (BLOCKER): canonical-shape check FIRST, before the
                // ownership-prefix check below. A crafted path (dot-dot, encoded dot-dot,
                // backslash, a nested/double-decoded signed URL, "../branding/...") can make
                // `rel.StartsWith("uploads/{ownerGuid:N}/")` true by literal string prefix while
                // still resolving outside that owner's folder once it reaches the filesystem. A
                // non-canonical path is never owned by anyone — skip it here, before it is ever
                // handed to storage.
                if (!UploadPaths.IsCanonical(rel))
                {
                    skipped++;
                    skippedIds.Add(row.Id);
                    continue;
                }

                var isOwned =
                    row.OwnerId is Guid ownerGuid
                    && rel.StartsWith($"uploads/{ownerGuid:N}/", StringComparison.Ordinal);
                var isLegacyGlobal =
                    !isOwned
                    && rel.StartsWith("uploads/global/", StringComparison.Ordinal)
                    && row.CreatedAt < LegacyGlobalCutoff;

                if (!isOwned && !isLegacyGlobal)
                {
                    skipped++;
                    skippedIds.Add(row.Id);
                    continue;
                }

                try
                {
                    var size = await storage.SizeAsync(rel);

                    if (dryRun)
                    {
                        wouldDelete++;
                        wouldBytes += size;
                        // DB-16 review fix #10 (nit): count files with size > 0 separately from
                        // rows, matching the real (non-dry-run) branch's filesDeleted semantics.
                        if (size > 0)
                            wouldDeleteFiles++;
                        continue;
                    }

                    await storage.DeleteAsync(rel);
                    // DB-16 review fix #2: stamp only on a CONFIRMED false — null (unresolvable)
                    // must never be treated as "gone".
                    var exists = await storage.ExistsAsync(rel);
                    if (exists == false)
                    {
                        stampIds.Add(row.Id);
                        if (size > 0)
                            filesDeleted++;
                        bytesDeleted += size;
                    }
                    else
                    {
                        failures++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failures++;
                }
            }

            if (stampIds.Count > 0)
            {
                // Set-based: updated_at/updated_by are NOT touched (a tracked Update would write
                // updated_by = Guid.Empty, AppDbContext.cs:420 — Opus DB-16 #8).
                await db
                    .Comments.IgnoreQueryFilters()
                    .Where(c => stampIds.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ScreenshotPurgedAt, nowUtc), ct);
                commentsPurged += stampIds.Count;
            }

            if (rows.Count < o.BatchSize)
                break;
        }

        if (skippedIds.Count > 0)
        {
            log.LogWarning(
                "Retention: {Count} deleted comment(s) name a screenshot outside their workspace folder or in an unknown shape; ids {Ids} (first 20); left untouched",
                skippedIds.Count,
                string.Join(", ", skippedIds.Take(20))
            );
        }

        var mode = dryRun ? "DRY RUN (nothing deleted)" : "purged";
        var loggedComments = dryRun ? wouldDelete : commentsPurged;
        var loggedFiles = dryRun ? wouldDeleteFiles : filesDeleted;
        var loggedBytes = dryRun ? wouldBytes : bytesDeleted;
        log.LogInformation(
            "Retention: deleted-comment screenshots {Mode} — {Comments} comment(s), {Files} file(s), {Bytes} bytes, {Failures} failure(s), {Skipped} skipped (foreign/unknown path), cutoff {Cutoff:u}",
            mode,
            loggedComments,
            loggedFiles,
            loggedBytes,
            failures,
            skipped,
            cutoff
        );

        return new ScreenshotPurgeResult(
            commentsPurged,
            filesDeleted,
            bytesDeleted,
            failures,
            skipped,
            wouldDelete,
            wouldBytes
        );
    }

    internal static async Task<UploadSweepResult> OrphanSweepAsync(
        AppDbContext db,
        IFileStorage storage,
        IUploadSigner signer,
        RetentionOptions o,
        DateTime nowUtc,
        ILogger log,
        CancellationToken ct
    )
    {
        var dryRun = o.ScreenshotPurgeDryRun;

        if (o.UploadOrphanGraceHours <= 0)
        {
            log.LogInformation("Retention: uploads orphan sweep skipped (grace period 0)");
            return new UploadSweepResult(0, 0, 0, 0, 0, 0, 0, 0, dryRun);
        }

        var graceCutoff = nowUtc.AddHours(-o.UploadOrphanGraceHours);
        var segments = await storage.ListOwnerSegmentsAsync();

        var segmentsProcessed = 0;
        var scannedFiles = 0;
        long scannedBytes = 0;
        var orphans = 0;
        long orphanBytes = 0;
        var young = 0;
        var failures = 0;
        var skippedSegments = 0;

        // DB-16 review fix #3 (MEDIUM — orchestrator 2026-09-23: protect by path across owners).
        // A single GLOBAL protected set of canonical decoded paths, built from every live-or-unpurged
        // comment's screenshot regardless of that row's OwnerId — streamed by id batches (bounded
        // memory), same technique the old "global"-only special case used (Opus DB-16 #3). The
        // safer rule: a path is protected as long as ANY row still names it, even a row whose
        // OwnerId doesn't match the path's own owner segment (a forged/foreign reference must never
        // make the sweep MORE aggressive against the real owner's file). Cross-owner mismatches are
        // counted and logged once (Warning) — they indicate a row worth investigating, not a
        // deletion to make.
        //
        // DB-16 review fix #7 (LOW): a referenced path that is not UploadPaths.IsCanonical can never
        // match a real disk path (LocalFileStorage.ListOwnerFilesAsync only ever yields canonical
        // relative paths) — drop it here rather than carry it in the set, and count it (Warning).
        var protectedPaths = new HashSet<string>(StringComparer.Ordinal);
        var crossOwnerMismatches = 0;
        var nonCanonicalReferenced = 0;
        {
            var lastId = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = await db
                    .Comments.IgnoreQueryFilters()
                    .Where(c =>
                        c.Id > lastId
                        && c.Element.ScreenshotUrl != null
                        && c.Element.ScreenshotUrl != ""
                        && (c.DeletedAt == null || c.ScreenshotPurgedAt == null)
                    )
                    .OrderBy(c => c.Id)
                    .Take(o.BatchSize)
                    .Select(c => new
                    {
                        c.Id,
                        c.OwnerId,
                        c.Element.ScreenshotUrl,
                    })
                    .ToListAsync(ct);

                if (batch.Count == 0)
                    break;
                lastId = batch[^1].Id;

                foreach (var b in batch)
                {
                    var rel = signer.ExtractRelPath(b.ScreenshotUrl!);
                    if (!UploadPaths.IsCanonical(rel))
                    {
                        nonCanonicalReferenced++;
                        continue;
                    }

                    var ownerSeg = rel.Split('/')[1];
                    var expectedSeg = b.OwnerId is Guid g ? g.ToString("N") : "global";
                    if (!string.Equals(ownerSeg, expectedSeg, StringComparison.Ordinal))
                        crossOwnerMismatches++;

                    protectedPaths.Add(rel);
                }

                if (batch.Count < o.BatchSize)
                    break;
            }
        }

        if (crossOwnerMismatches > 0)
        {
            log.LogWarning(
                "Retention: {Count} referenced screenshot(s) name a path outside their own row's OwnerId folder; protected anyway (path-based protection, orchestrator 2026-09-23)",
                crossOwnerMismatches
            );
        }
        if (nonCanonicalReferenced > 0)
        {
            log.LogWarning(
                "Retention: {Count} referenced screenshot(s) have a non-canonical path shape; ignored (can never match a disk path)",
                nonCanonicalReferenced
            );
        }

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();

            // DB-16 review fix #5 (LOW): Guid.TryParseExact("N") accepts upper-case hex too, but the
            // real folder name LocalFileStorage.SaveAsync writes is always the lower-case
            // Guid.ToString("N") form. Require the segment string to round-trip exactly — an
            // upper-case (or otherwise non-canonical) Guid-looking folder name is never treated as
            // a workspace folder.
            var isWorkspaceSegment =
                (Guid.TryParseExact(seg, "N", out var ownerId) && seg == ownerId.ToString("N"))
                || seg == "global";

            if (!isWorkspaceSegment)
            {
                skippedSegments++;
                log.LogDebug(
                    "Retention: uploads segment {Seg} is not a workspace folder; skipped",
                    seg
                );
                continue;
            }

            segmentsProcessed++;
            var filesInSegment = 0;

            // DB-16 review fix #4 (MEDIUM): isolate one owner folder's enumeration failure from the
            // rest of the sweep. Directory.EnumerateFiles/ListOwnerFilesAsync is lazy — a failure
            // deep in the tree (permissions, a broken symlink) surfaces on a MoveNext during THIS
            // loop, not when the sequence was created, so the try/catch has to wrap the consumption,
            // not just the call that produced it.
            try
            {
                await foreach (var f in storage.ListOwnerFilesAsync(seg))
                {
                    scannedFiles++;
                    scannedBytes += f.Bytes;
                    filesInSegment++;
                    if (filesInSegment % 100 == 0)
                        ct.ThrowIfCancellationRequested();

                    if (protectedPaths.Contains(f.RelativePath))
                        continue;

                    if (f.LastWriteUtc > graceCutoff)
                    {
                        young++;
                        continue;
                    }

                    if (dryRun)
                    {
                        orphans++;
                        orphanBytes += f.Bytes;
                        continue;
                    }

                    try
                    {
                        await storage.DeleteAsync(f.RelativePath);
                        // DB-16 review fix #2: only a confirmed `false` counts as deleted.
                        var exists = await storage.ExistsAsync(f.RelativePath);
                        if (exists == false)
                        {
                            orphans++;
                            orphanBytes += f.Bytes;
                        }
                        else
                        {
                            failures++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        failures++;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                log.LogWarning(
                    ex,
                    "Retention: enumerating uploads segment {Seg} failed; folder skipped this pass",
                    seg
                );
                continue;
            }

            // Empty-folder cleanup lives here (not inside LocalFileStorage.DeleteAsync — a delete
            // racing a concurrent SaveAsync could remove a folder mid-write, Opus DB-16 #6). Skipped
            // in dry-run.
            if (!dryRun)
            {
                try
                {
                    await storage.DeleteEmptyProjectFoldersAsync(seg, graceCutoff);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    failures++;
                }
            }
        }

        var mode = dryRun ? "DRY RUN (nothing deleted)" : "swept";
        log.LogInformation(
            "Retention: uploads volume {Mode} — {Segments} workspace folder(s), {Files} file(s), {Bytes} bytes scanned; {Orphans} orphan file(s) / {OrphanBytes} bytes deleted; {Young} unreferenced file(s) inside the {GraceHours} h grace window kept; {Failures} failure(s); {Skipped} non-workspace folder(s) skipped",
            mode,
            segmentsProcessed,
            scannedFiles,
            scannedBytes,
            orphans,
            orphanBytes,
            young,
            o.UploadOrphanGraceHours,
            failures,
            skippedSegments
        );

        return new UploadSweepResult(
            segmentsProcessed,
            scannedFiles,
            scannedBytes,
            orphans,
            orphanBytes,
            young,
            failures,
            skippedSegments,
            dryRun
        );
    }
}
