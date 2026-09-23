namespace Pointer.Application.Abstractions;

/// <summary>DB-16. One file under uploads/&lt;ownerSegment&gt;/, as listed per-owner-folder by IFileStorage.</summary>
public sealed record StoredFile(string RelativePath, long Bytes, DateTime LastWriteUtc);

public interface IFileStorage
{
    /// <summary>
    /// Persists the given content under the owner/project upload folder using a generated
    /// file name with the supplied extension. Returns the RELATIVE web path
    /// (e.g. "uploads/&lt;ownerSegment&gt;/&lt;project&gt;/&lt;file&gt;"); the caller composes the absolute URL.
    /// ownerSegment is the project OwnerId formatted as "N" (no hyphens), or "global" for super-admin-owned projects.
    /// </summary>
    Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension);

    /// <summary>
    /// Best-effort delete of a previously stored file, given its relative web path
    /// ("uploads/&lt;ownerSegment&gt;/&lt;project&gt;/&lt;file&gt;") or an absolute URL ending in that path.
    /// Never throws on a missing file. Accepts the signed-URL shape too (DB-16).
    /// </summary>
    Task DeleteAsync(string relativePathOrUrl);

    /// <summary>
    /// Best-effort recursive delete of the entire owner folder at
    /// "wwwroot/uploads/{ownerSegment}/". Never throws on a missing directory.
    /// ownerSegment must be a tenant's PublicId formatted as "N" (no hyphens).
    /// </summary>
    Task DeleteOwnerFilesAsync(string ownerSegment);

    /// <summary>DB-16. True when the relative path ("uploads/…") names an existing file. Default: false (test doubles).</summary>
    Task<bool> ExistsAsync(string relativePath) => Task.FromResult(false);

    /// <summary>DB-16. Size in bytes of the file, 0 when missing. Default: 0.</summary>
    Task<long> SizeAsync(string relativePath) => Task.FromResult(0L);

    /// <summary>DB-16. Every file under uploads/&lt;ownerSegment&gt;/ as (relativePath, bytes, lastWriteUtc). Default: empty.</summary>
    IAsyncEnumerable<StoredFile> ListOwnerFilesAsync(string ownerSegment) => EmptyAsync();

    /// <summary>DB-16. The first-level folder names under uploads/ (owner segments, plus non-workspace folders such as "branding"). Default: empty.</summary>
    Task<IReadOnlyList<string>> ListOwnerSegmentsAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    /// <summary>DB-16. Deletes uploads/&lt;ownerSegment&gt;/&lt;project&gt; when it is empty and older than olderThanUtc. Returns the count removed. Default: 0.</summary>
    Task<int> DeleteEmptyProjectFoldersAsync(string ownerSegment, DateTime olderThanUtc) => Task.FromResult(0);

    private static async IAsyncEnumerable<StoredFile> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}
