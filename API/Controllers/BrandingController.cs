using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

/// <summary>
/// Public branding endpoints — no authentication required.
/// GET /api/branding              — current branding configuration
/// GET /api/branding/asset/{kind} — versioned asset file (long cache)
/// </summary>
[ApiController]
[AllowAnonymous]
[Tags("Branding")]
public class BrandingController(IBrandingService brandingService, IWebHostEnvironment env) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private static readonly Dictionary<string, string> ExtToContentType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { ".svg",  "image/svg+xml"  },
            { ".png",  "image/png"      },
            { ".webp", "image/webp"     },
            { ".jpg",  "image/jpeg"     },
            { ".jpeg", "image/jpeg"     },
        };

    [HttpGet("api/branding")]
    [ProducesResponseType(typeof(Result<BrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        var publicBase    = $"{Request.Scheme}://{Request.Host}";
        var existingKinds = GetExistingKinds();
        var result        = await brandingService.GetAsync(publicBase, existingKinds);
        return Ok(result);
    }

    /// <summary>
    /// Serves a branding asset file. The URL is versioned via ?v= so it can be cached
    /// aggressively. Returns 404 when no file has been uploaded for that kind.
    /// </summary>
    [HttpGet("api/branding/asset/{kind}")]
    public IActionResult GetAsset(string kind)
    {
        if (!IBrandingService.ValidKinds.Contains(kind))
            return NotFound(Result.NotFound("Unknown asset kind."));

        var filePath = ResolveAssetFilePath(kind);
        if (filePath is null)
            return NotFound(Result.NotFound("Asset not found."));

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!ExtToContentType.TryGetValue(ext, out var contentType))
            contentType = "application/octet-stream";

        // Long cache keyed by ?v= version query param.
        Response.Headers["Cache-Control"] = "public, max-age=31536000";

        return PhysicalFile(filePath, contentType);
    }

    // ── internal helpers ─────────────────────────────────────────────────────

    internal string GetBrandingDirectory()
    {
        var webRoot = env.WebRootPath;
        if (string.IsNullOrEmpty(webRoot))
            webRoot = Path.Combine(env.ContentRootPath, "wwwroot");
        return Path.Combine(webRoot, "uploads", "branding");
    }

    internal string? ResolveAssetFilePath(string kind)
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

    internal HashSet<string> GetExistingKinds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in IBrandingService.ValidKinds)
        {
            if (ResolveAssetFilePath(kind) is not null)
                result.Add(kind);
        }
        return result;
    }
}
