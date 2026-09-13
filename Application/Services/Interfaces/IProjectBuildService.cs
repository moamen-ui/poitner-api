using Pointer.Application.DTOs.Build;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProjectBuildService
{
    /// <summary>
    /// Records a deployed build for a project and marks the comments it carries live.
    /// </summary>
    Task<Result<ReportBuildResponse>> ReportAsync(string projectKey, ReportBuildRequest request);
}
