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
    /// </summary>
    private bool TryResolve(string relativePathOrUrl, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(relativePathOrUrl))
            return false;

        var decoded = signer.ExtractRelPath(relativePathOrUrl);
        var idx = decoded.IndexOf("uploads/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;
        var rel = decoded[idx..].Replace('\\', '/').TrimStart('/');

        var webRoot = WebRoot();
        var fullPath = Path.Combine(webRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        var uploadsRoot = UploadsRoot();
        var candidate = Path.GetFullPath(fullPath);

        if (!candidate.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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

        await using (var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write))
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

    public Task<bool> ExistsAsync(string relativePath)
    {
        var exists = TryResolve(relativePath, out var resolved) && File.Exists(resolved);
        return Task.FromResult(exists);
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

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(ownerDir, "*", SearchOption.AllDirectories);
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
