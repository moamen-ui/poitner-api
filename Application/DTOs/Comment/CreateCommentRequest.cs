using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CreateCommentRequest
{
    public string Body { get; set; } = string.Empty;
    public EnvironmentTag Environment { get; set; }
    public bool IsPrivate { get; set; }

    /// <summary>
    /// Optional picked predefined actions (multi-select). The server validates each is active +
    /// in-scope for the resolved project, then snapshots {text, prompt} onto the comment. Any
    /// invalid/out-of-scope id rejects the request (not silently dropped).
    /// </summary>
    public List<int>? PredefinedActionIds { get; set; }

    public ElementCaptureDto Element { get; set; } = new();

    /// <summary>"Report as a bug" checkbox state. Gates whether PageContext is persisted at all —
    /// see CommentService.CreateAsync.</summary>
    public bool IsBugReport { get; set; }

    /// <summary>Only meaningful when IsBugReport is true; ignored server-side otherwise regardless
    /// of what the client sends.</summary>
    public PageContextCaptureDto? PageContext { get; set; }

    /// <summary>
    /// BCP-47 primary tag the widget's rule-based/on-device detector assigned to the comment text
    /// (e.g. "ar"), or "unknown"/empty when not confident. Normalized server-side — see
    /// CommentService.NormalizeLanguage.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Optional admin-defined field values (R4-01): machine key → value. Validated server-side
    /// against the PROJECT's workspace definitions (CommentFieldService.ValidateValues); unknown,
    /// disabled or invalid entries reject the request rather than being silently dropped. Trimmed
    /// empty values are treated as unset.
    /// </summary>
    public Dictionary<string, string>? CustomFields { get; set; }
}
