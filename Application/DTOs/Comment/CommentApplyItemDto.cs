using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Admin-gated apply-queue export item (the .NET equivalent of a self-contained pending.json
/// entry). This is the ONLY comment DTO that carries <see cref="PickedActionPrompt"/> — it is
/// returned exclusively by the admin-scoped apply-queue endpoint so the apply-time LLM receives
/// the snapshotted prompt alongside the comment body/selectors.
///
/// The prompt is deliberately kept OFF <c>CommentResponse</c> and <c>CommentListItemDto</c>
/// (the widget/stakeholder-facing DTOs) — see the design blocker.
/// </summary>
public class CommentApplyItemDto
{
    public int Id { get; set; }
    public CommentStatus Status { get; set; }
    public EnvironmentTag Environment { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public DateTime CreatedAt { get; set; }
    public ElementCaptureDto Element { get; set; } = new();
    public List<ReplyResponse> Replies { get; set; } = new();

    // Predefined-action snapshot — prompt included ONLY here (admin/AI apply path).
    public string? PickedActionText { get; set; }
    public string? PickedActionPrompt { get; set; }
}
