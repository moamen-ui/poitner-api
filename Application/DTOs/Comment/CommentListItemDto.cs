using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentListItemDto
{
    public int Id { get; set; }
    public CommentStatus Status { get; set; }
    public EnvironmentTag Environment { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool IsPrivate { get; set; }
    public Guid AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public string? AppliedByLabel { get; set; }
    public DateTime? EditedAt { get; set; }

    // Picked predefined action's visible label. The matching PickedActionPrompt is
    // DELIBERATELY absent from this class (LLM-only; see design blocker) — do not add it.
    public string? PickedActionText { get; set; }

    // Included so the list is self-contained: the web component needs Element to
    // place pins, and the AI fetch queue (same endpoint, ?status=ReadyToApply)
    // needs it to resolve source/CSS without a second lookup (DESIGN §7).
    public ElementCaptureDto Element { get; set; } = new();
    public List<ReplyResponse> Replies { get; set; } = new();
}
