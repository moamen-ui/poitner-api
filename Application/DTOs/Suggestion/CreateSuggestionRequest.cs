namespace Pointer.Application.DTOs.Suggestion;

/// <summary>Body for suggesting a predefined action on a project the caller cannot edit.</summary>
public class CreateSuggestionRequest
{
    /// <summary>Proposed visible label (bounded ≤256).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Proposed LLM prompt.</summary>
    public string Prompt { get; set; } = string.Empty;
}
