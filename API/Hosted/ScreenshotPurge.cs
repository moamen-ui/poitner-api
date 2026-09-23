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
                        continue;
                    }

                    await storage.DeleteAsync(rel);
                    var gone = !await storage.ExistsAsync(rel);
                    if (gone)
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
        var loggedFiles = dryRun ? wouldDelete : filesDeleted;
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

        // The "global" segment's reference set cannot be built with an OwnerId predicate (legacy
        // uploads have no owning workspace) — stream every comment with a screenshot in id batches
        // and decode each URL IN MEMORY (Opus DB-16 #3). A SQL Contains("uploads/global/") is wrong:
        // stored signed URLs are percent-encoded (p=uploads%2Fglobal%2F…), so the substring never matches.
        HashSet<string>? globalReferenced = null;
        if (segments.Contains("global"))
        {
            globalReferenced = new HashSet<string>(StringComparer.Ordinal);
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
                    .Select(c => new { c.Id, c.Element.ScreenshotUrl })
                    .ToListAsync(ct);

                if (batch.Count == 0)
                    break;
                lastId = batch[^1].Id;

                foreach (var b in batch)
                {
                    var rel = signer.ExtractRelPath(b.ScreenshotUrl!);
                    if (rel.StartsWith("uploads/global/", StringComparison.Ordinal))
                        globalReferenced.Add(rel);
                }

                if (batch.Count < o.BatchSize)
                    break;
            }
        }

        foreach (var seg in segments)
        {
            ct.ThrowIfCancellationRequested();

            HashSet<string> referenced;
            if (Guid.TryParseExact(seg, "N", out var ownerId))
            {
                referenced = (
                    await db
                        .Comments.IgnoreQueryFilters()
                        .Where(c =>
                            c.OwnerId == ownerId
                            && c.Element.ScreenshotUrl != null
                            && c.Element.ScreenshotUrl != ""
                            && (c.DeletedAt == null || c.ScreenshotPurgedAt == null)
                        )
                        .Select(c => c.Element.ScreenshotUrl!)
                        .ToListAsync(ct)
                )
                    .Select(signer.ExtractRelPath)
                    .ToHashSet(StringComparer.Ordinal);
            }
            else if (seg == "global" && globalReferenced != null)
            {
                referenced = globalReferenced;
            }
            else
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

            await foreach (var f in storage.ListOwnerFilesAsync(seg))
            {
                scannedFiles++;
                scannedBytes += f.Bytes;
                filesInSegment++;
                if (filesInSegment % 100 == 0)
                    ct.ThrowIfCancellationRequested();

                if (referenced.Contains(f.RelativePath))
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
                    if (!await storage.ExistsAsync(f.RelativePath))
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
