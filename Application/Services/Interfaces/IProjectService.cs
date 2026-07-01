using Pointer.Application.DTOs.Project;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IProjectService
{
    Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest request);
    Task<Result<List<ProjectResponse>>> ListAsync();
    Task<Result<ProjectResponse>> UpdateAsync(int id, UpdateProjectRequest request);

    /// <summary>
    /// Delete a project with per-caller rules (all enforced server-side; client hints are advisory):
    ///   - Admin → cascade soft-delete the project + its comments + replies + predefined actions +
    ///     suggestions (keyed strictly off ProjectId, never OwnerId).
    ///   - Owner (CreatedBy == caller) with 0 comments → soft-delete the project + its actions +
    ///     suggestions.
    ///   - Non-owner → Forbidden. Owner with comments → Conflict. Missing → NotFound.
    /// </summary>
    Task<Result> DeleteAsync(int id);

    /// <summary>
    /// STRICT resolver: resolve a project id by (Key + current tenant OwnerId).
    /// Projects must be pre-defined in the dashboard — this NO LONGER lazily self-creates.
    ///   - missing  → NotFound  (widget hides silently on 404; do NOT create)
    ///   - disabled → Conflict  (distinct from "not configured" — the widget hides silently on 409)
    ///   - active   → Success(id)
    ///
    /// Other project-key resolution paths (intentionally left AS-IS for now — the
    /// "projects must be pre-defined" guarantee currently holds for comment posting only):
    ///   - UploadsController.Upload (API/Controllers/UploadsController.cs:53) — resolves via the
    ///     EF query filter, already returns NotFound for an unknown key (never creates).
    ///   - AuthService register (Application/.../AuthService.cs:149) — anonymous + IgnoreQueryFilters
    ///     to bind a pending user to the project's tenant; still creates no project.
    ///   - DemoService provisioning — creates a demo project explicitly; not a resolve path.
    /// </summary>
    Task<Result<int>> EnsureAsync(string key);
}
