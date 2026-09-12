namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// A reply as the AI-facing apply queue sees it: author, text, timestamp. Nothing else.
/// </summary>
/// <remarks>
/// The apply queue previously embedded <c>ReplyResponse</c>, the same DTO the dashboard and widget
/// use. That coupling meant any field added for humans — the R2-06 payload flags being exactly such
/// a field — appeared in the payload an AI tool consumes. An advisory flag reaching the apply prompt
/// would itself become an injection surface: one more attacker-controlled sentence.
///
/// A separate, deliberately small type is what keeps the two audiences from drifting into each
/// other. Adding a field here is a decision; inheriting one is an accident.
/// </remarks>
public class ApplyReplyDto
{
    public string AuthorName { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
