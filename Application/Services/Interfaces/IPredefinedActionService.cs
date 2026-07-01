using Pointer.Application.DTOs.PredefinedAction;
using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

public interface IPredefinedActionService
{
    // ── Tenant-wide admin CRUD (ProjectId == null) ────────────────────────────
    Task<Result<List<PredefinedActionResponse>>> ListTenantAsync();
    Task<Result<PredefinedActionResponse>> CreateTenantAsync(CreatePredefinedActionRequest request);
    Task<Result<PredefinedActionResponse>> UpdateAsync(int id, UpdatePredefinedActionRequest request);
    Task<Result> DeleteAsync(int id);

    /// <summary>
    /// Widget-facing effective set for a project (by key), scoped to the current tenant (JWT) and
    /// user: active + not-deleted + (ProjectId null OR ProjectId == P) + (UserId null OR UserId == U).
    /// Returns id + text ONLY — never the prompt. NotFound/Conflict propagate the strict project
    /// resolver's contract (missing → NotFound, disabled → Conflict).
    /// </summary>
    Task<Result<List<PredefinedActionOption>>> GetEffectiveForProjectAsync(string projectKey, Guid userId);

    /// <summary>
    /// Validate that <paramref name="predefinedActionId"/> is active and in-scope for the resolved
    /// project (owner T, requesting user U), and return the snapshot entity if valid. Used by
    /// comment-create to snapshot {text, prompt}. Returns null when invalid/out-of-scope.
    /// </summary>
    Task<PredefinedAction?> ResolveInScopeAsync(int predefinedActionId, int projectId, Guid? ownerId, Guid userId);
}
