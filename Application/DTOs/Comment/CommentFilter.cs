using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentFilter
{
    public CommentStatus? Status { get; set; }
    public EnvironmentTag? Environment { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;

    /// <summary>"summary" → CommentsController.List returns the lean CommentSummaryDto shape
    /// instead of the full CommentListItemDto (see CommentService.ListSummaryAsync). Any other
    /// value (including null/omitted) keeps today's full behavior — an AI-agent-only escape
    /// hatch, deliberately not modeled in the typed dashboard clients.</summary>
    public string? View { get; set; }
}
