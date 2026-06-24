using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Upload;
using Pointer.Application.Response;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
public class UploadsController(IFileStorage fileStorage) : ControllerBase
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

        await using var stream = file.OpenReadStream();
        var relativePath = await fileStorage.SaveAsync(project, stream, extension.ToLowerInvariant());

        var fileName = Path.GetFileName(relativePath);
        var url = $"{Request.Scheme}://{Request.Host}/uploads/{project}/{fileName}";

        return Ok(Result<UploadResponse>.Success(new UploadResponse
        {
            Url = url,
            FileName = fileName,
            Size = file.Length,
            ContentType = file.ContentType
        }));
    }
}
