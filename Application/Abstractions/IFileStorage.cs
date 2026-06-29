namespace Pointer.Application.Abstractions;

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
    /// Never throws on a missing file.
    /// </summary>
    Task DeleteAsync(string relativePathOrUrl);

    /// <summary>
    /// Best-effort recursive delete of the entire owner folder at
    /// "wwwroot/uploads/{ownerSegment}/". Never throws on a missing directory.
    /// ownerSegment must be a tenant's PublicId formatted as "N" (no hyphens).
    /// </summary>
    Task DeleteOwnerFilesAsync(string ownerSegment);
}
