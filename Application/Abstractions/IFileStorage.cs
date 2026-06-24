namespace Pointer.Application.Abstractions;

public interface IFileStorage
{
    /// <summary>
    /// Persists the given content under the project's upload folder using a generated
    /// file name with the supplied extension. Returns the RELATIVE web path
    /// (e.g. "uploads/&lt;project&gt;/&lt;file&gt;"); the caller composes the absolute URL.
    /// </summary>
    Task<string> SaveAsync(string project, Stream content, string extension);

    /// <summary>
    /// Best-effort delete of a previously stored file, given its relative web path
    /// ("uploads/&lt;project&gt;/&lt;file&gt;") or an absolute URL ending in that path.
    /// Never throws on a missing file.
    /// </summary>
    Task DeleteAsync(string relativePathOrUrl);
}
