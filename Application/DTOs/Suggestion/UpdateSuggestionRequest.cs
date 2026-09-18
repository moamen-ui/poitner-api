namespace Pointer.Application.DTOs.Suggestion;

/// <summary>Body for the submitter editing and resubmitting a suggestion an admin sent back
/// (Status == ChangesRequested).</summary>
public class UpdateSuggestionRequest
{
    /// <summary>Proposed visible label (bounded ≤256).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Proposed LLM prompt.</summary>
    public string Prompt { get; set; } = string.Empty;
}
