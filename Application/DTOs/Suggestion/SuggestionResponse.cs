using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Suggestion;

/// <summary>
/// Admin-facing suggestion for the review queue. Includes prompt (admin-only surface) plus the
/// target project's name/key and the suggester's display name for review context.
/// </summary>
public class SuggestionResponse
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string ProjectKey { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public SuggestionStatus Status { get; set; }
    public string? SuggestedByName { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Admin's feedback when Status == ChangesRequested. Null otherwise (including once
    /// the submitter has edited and resubmitted — it reverts to Pending and this clears).</summary>
    public string? AdminFeedback { get; set; }

    public DateTime? ReviewedAt { get; set; }
}
