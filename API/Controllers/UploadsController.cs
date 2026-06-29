using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Upload;
using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
public class UploadsController(
    IFileStorage fileStorage,
    IUnitOfWork unitOfWork,
    IUploadSigner uploadSigner,
    IWebHostEnvironment env) : ControllerBase
{
    private const long MaxBytes = 5_242_880; // 5 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif"
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif"
    };

    private static readonly Dictionary<string, string> ExtensionContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".png",  "image/png" },
        { ".jpg",  "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".webp", "image/webp" },
        { ".gif",  "image/gif" },
    };

    private static readonly Regex ProjectPattern = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    [HttpPost("api/uploads")]
    [RequestSizeLimit(MaxBytes)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string project)
    {
        if (file is null || file.Length == 0)
            return BadRequest(Result.Failure("A file is required."));

        if (file.Length > MaxBytes)
            return BadRequest(Result.Failure("File exceeds the 5 MB size limit."));

        if (string.IsNullOrWhiteSpace(project) || !ProjectPattern.IsMatch(project))
            return BadRequest(Result.Failure("Invalid project. Only letters, digits, '.', '_' and '-' are allowed."));

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest(Result.Failure("Unsupported content type. Only PNG, JPEG, WEBP and GIF images are allowed."));

        // Never trust the client filename for the saved path; only read its extension for validation.
        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
            return BadRequest(Result.Failure("Unsupported file extension. Only .png, .jpg, .jpeg, .webp and .gif are allowed."));

        // Ownership check: resolve the project through the EF global query filter.
        // A scoped admin only sees their own projects; super admin sees all.
        var keyNormalized = project.Trim().ToLower();
        var projectEntity = await unitOfWork.Repository<Project>()
            .Query()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
            .Select(p => new { p.OwnerId })
            .FirstOrDefaultAsync();

        if (projectEntity is null)
            return NotFound(Result.Failure("Project not found"));

        // Derive owner segment: super-admin-owned (null OwnerId) → "global"; otherwise TenantId as N-format GUID.
        var ownerSegment = projectEntity.OwnerId.HasValue
            ? projectEntity.OwnerId.Value.ToString("N")
            : "global";

        await using var stream = file.OpenReadStream();
        var relativePath = await fileStorage.SaveAsync(ownerSegment, keyNormalized, stream, extension.ToLowerInvariant());

        var fileName = Path.GetFileName(relativePath);
        // Return a short-lived HMAC-signed URL instead of a permanent public path.
        var url = uploadSigner.SignedUrl(relativePath);

        return Ok(Result<UploadResponse>.Success(new UploadResponse
        {
            Url = url,
            FileName = fileName,
            Size = file.Length,
            ContentType = file.ContentType
        }));
    }

    /// <summary>
    /// Serves a previously uploaded file only when the HMAC signature is valid and not expired.
    /// Anonymous access is allowed (no bearer token required) so that &lt;img src&gt; works in the browser.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("api/uploads/file")]
    public IActionResult GetFile([FromQuery] string p, [FromQuery] long exp, [FromQuery] string sig)
    {
        if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(sig))
            return NotFound();

        if (!uploadSigner.Validate(p, exp, sig))
            return NotFound();

        // Resolve the physical path under wwwroot.
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            webRoot = Path.Combine(env.ContentRootPath, "wwwroot");

        // Normalise: strip any leading slash; forward-slashes only.
        var rel = p.Replace('\\', '/').TrimStart('/');
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

        // Path-traversal guard: the resolved path MUST stay under wwwroot/uploads.
        var uploadsRoot = Path.GetFullPath(Path.Combine(webRoot, "uploads"));
        if (!fullPath.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return NotFound();

        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (!ExtensionContentTypes.TryGetValue(ext, out var contentType))
            contentType = "application/octet-stream";

        // Cache-Control: private — the URL itself is the time-limited token.
        Response.Headers["Cache-Control"] = "private, max-age=3600";

        return PhysicalFile(fullPath, contentType);
    }
}
