using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentFilter
{
    public CommentStatus? Status { get; set; }
    public EnvironmentTag? Environment { get; set; }
    /// <summary>true → only comments the payload detector flagged (R2-06). Dashboard filter; the
    /// flag itself is still only serialized for widget/dashboard callers.</summary>
    public bool? Flagged { get; set; }

    /// <summary>Deploy state of applied comments (R3-01). true → applied and a build carrying the
    /// fix was reported deployed; false → applied but not yet live. Null → no deploy filtering.</summary>
    public bool? Live { get; set; }

    /// <summary>Case-insensitive substring match on the comment body.</summary>
    public string? Search { get; set; }

    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>"summary" → CommentsController.List returns the lean CommentSummaryDto shape
    /// instead of the full CommentListItemDto (see CommentService.ListSummaryAsync). Any other
    /// value (including null/omitted) keeps today's full behavior — an AI-agent-only escape
    /// hatch, deliberately not modeled in the typed dashboard clients.</summary>
    public string? View { get; set; }
}
