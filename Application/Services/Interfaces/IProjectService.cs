using Pointer.Application.DTOs.Project;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProjectService
{
    Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request);
    Task<Result<List<ProjectResponse>>> ListAsync();
    Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request);

    /// <summary>
    /// Resolve a project id by key, lazily creating the project (active) if it does not yet
    /// exist — so a developer integrating an app via VITE_POINTER_PROJECT self-registers it.
    /// Returns Conflict if the project exists but was disabled by an admin.
    /// </summary>
    Task<Result<int>> EnsureAsync(string key);
}
