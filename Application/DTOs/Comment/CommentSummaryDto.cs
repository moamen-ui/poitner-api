using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Lean projection for ?view=summary (CommentService.ListSummaryAsync) — an AI-agent-facing escape
/// hatch that skips the heavy per-comment payload (element snapshot/styles/rules, replies,
/// page context) CommentListItemDto always carries. Same visibility rules as the full list (quick
/// access, private comments) — see CommentService.BuildCommentQuery.
/// </summary>
public class CommentSummaryDto
{
    public int Id { get; set; }
    public CommentStatus Status { get; set; }
    public EnvironmentTag Environment { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? Route { get; set; }
    public string? SourcePath { get; set; }
    public string? AuthorName { get; set; }

    /// <summary>BCP-47 primary tag detected client-side from the comment text, or null when the
    /// widget wasn't confident. See Comment.Language.</summary>
    public string? Language { get; set; }
}
