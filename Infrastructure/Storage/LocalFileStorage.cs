using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Storage;

public class LocalFileStorage(IWebHostEnvironment env, IUploadSigner signer) : IFileStorage
{
    private string WebRoot()
    {
        var webRoot = env.WebRootPath;
        return string.IsNullOrEmpty(webRoot) ? Path.Combine(env.ContentRootPath, "wwwroot") : webRoot;
    }

    private string UploadsRoot() => Path.GetFullPath(Path.Combine(WebRoot(), "uploads"));

    /// <summary>
    /// DB-16: decodes any of the stored shapes (signed URL / public URL / raw path) via
    /// <see cref="IUploadSigner.ExtractRelPath"/>, resolves it under wwwroot, and guards traversal —
    /// the same guard <c>DeleteAsync</c> always had, now shared by every single-file member.
    /// Returns false (with <paramref name="resolved"/> = "") when the path is empty, unparsable, or
    /// escapes wwwroot/uploads.
    ///
    /// DB-16 review fix #1 (BLOCKER): three independent guards, all required —
    ///   (1) <see cref="IUploadSigner.ExtractRelPath"/> applied to the already-decoded value must be
    ///       a no-op. A value that decodes differently a second time (a nested/double-encoded signed
    ///       URL) is refused outright rather than silently decoded again.
    ///   (2) The once-decoded value must be <see cref="UploadPaths.IsCanonical"/> — the exact
    ///       uploads/&lt;owner&gt;/&lt;project&gt;/&lt;file&gt; shape, with no dot-dot segment,
    ///       backslash, or leftover percent-encoding. This alone rejects every "escape the owner
    ///       prefix via traversal" vector, because a path a naive prefix check would call "owned"
    ///       can never also be canonical once it contains "..".
    ///   (3) Belt-and-braces: after <c>Path.GetFullPath</c> normalizes the combined path, re-express
    ///       the result relative to web root with forward slashes and require it be byte-for-byte
    ///       the canonical value we started with — so no OS-specific normalization quirk could have
    ///       taken us somewhere <see cref="UploadPaths.IsCanonical"/> didn't examine.
    /// </summary>
    private bool TryResolve(string relativePathOrUrl, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(relativePathOrUrl))
            return false;

        var decoded = signer.ExtractRelPath(relativePathOrUrl);

        // Guard (1): no second decode.
        if (signer.ExtractRelPath(decoded) != decoded)
            return false;

        // Guard (2): exact canonical shape.
        if (!UploadPaths.IsCanonical(decoded))
            return false;

        var webRoot = WebRoot();
        var fullPath = Path.Combine(webRoot, decoded.Replace('/', Path.DirectorySeparatorChar));
        var uploadsRoot = UploadsRoot();
        var candidate = Path.GetFullPath(fullPath);

        if (!candidate.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        // Guard (3): the normalized absolute path, re-expressed relative to web root, must be
        // exactly the canonical value — never a different path that merely stayed inside uploads/.
        var relFromRoot = Path.GetRelativePath(webRoot, candidate).Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(relFromRoot, decoded, StringComparison.Ordinal))
            return false;

        resolved = candidate;
        return true;
    }

    public async Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension)
    {
        var webRoot = WebRoot();

        var folder = Path.Combine(webRoot, "uploads", ownerSegment, project);
        Directory.CreateDirectory(folder);

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var fullPath = Path.Combine(folder, fileName);

        // DB-16 review fix #8: the orphan sweep's DeleteEmptyProjectFoldersAsync can race this call
        // — it may see this exact folder empty and remove it in the window between the
        // CreateDirectory above and the FileStream open below, since nothing has been written into
        // it yet. Retry the create-and-open once on IOException (the parent folder vanishing
        // mid-open surfaces as DirectoryNotFoundException on Windows but as a plain IOException,
        // e.g. errno EINVAL/ENOENT wrapped generically, on Linux/macOS — DirectoryNotFoundException
        // IS an IOException, so catching the base type is the portable choice; production runs in
        // Docker on Linux, DEPLOY.md). The FileStream constructor is what actually touches disk;
        // content is never read before it succeeds, so a retry from scratch is safe.
        FileStream stream;
        try
        {
            stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        }
        catch (IOException)
        {
            Directory.CreateDirectory(folder);
            stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        }

        await using (stream)
        {
            await content.CopyToAsync(stream);
        }

        // Relative web path; forward slashes for URL composition.
        return $"uploads/{ownerSegment}/{project}/{fileName}";
    }

    public Task DeleteAsync(string relativePathOrUrl)
    {
        try
        {
            if (TryResolve(relativePathOrUrl, out var resolved) && File.Exists(resolved))
                File.Delete(resolved);
        }
        catch { /* best-effort: ignore IO errors */ }

        return Task.CompletedTask;
    }

    public Task DeleteOwnerFilesAsync(string ownerSegment)
    {
        if (string.IsNullOrWhiteSpace(ownerSegment))
            return Task.CompletedTask;

        var uploadsRoot = UploadsRoot();
        var ownerDir = Path.GetFullPath(Path.Combine(uploadsRoot, ownerSegment));

        // Guard against path traversal: the resolved directory must stay directly under
        // wwwroot/uploads (one level deep — ownerSegment must not contain separators).
        if (!ownerDir.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Task.CompletedTask;

        try
        {
            if (Directory.Exists(ownerDir))
                Directory.Delete(ownerDir, recursive: true);
        }
        catch { /* best-effort: ignore IO errors */ }

        return Task.CompletedTask;
    }

    public Task<bool?> ExistsAsync(string relativePath)
    {
        if (!TryResolve(relativePath, out var resolved))
            return Task.FromResult<bool?>(null);
        return Task.FromResult<bool?>(File.Exists(resolved));
    }

    public Task<long> SizeAsync(string relativePath)
    {
        if (!TryResolve(relativePath, out var resolved))
            return Task.FromResult(0L);
        try
        {
            var info = new FileInfo(resolved);
            return Task.FromResult(info.Exists ? info.Length : 0L);
        }
        catch
        {
            return Task.FromResult(0L);
        }
    }

    public async IAsyncEnumerable<StoredFile> ListOwnerFilesAsync(string ownerSegment)
    {
        if (string.IsNullOrWhiteSpace(ownerSegment))
            yield break;

        var uploadsRoot = UploadsRoot();
        var ownerDir = Path.GetFullPath(Path.Combine(uploadsRoot, ownerSegment));
        if (!ownerDir.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            yield break;
        if (!Directory.Exists(ownerDir))
            yield break;

        await Task.CompletedTask;

        // DB-16 review fix #4: IgnoreInaccessible so one unreadable sub-entry doesn't blow up the
        // whole enumeration; the caller (OrphanSweepAsync) additionally wraps its consumption of
        // this sequence in a try/catch per owner folder, since Directory.EnumerateFiles is lazy —
        // an error deeper in the tree surfaces on a later MoveNext, not on this call.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(ownerDir, "*", options);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                    continue;
            }
            catch
            {
                continue;
            }

            var relFromUploads = Path.GetRelativePath(uploadsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return new StoredFile($"uploads/{relFromUploads}", info.Length, info.LastWriteTimeUtc);
        }
    }

    public Task<IReadOnlyList<string>> ListOwnerSegmentsAsync()
    {
        var uploadsRoot = UploadsRoot();
        if (!Directory.Exists(uploadsRoot))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        try
        {
            var segments = Directory
                .EnumerateDirectories(uploadsRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray();
            return Task.FromResult<IReadOnlyList<string>>(segments);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    public Task<int> DeleteEmptyProjectFoldersAsync(string ownerSegment, DateTime olderThanUtc)
    {
        if (string.IsNullOrWhiteSpace(ownerSegment))
            return Task.FromResult(0);

        var uploadsRoot = UploadsRoot();
        var ownerDir = Path.GetFullPath(Path.Combine(uploadsRoot, ownerSegment));
        if (!ownerDir.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return Task.FromResult(0);
        if (!Directory.Exists(ownerDir))
            return Task.FromResult(0);

        var removed = 0;
        IEnumerable<string> projectDirs;
        try
        {
            projectDirs = Directory.EnumerateDirectories(ownerDir).ToArray();
        }
        catch
        {
            return Task.FromResult(0);
        }

        foreach (var dir in projectDirs)
        {
            try
            {
                // Exactly one level below the owner folder (two below uploads/): never the owner
                // folder itself, never uploads/ (DB-16 §3.3 step 6).
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                    continue;
                if (Directory.GetLastWriteTimeUtc(dir) >= olderThanUtc)
                    continue;

                Directory.Delete(dir, recursive: false);
                removed++;
            }
            catch (IOException)
            {
                // A concurrent SaveAsync may have recreated the folder between the emptiness check
                // and the delete — best-effort, retried next pass.
            }
        }

        return Task.FromResult(removed);
    }
}
