using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentResponse
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

    // Picked predefined actions' visible labels (multi-select). The matching prompts are
    // DELIBERATELY absent from this class (LLM-only; see design blocker) — do not add them.
    public List<string> PickedActionTexts { get; set; } = new();

    public ElementCaptureDto Element { get; set; } = new();
    public List<ReplyResponse> Replies { get; set; } = new();

    /// <summary>"Report as a bug" checkbox state.</summary>
    public bool IsBugReport { get; set; }

    /// <summary>Embedded inline (this is a single-item response, so there's no dedup concern) — keeps
    /// this response self-contained. Null when no page context was captured for this comment.</summary>
    public PageContextDto? PageContext { get; set; }
}
