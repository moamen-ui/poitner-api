using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Export;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers;

[ApiController]
[Authorize]
public class ExportImportController(IExportImportService exportImportService) : ControllerBase
{
    // Open Decision #4: hard-coded 10 MB cap for v1 (enforced before deserialization).
    private const int MaxImportFileSizeBytes = 10_485_760;

    // Export file JSON: indented, raw Unicode (no \uXXXX escaping). Nulls are kept so the
    // documented schema (incl. element.screenshot_url = null) is emitted verbatim.
    private static readonly JsonSerializerOptions ExportJsonOptions =
        new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

    // Import binds via [FromBody] ExportFileDto — ASP.NET's default JSON is case-insensitive and
    // honors the DTOs' [JsonPropertyName] snake_case keys, and ignores unknown fields (future minor
    // schema bumps). The 10 MB cap is enforced by [RequestSizeLimit] on the import actions.

    // -------------------------------------------------------------------------
    // EXPORT — read-only, available to any authenticated user in the tenant.
    // -------------------------------------------------------------------------

    /// <summary>Download a portable snapshot of one project's comments.</summary>
    [HttpGet("api/projects/{key}/export")]
    [ProducesResponseType(typeof(ExportFileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportProject(string key, [FromQuery] ExportQueryParams query)
    {
        var result = await exportImportService.ExportProjectAsync(key, query.ToOptions());
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        if (!result.IsSuccess)
            return BadRequest(result);
        return ExportFile(result.Data!, $"pointer-export-{key}-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    /// <summary>Download a portable snapshot of every comment in the caller's workspace.</summary>
    [HttpGet("api/export")]
    [ProducesResponseType(typeof(ExportFileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportWorkspace([FromQuery] ExportQueryParams query)
    {
        var result = await exportImportService.ExportWorkspaceAsync(query.ToOptions());
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        if (!result.IsSuccess)
            return BadRequest(result);
        return ExportFile(
            result.Data!,
            $"pointer-export-workspace-{DateTime.UtcNow:yyyyMMdd}.json"
        );
    }

    // -------------------------------------------------------------------------
    // IMPORT — bulk write, admin-only (Open Decision #3).
    // -------------------------------------------------------------------------

    /// <summary>
    /// Import an export file into a specific project. Accepts the schema JSON directly
    /// (Content-Type: application/json) or a multipart upload with a "file" field.
    /// </summary>
    [HttpPost("api/projects/{key}/import")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportProject(string key, [FromBody] ExportFileDto file)
    {
        if (file == null)
            return BadRequest(Result.Failure(MessageKeys.ExportImport.InvalidJson));

        var result = await exportImportService.ImportProjectAsync(key, file);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    /// <summary>
    /// Bulk import: routes each comment to the project named by its project_key field.
    /// </summary>
    [HttpPost("api/import")]
    [Authorize(Policy = Policies.Admin)]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxImportFileSizeBytes)]
    [ProducesResponseType(typeof(ImportResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportWorkspace([FromBody] ExportFileDto file)
    {
        if (file == null)
            return BadRequest(Result.Failure(MessageKeys.ExportImport.InvalidJson));

        var result = await exportImportService.ImportWorkspaceAsync(file);
        if (result.IsNotFound)
            return NotFound(result);
        if (result.IsConflict)
            return Conflict(result);
        return result.IsSuccess ? Ok(result) : BadRequest(result);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private FileResult ExportFile(ExportFileDto file, string fileName)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, ExportJsonOptions);
        return File(bytes, "application/json", fileName);
    }
}
