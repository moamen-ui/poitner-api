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
    public string? CommitUrl { get; set; }
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

    /// <summary>Effective AI rules for this comment, ordered by strict priority: Workspace > Project > Personal.</summary>
    public List<AiRuleApplyDto> AiRules { get; set; } = new();

    /// <summary>
    /// Advisory: this text looked like it contained a credential or executable payload.
    /// </summary>
    /// <remarks>
    /// Populated ONLY for the widget and dashboard (the X-Pointer-Client header). Null — and so
    /// absent from the JSON — for every other caller, which is how the documented AI paths never
    /// see it. See R2-06 exposure rules.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasPayloadFlag { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PayloadFlags { get; set; }

}
