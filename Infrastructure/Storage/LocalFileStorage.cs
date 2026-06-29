using Microsoft.AspNetCore.Hosting;
using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Storage;

public class LocalFileStorage(IWebHostEnvironment env) : IFileStorage
{
    public async Task<string> SaveAsync(string ownerSegment, string project, Stream content, string extension)
    {
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            webRoot = Path.Combine(env.ContentRootPath, "wwwroot");

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
        if (string.IsNullOrWhiteSpace(relativePathOrUrl))
            return Task.CompletedTask;

        // Accept an absolute URL or a relative path; reduce to the part after "uploads/".
        var idx = relativePathOrUrl.IndexOf("uploads/", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return Task.CompletedTask;
        var rel = relativePathOrUrl[idx..].Replace('\\', '/').TrimStart('/');

        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            webRoot = Path.Combine(env.ContentRootPath, "wwwroot");

        var fullPath = Path.Combine(webRoot, rel.Replace('/', Path.DirectorySeparatorChar));

        // Guard against path traversal: the resolved path must stay under wwwroot/uploads.
        var uploadsRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads"));
        var resolved = Path.GetFullPath(fullPath);
        try
        {
            if (resolved.StartsWith(uploadsRoot, StringComparison.Ordinal) && File.Exists(resolved))
                File.Delete(resolved);
        }
        catch { /* best-effort: ignore IO errors */ }

        return Task.CompletedTask;
    }
}
