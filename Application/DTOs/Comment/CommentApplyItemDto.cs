using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

/// <summary>
/// Admin-gated apply-queue export item (the .NET equivalent of a self-contained pending.json
/// entry). This is the ONLY comment DTO that carries the picked-action <c>Prompt</c> — it is
/// returned exclusively by the admin-scoped apply-queue endpoint so the apply-time LLM receives
/// the snapshotted prompts alongside the comment body/selectors.
///
/// The prompts are deliberately kept OFF <c>CommentResponse</c> and <c>CommentListItemDto</c>
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

    // Predefined-action snapshots (multi-select) — prompts included ONLY here (admin/AI apply path).
    public List<PickedActionDto> PickedActions { get; set; } = new();
}

public class PickedActionDto
{
    public string Text { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
}
