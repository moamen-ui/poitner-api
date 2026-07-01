using Pointer.Application.DTOs.Suggestion;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface ISuggestionService
{
    /// <summary>
    /// Any tenant member suggests a predefined action on a project they CANNOT edit. Cross-tenant
    /// target → NotFound. If the caller is admin or the project's creator (i.e. can edit it) →
    /// Failure with guidance (add it directly). Creates a Pending suggestion and best-effort emails
    /// the tenant's admins.
    /// </summary>
    Task<Result<SuggestionResponse>> SuggestAsync(int projectId, CreateSuggestionRequest request);

    /// <summary>Admin: this tenant's Pending suggestions (excludes those on soft-deleted projects).</summary>
    Task<Result<List<SuggestionResponse>>> ListPendingAsync();

    /// <summary>
    /// Admin: approve a Pending suggestion → mint a real project-scoped PredefinedAction and mark
    /// the suggestion Approved. Re-validates the target project still exists + is active (Conflict otherwise).
    /// </summary>
    Task<Result<SuggestionResponse>> ApproveAsync(int id);

    /// <summary>Admin: reject a Pending suggestion.</summary>
    Task<Result<SuggestionResponse>> RejectAsync(int id);
}
