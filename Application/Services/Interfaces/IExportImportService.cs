using Pointer.Application.DTOs.Export;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IExportImportService
{
    /// <summary>
    /// Export every comment (and reply) of a single project, scoped to the caller's tenant by
    /// the EF global query filter. Returns NotFound if the project does not exist, Conflict if
    /// it was disabled.
    /// </summary>
    Task<Result<ExportFileDto>> ExportProjectAsync(string projectKey, ExportOptions options);

    /// <summary>Export every comment in the caller's tenant (all projects).</summary>
    Task<Result<ExportFileDto>> ExportWorkspaceAsync(ExportOptions options);

    /// <summary>
    /// Import comments from an export file into a specific project. Validates the schema,
    /// re-attributes authors to the importing user, stamps OwnerId from the TARGET project
    /// (never from the JSON), drops screenshots, and preserves original created_at timestamps.
    /// Admin-only at the controller layer.
    /// </summary>
    Task<Result<ImportResultDto>> ImportProjectAsync(string projectKey, ExportFileDto file);

    /// <summary>
    /// Bulk import: routes each comment to the project named by its <c>project_key</c> field
    /// (lazily creating it via <c>EnsureAsync</c>, consistent with the live create path).
    /// </summary>
    Task<Result<ImportResultDto>> ImportWorkspaceAsync(ExportFileDto file);
}
