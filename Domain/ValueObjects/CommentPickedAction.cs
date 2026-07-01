namespace Pointer.Domain.ValueObjects;

/// <summary>
/// Snapshot of a predefined action picked on a comment (multi-select). Text is the label the
/// stakeholder saw; Prompt is the LLM instruction (apply-time only, never exposed to the browser).
/// Stored as a JSON collection on the comment so editing/deleting the action definition later
/// never rewrites history.
/// </summary>
public class CommentPickedAction
{
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
}
