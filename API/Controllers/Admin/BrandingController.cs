using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

/// <summary>
/// Super-admin branding management endpoints.
/// GET    /api/admin/branding
/// PUT    /api/admin/branding
/// POST   /api/admin/branding/asset/{kind}
/// DELETE /api/admin/branding/asset/{kind}
/// </summary>
[ApiController]
[Route("api/admin/branding")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Branding")]
public class BrandingController(IBrandingService brandingService, IWebHostEnvironment env) : ControllerBase
{
    private const long MaxBytes = 1_048_576; // 1 MB

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/svg+xml",
        "image/webp",
        "image/jpeg"
    };

    private static readonly Dictionary<string, string> ContentTypeToExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "image/png",     ".png"  },
            { "image/svg+xml", ".svg"  },
            { "image/webp",    ".webp" },
            { "image/jpeg",    ".jpg"  },
        };

    [HttpGet]
    [ProducesResponseType(typeof(Result<BrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var publicBase    = $"{Request.Scheme}://{Request.Host}";
        var existingKinds = GetExistingKinds();
        var result        = await brandingService.GetAsync(publicBase, existingKinds);
        return Ok(result);
    }

    [HttpPut]
    [ProducesResponseType(typeof(Result<BrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] BrandingWriteDto dto)
    {
        var publicBase    = $"{Request.Scheme}://{Request.Host}";
        var existingKinds = GetExistingKinds();
        var result        = await brandingService.UpdateAsync(dto, publicBase, existingKinds);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    [HttpPost("asset/{kind}")]
    [RequestSizeLimit(MaxBytes)]
    [ProducesResponseType(typeof(Result<BrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> UploadAsset(string kind, IFormFile file)
    {
        if (!IBrandingService.ValidKinds.Contains(kind))
            return BadRequest(Result.Failure($"Invalid kind '{kind}'. Must be one of: {string.Join(", ", IBrandingService.ValidKinds)}."));

        if (file is null || file.Length == 0)
            return BadRequest(Result.Failure("A file is required."));

        if (file.Length > MaxBytes)
            return BadRequest(Result.Failure("File exceeds the 1 MB size limit."));

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest(Result.Failure("Unsupported content type. Allowed: png, svg, webp, jpeg."));

        if (!ContentTypeToExtension.TryGetValue(file.ContentType, out var ext))
            return BadRequest(Result.Failure("Could not determine file extension from content type."));

        var brandingDir = GetBrandingDirectory();
        Directory.CreateDirectory(brandingDir);

        // Remove any existing file for this kind (any extension).
        DeleteExistingKindFile(kind, brandingDir);

        var fileName = $"{kind.ToLowerInvariant()}{ext}";
        var fullPath = Path.Combine(brandingDir, fileName);

        await using var stream = file.OpenReadStream();
        await using var dest   = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        await stream.CopyToAsync(dest);

        await brandingService.BumpVersionAsync();

        var publicBase    = $"{Request.Scheme}://{Request.Host}";
        var existingKinds = GetExistingKinds();
        var response      = await brandingService.BuildResponseAsync(publicBase, existingKinds);
        return Ok(Result<BrandingResponse>.Success(response));
    }

    [HttpDelete("asset/{kind}")]
    [ProducesResponseType(typeof(Result<BrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteAsset(string kind)
    {
        if (!IBrandingService.ValidKinds.Contains(kind))
            return BadRequest(Result.Failure($"Invalid kind '{kind}'. Must be one of: {string.Join(", ", IBrandingService.ValidKinds)}."));

        var brandingDir = GetBrandingDirectory();
        DeleteExistingKindFile(kind, brandingDir);

        await brandingService.BumpVersionAsync();

        var publicBase    = $"{Request.Scheme}://{Request.Host}";
        var existingKinds = GetExistingKinds();
        var response      = await brandingService.BuildResponseAsync(publicBase, existingKinds);
        return Ok(Result<BrandingResponse>.Success(response));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private string GetBrandingDirectory()
    {
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            webRoot = Path.Combine(env.ContentRootPath, "wwwroot");
        return Path.Combine(webRoot, "uploads", "branding");
    }

    private string? ResolveAssetFilePath(string kind)
    {
        var dir = GetBrandingDirectory();
        var normalizedKind = kind.ToLowerInvariant();
        foreach (var ext in new[] { ".png", ".svg", ".webp", ".jpg" })
        {
            var candidate = Path.Combine(dir, $"{normalizedKind}{ext}");
            if (System.IO.File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private HashSet<string> GetExistingKinds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in IBrandingService.ValidKinds)
        {
            if (ResolveAssetFilePath(kind) is not null)
                result.Add(kind);
        }
        return result;
    }

    private static void DeleteExistingKindFile(string kind, string brandingDir)
    {
        if (!Directory.Exists(brandingDir)) return;
        var normalizedKind = kind.ToLowerInvariant();
        foreach (var ext in new[] { ".png", ".svg", ".webp", ".jpg" })
        {
            var path = Path.Combine(brandingDir, $"{normalizedKind}{ext}");
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best-effort */ }
        }
    }
}
