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

    /// <summary>Admin: this tenant's Pending or ChangesRequested suggestions (its review queue),
    /// excluding those on soft-deleted projects. Pending first, then by CreatedAt desc.</summary>
    Task<Result<List<SuggestionResponse>>> ListPendingAsync();

    /// <summary>
    /// Admin: approve a Pending suggestion → mint a real project-scoped PredefinedAction and mark
    /// the suggestion Approved. Re-validates the target project still exists + is active (Conflict otherwise).
    /// </summary>
    Task<Result<SuggestionResponse>> ApproveAsync(int id);

    /// <summary>Admin: reject a Pending suggestion.</summary>
    Task<Result<SuggestionResponse>> RejectAsync(int id);

    /// <summary>Admin: send a Pending suggestion back to the submitter with feedback (Conflict if not
    /// Pending). Best-effort notifies the submitter.</summary>
    Task<Result<SuggestionResponse>> RequestChangesAsync(int id, RequestChangesRequest request);

    /// <summary>Any authenticated user: their own suggestions (all statuses), newest first.</summary>
    Task<Result<List<SuggestionResponse>>> ListMineAsync();

    /// <summary>The original submitter: edit + resubmit a suggestion an admin sent back
    /// (Status == ChangesRequested → Pending). Conflict otherwise; NotFound if not the caller's own.
    /// Best-effort notifies the tenant's admins.</summary>
    Task<Result<SuggestionResponse>> UpdateAsync(int id, UpdateSuggestionRequest request);
}
